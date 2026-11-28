using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Os;

/// <summary>Plazos de <see cref="VssSnapshotter"/>.</summary>
internal sealed record VssOptions
{
    /// <summary>
    /// Plazo de la creación de un conjunto (recoger los escritores, congelarlos y crear las instantáneas). Pasado, se cancela
    /// (<c>IVssAsync.Cancel</c>), se suelta todo y la copia sigue en vivo.
    /// </summary>
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Plazo de la eliminación de un conjunto. Pasado, se suelta el objeto de VSS (que elimina las auto-liberables) y queda registrado para el siguiente arranque.</summary>
    public TimeSpan ReleaseTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// <see cref="IVolumeSnapshotter"/> con la API COM de VSS (<c>IVssBackupComponents</c>, §8.2 <c>VssSnapshotter</c>, RF-22).
/// </summary>
/// <remarks>
/// <para><b>API elegida</b>: <c>IVssBackupComponents</c> y no WMI <c>Win32_ShadowCopy.Create</c>, porque (1) crea <b>un conjunto</b>
/// con todos los volúmenes en el mismo instante (HU-15 criterio 5); WMI crea una instantánea por llamada, cada una en su
/// conjunto; (2) el contexto <c>VSS_CTX_BACKUP</c> es <b>no persistente y se libera sola</b>: si el servicio muere, VSS elimina
/// las instantáneas al cerrarse el objeto del solicitante, mientras que las de WMI son persistentes y se acumulan; (3) cuenta
/// con los escritores (consistencia de bases de datos como SQL Server LocalDB) y <c>VSS_BT_COPY</c> no altera su historial.</para>
/// <para><b>Reconocer las instantáneas de Backbone</b> (criterio 3): VSS no ofrece una etiqueta de propietario que un tercero
/// no pueda copiar, así que Backbone registra en <c>data\vss-snapshots.json</c> (<see cref="VssSnapshotRegistry"/>, ADR-14) el
/// conjunto y los identificadores (GUID de VSS) de lo que crea, <b>antes</b> de confirmarlo (<c>DoSnapshotSet</c>), y lo borra
/// cuando lo elimina. El arranque (<see cref="DeleteOrphansAsync"/>) elimina solo identificadores del registro que no sean de
/// la instancia actual, y antes de borrar cada uno comprueba que su conjunto y su volumen coinciden con lo registrado. Una
/// instantánea que no está en el registro (de otro programa, o de las "Versiones anteriores" de Windows) no se consulta ni se toca.</para>
/// <para>Todas las llamadas COM se hacen en <see cref="VssWorker"/>, un hilo propio con COM inicializado y el token del proceso.</para>
/// </remarks>
internal sealed partial class VssSnapshotter : IVolumeSnapshotter, IAsyncDisposable, IDisposable
{
    private readonly IVssBackupFactory _factory;
    private readonly VssSnapshotRegistry _registry;
    private readonly TimeProvider _time;
    private readonly VssOptions _options;
    private readonly ILogger<VssSnapshotter> _logger;
    private readonly VssWorker _worker;

    public VssSnapshotter(
        IVssBackupFactory factory,
        VssSnapshotRegistry registry,
        TimeProvider time,
        VssOptions options,
        ILogger<VssSnapshotter> logger,
        bool initializeCom = true,
        Guid? instanceId = null)
    {
        _factory = factory;
        _registry = registry;
        _time = time;
        _options = options;
        _logger = logger;
        _worker = new VssWorker(initializeCom);
        InstanceId = instanceId ?? Guid.NewGuid();
    }

    /// <summary>Identificador de esta ejecución del servicio: lo que crea lleva este propietario y no lo toca la limpieza del arranque.</summary>
    public Guid InstanceId { get; }

    /// <summary>Un conjunto creado en VSS: el objeto de VSS que lo mantiene vivo y lo que hace falta para eliminarlo.</summary>
    private sealed class Prepared(IVssBackup backup, Guid setId, List<(Guid Id, string Volume)> items)
    {
        public IVssBackup Backup { get; } = backup;

        public Guid SetId { get; } = setId;

        public List<(Guid Id, string Volume)> Items { get; } = items;

        /// <summary><c>DoSnapshotSet</c> ya se ha llamado: puede haber instantáneas en el sistema.</summary>
        public bool Committed { get; set; }
    }

    // ---- Creación ----

    public async Task<IVolumeSnapshotSet> CreateAsync(IReadOnlyCollection<string> volumes, CancellationToken cancellationToken)
    {
        var requested = Normalize(volumes);
        cancellationToken.ThrowIfCancellationRequested();

        using var timeout = new CancellationTokenSource(_options.CreateTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;

        Prepared prepared;
        try
        {
            prepared = await _worker.RunAsync(() => Prepare(requested, token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, "prepare the shadow copy set", cancellationToken);
        }

        List<VolumeSnapshot> snapshots;
        try
        {
            // Registrar antes de confirmar: nunca existe una instantánea de Backbone que el registro no conozca.
            await _registry.AddAsync(InstanceId, prepared.SetId, prepared.Items.Select(i => (i.Id, i.Volume)), token).ConfigureAwait(false);
            snapshots = await _worker.RunAsync(() => Commit(prepared, token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AbandonAsync(prepared).ConfigureAwait(false);
            throw Translate(ex, "create the shadow copies", cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelada justo al terminar: no se entrega nada. Quien llama no recibe el conjunto, así que se elimina aquí.
            await AbandonAsync(prepared).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        LogCreated(_logger, prepared.SetId, snapshots.Count);
        return new SnapshotSet(this, prepared, snapshots);
    }

    /// <summary>Normaliza los volúmenes pedidos: <c>\\?\Volume{guid}\</c>, sin repetir.</summary>
    /// <exception cref="ArgumentException">Hay un volumen que no tiene esa forma o no se pide ninguno (error de programación).</exception>
    private static List<string> Normalize(IReadOnlyCollection<string> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        var result = new List<string>(volumes.Count);
        foreach (var volume in volumes)
        {
            if (!IsVolumeRoot(volume))
            {
                throw new ArgumentException("A volume must be written as \\\\?\\Volume{guid}\\.", nameof(volumes));
            }

            if (!result.Contains(volume, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(volume);
            }
        }

        return result.Count > 0 ? result : throw new ArgumentException("At least one volume is required.", nameof(volumes));
    }

    /// <summary><c>\\?\Volume{guid}\</c> exactamente.</summary>
    internal static bool IsVolumeRoot(string? volume) =>
        volume is { Length: 49 }
        && volume.StartsWith(@"\\?\", StringComparison.Ordinal)
        && volume[^1] == '\\'
        && SourceOpen.IsVolumeGuid(volume.AsSpan(4, 44));

    /// <summary>Hilo de VSS: crea el objeto, recoge los escritores y añade los volúmenes (aún no hay instantáneas).</summary>
    private Prepared Prepare(List<string> volumes, CancellationToken token)
    {
        var backup = _factory.Create();
        try
        {
            token.ThrowIfCancellationRequested();
            backup.SetContext(NativeMethods.Vss.ContextBackup);
            backup.SetBackupState();
            backup.GatherWriterMetadata(token);
            var setId = backup.StartSnapshotSet();
            var items = new List<(Guid Id, string Volume)>(volumes.Count);
            foreach (var volume in volumes)
            {
                token.ThrowIfCancellationRequested();
                items.Add((backup.AddToSnapshotSet(volume), volume));
            }

            return new Prepared(backup, setId, items);
        }
        catch
        {
            TryAbort(backup);
            backup.Dispose();
            throw;
        }
    }

    /// <summary>Hilo de VSS: prepara los escritores, confirma el conjunto y lee las propiedades de cada instantánea.</summary>
    private static List<VolumeSnapshot> Commit(Prepared prepared, CancellationToken token)
    {
        var backup = prepared.Backup;
        backup.PrepareForBackup(token);
        token.ThrowIfCancellationRequested();
        prepared.Committed = true;
        backup.DoSnapshotSet(token);

        var snapshots = new List<VolumeSnapshot>(prepared.Items.Count);
        foreach (var (id, volume) in prepared.Items)
        {
            var properties = backup.GetSnapshotProperties(id);
            if (properties.SnapshotSetId != prepared.SetId || !SameVolume(properties.OriginalVolume, volume))
            {
                throw new VolumeSnapshotException("VSS returned a shadow copy that does not match the one requested.");
            }

            snapshots.Add(new VolumeSnapshot(volume, ToDevicePath(properties.DeviceObject), properties.CreatedUtc));
        }

        return snapshots;
    }

    /// <summary>
    /// Pasa el dispositivo que da VSS (<c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>, o <c>\Device\…</c>) a la raíz
    /// que admite la lectura de orígenes. Solo se acepta una instantánea (B-2).
    /// </summary>
    internal static string ToDevicePath(string deviceObject)
    {
        ArgumentNullException.ThrowIfNull(deviceObject);
        var trimmed = deviceObject.TrimEnd('\\');
        string device;
        if (trimmed.StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.OrdinalIgnoreCase))
        {
            device = trimmed[@"\\?\GLOBALROOT\Device\".Length..];
        }
        else if (trimmed.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            device = trimmed[@"\Device\".Length..];
        }
        else
        {
            throw new VolumeSnapshotException("VSS returned a shadow copy device that is not recognised.");
        }

        if (device.Contains('\\') || !SourceOpen.IsAllowedDevice(device, out var isShadowCopy) || !isShadowCopy)
        {
            throw new VolumeSnapshotException("VSS returned a shadow copy device that is not recognised.");
        }

        return @"\\?\GLOBALROOT\Device\" + device;
    }

    private static bool SameVolume(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deshace un conjunto que no llegó a entregarse: lo elimina si llegó a confirmarse y lo quita del registro si está limpio.</summary>
    private async Task AbandonAsync(Prepared prepared)
    {
        var clean = await ReleaseCoreAsync(prepared).ConfigureAwait(false);
        if (clean)
        {
            await _registry.RemoveAsync([prepared.SetId], CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// La excepción que ve quien llama: la cancelación del llamante se propaga tal cual; todo lo demás (VSS, plazo, registro)
    /// es <see cref="VolumeSnapshotException"/>, que hace que la copia siga en vivo.
    /// </summary>
    private Exception Translate(Exception ex, string action, CancellationToken caller)
    {
        if (ex is OperationCanceledException && caller.IsCancellationRequested)
        {
            return new OperationCanceledException(caller);
        }

        LogFailed(_logger, action, ex.GetType().Name, ex.HResult);
        var message = ex switch
        {
            VssException vss => $"VSS could not {action}: {vss.Operation} failed (HRESULT 0x{unchecked((uint)vss.HResult):X8}).",
            OperationCanceledException => $"VSS did not {action} in time.",
            VolumeSnapshotException v => v.Message,
            IOException or UnauthorizedAccessException or UntrustedFileException => $"VSS could not {action}: the list of shadow copies could not be saved ({ex.GetType().Name}).",
            _ => $"VSS could not {action} ({ex.GetType().Name}).",
        };
        return new VolumeSnapshotException(message, ex.HResult, ex);
    }

    // ---- Eliminación ----

    /// <summary>
    /// Elimina el conjunto en el hilo de VSS y suelta el objeto. Devuelve si no queda nada que limpiar (eliminado, o nunca
    /// confirmado). No lanza.
    /// </summary>
    private async Task<bool> ReleaseCoreAsync(Prepared prepared)
    {
        using var timeout = new CancellationTokenSource(_options.ReleaseTimeout, _time);
        try
        {
            return await _worker.RunAsync(() => Release(prepared, timeout.Token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(_logger, prepared.SetId, ex.GetType().Name, ex.HResult);
            return false;
        }
    }

    /// <summary>
    /// Hilo de VSS: <c>BackupComplete</c> (avisa a los escritores), <c>DeleteSnapshots</c> del conjunto y <c>Release</c> del
    /// objeto, que elimina además cualquier instantánea auto-liberable que quedara. Cada paso es independiente: el siguiente se
    /// hace aunque falle el anterior.
    /// </summary>
    private bool Release(Prepared prepared, CancellationToken token)
    {
        var clean = !prepared.Committed;
        try
        {
            if (prepared.Committed)
            {
                try
                {
                    prepared.Backup.BackupComplete(token);
                }
                catch (Exception ex) when (ex is VssException or OperationCanceledException)
                {
                    LogBackupCompleteFailed(_logger, prepared.SetId, ex.GetType().Name, ex.HResult);
                }

                try
                {
                    _ = prepared.Backup.DeleteSnapshots(prepared.SetId, isSet: true);
                    clean = true;
                }
                catch (VssException ex) when (ex.IsObjectNotFound)
                {
                    clean = true;
                }
                catch (VssException ex)
                {
                    LogDeleteFailed(_logger, prepared.SetId, ex.Operation, ex.HResult);
                }
            }
            else
            {
                TryAbort(prepared.Backup);
            }
        }
        finally
        {
            prepared.Backup.Dispose();
        }

        return clean;
    }

    private static void TryAbort(IVssBackup backup)
    {
        try
        {
            backup.AbortBackup();
        }
        catch (VssException)
        {
            // El objeto se suelta justo después: VSS descarta lo que no se hubiera confirmado.
        }
    }

    private sealed class SnapshotSet(VssSnapshotter owner, Prepared prepared, IReadOnlyList<VolumeSnapshot> snapshots) : IVolumeSnapshotSet
    {
        private readonly Lock _gate = new();
        private Task? _disposal;

        public IReadOnlyList<VolumeSnapshot> Snapshots { get; } = snapshots;

        public async ValueTask DisposeAsync()
        {
            Task disposal;
            lock (_gate)
            {
                _disposal ??= DisposeCoreAsync();
                disposal = _disposal;
            }

            await disposal.ConfigureAwait(false);
        }

        private async Task DisposeCoreAsync()
        {
            if (await owner.ReleaseCoreAsync(prepared).ConfigureAwait(false))
            {
                await owner._registry.RemoveAsync([prepared.SetId], CancellationToken.None).ConfigureAwait(false);
                LogDeleted(owner._logger, prepared.SetId);
            }
        }
    }

    // ---- Instantáneas huérfanas ----

    public async Task<int> DeleteOrphansAsync(CancellationToken cancellationToken)
    {
        var orphans = (await _registry.LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.Owner != InstanceId)
            .ToList();
        if (orphans.Count == 0)
        {
            return 0;
        }

        LogOrphansFound(_logger, orphans.Count);
        using var timeout = new CancellationTokenSource(_options.ReleaseTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        SweepResult result;
        try
        {
            result = await _worker.RunAsync(() => Sweep(orphans, linked.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // VSS no disponible, plazo vencido…: el registro no cambia y se reintenta en el siguiente arranque.
            LogSweepFailed(_logger, ex.GetType().Name, ex.HResult);
            return 0;
        }

        await _registry.RemoveAsync(result.Resolved, CancellationToken.None).ConfigureAwait(false);
        return result.Deleted;
    }

    private sealed record SweepResult(IReadOnlyCollection<Guid> Resolved, int Deleted);

    /// <summary>
    /// Hilo de VSS. Para cada identificador <b>del registro</b>: si ya no existe, listo; si existe, solo se elimina cuando su
    /// conjunto y su volumen son los registrados. Nunca se enumeran ni se tocan otras instantáneas del sistema.
    /// </summary>
    private SweepResult Sweep(List<VssSnapshotSetRecord> orphans, CancellationToken token)
    {
        var resolved = new List<Guid>();
        var deleted = 0;
        using var backup = _factory.Create();
        backup.SetContext(NativeMethods.Vss.ContextAll);
        foreach (var set in orphans)
        {
            token.ThrowIfCancellationRequested();
            var done = true;
            foreach (var snapshot in set.Snapshots.OfType<VssSnapshotRecord>())
            {
                VssSnapshotProperties properties;
                try
                {
                    properties = backup.GetSnapshotProperties(snapshot.SnapshotId);
                }
                catch (VssException ex) when (ex.IsObjectNotFound)
                {
                    continue; // ya no existe (VSS elimina las no persistentes al morir el solicitante)
                }
                catch (VssException ex)
                {
                    LogDeleteFailed(_logger, set.SetId, ex.Operation, ex.HResult);
                    done = false;
                    continue;
                }

                if (properties.SnapshotSetId != set.SetId || !SameVolume(properties.OriginalVolume, snapshot.Volume))
                {
                    // No es lo que Backbone registró: no se toca, y se descarta la entrada para no volver a mirarla.
                    LogNotOurs(_logger, snapshot.SnapshotId);
                    continue;
                }

                try
                {
                    _ = backup.DeleteSnapshots(snapshot.SnapshotId, isSet: false);
                    deleted++;
                    LogOrphanDeleted(_logger, snapshot.SnapshotId, set.SetId);
                }
                catch (VssException ex) when (ex.IsObjectNotFound)
                {
                    // Desapareció entre medias.
                }
                catch (VssException ex)
                {
                    LogDeleteFailed(_logger, set.SetId, ex.Operation, ex.HResult);
                    done = false;
                }
            }

            if (done)
            {
                resolved.Add(set.SetId);
            }
        }

        return new SweepResult(resolved, deleted);
    }

    // ---- Parada ----

    public void Dispose() => _worker.Dispose();

    /// <summary>Termina lo que esté en cola (eliminaciones pendientes) y para el hilo de VSS.</summary>
    public ValueTask DisposeAsync()
    {
        _worker.Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conjunto de instantáneas VSS {SetId} creado ({Count} volúmenes).")]
    private static partial void LogCreated(ILogger logger, Guid setId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conjunto de instantáneas VSS {SetId} eliminado.")]
    private static partial void LogDeleted(ILogger logger, Guid setId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "VSS no ha podido {Action} ({ErrorType}, HRESULT {HResult:X8}); la copia sigue en vivo.")]
    private static partial void LogFailed(ILogger logger, string action, string errorType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido eliminar el conjunto de instantáneas VSS {SetId} ({ErrorType}, HRESULT {HResult:X8}); queda registrado y se intentará de nuevo al arrancar.")]
    private static partial void LogReleaseFailed(ILogger logger, Guid setId, string errorType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BackupComplete del conjunto VSS {SetId} ha fallado ({ErrorType}, HRESULT {HResult:X8}); se sigue con la eliminación.")]
    private static partial void LogBackupCompleteFailed(ILogger logger, Guid setId, string errorType, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "VSS {Operation} del conjunto {SetId} ha fallado (HRESULT {HResult:X8}); queda registrado y se intentará de nuevo al arrancar.")]
    private static partial void LogDeleteFailed(ILogger logger, Guid setId, string operation, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hay {Count} conjuntos de instantáneas VSS creados por Backbone que no se eliminaron; se eliminan ahora.")]
    private static partial void LogOrphansFound(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantánea VSS huérfana {SnapshotId} (conjunto {SetId}) eliminada.")]
    private static partial void LogOrphanDeleted(ILogger logger, Guid snapshotId, Guid setId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La instantánea VSS {SnapshotId} está en el registro pero su conjunto o su volumen no coinciden: no es de Backbone y no se toca.")]
    private static partial void LogNotOurs(ILogger logger, Guid snapshotId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se han podido eliminar las instantáneas VSS huérfanas ({ErrorType}, HRESULT {HResult:X8}); se reintentará al arrancar.")]
    private static partial void LogSweepFailed(ILogger logger, string errorType, int hResult);
}
