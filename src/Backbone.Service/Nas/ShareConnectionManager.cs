using Backbone.Contracts;
using Backbone.Core.Storage;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// Gestor único de las conexiones SMB del servicio (revisión de seguridad de HU-05, MEDIA-1): una conexión por raíz de recurso
/// con recuento de referencias. Toda conexión al NAS (la prueba de conexión y, después, las copias, restauraciones y
/// mantenimiento) pasa por aquí, nunca por <see cref="IShareConnector"/> directamente.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="AcquireAsync"/> reutiliza la conexión viva (o en curso) de la raíz o la crea; solo la última liberación la
/// cierra (<c>WNetCancelConnection2</c>), así que una prueba nunca corta la conexión de una copia.</item>
/// <item>Windows solo admite <b>un usuario por servidor</b> en la sesión de inicio del servicio, y con una sesión SMB viva no
/// vuelve a comprobar la contraseña. Por eso la reutilización de una conexión se decide por la <b>identidad</b> de la
/// credencial guardada (raíz más <see cref="NasCredential.UpdatedUtc"/>, que cambia con cada guardado), <b>nunca por su
/// contenido</b>: comparar usuario y contraseña (con una huella o como sea) sería un oráculo de contraseñas para quien pudiera
/// probar credenciales contra una conexión viva (revisión final de seguridad de HU-05, M-1). Una credencial explícita no
/// guardada (<see cref="NasCredential.IsExplicit"/>) nunca comparte una conexión ajena. Si ya hay otra conexión con ese
/// servidor (con cualquier recurso) que no se puede compartir, la respuesta es <see cref="BackboneErrorCode.NasBusy"/> sin
/// llamar al sistema, coincida o no el secreto.</item>
/// <item><b>Desconexión pendiente</b> (§8.2 B-1): entre la última liberación y el final de <c>WNetCancelConnection2</c> la raíz
/// queda "cerrando"; un <see cref="AcquireAsync"/> de esa raíz espera a que termine y reconecta. Si no, la cancelación
/// pendiente borraría el registro de uso de la conexión nueva y su E/S saldría con la cuenta del equipo (§7.5 A-3).</item>
/// <item><b>Parada</b> (§8.2 B-2): <see cref="Close"/> hace que todo <see cref="AcquireAsync"/> posterior falle; las conexiones
/// que sigan abiertas o en curso se cierran a la fuerza (<see cref="ForceDisconnectAllAsync"/>).</item>
/// </list>
/// </remarks>
internal sealed partial class ShareConnectionManager(IShareConnector connector, ILogger<ShareConnectionManager> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // Raíces cuya última liberación aún no ha terminado de desconectar (B-1): la tarea termina al volver WNetCancelConnection2.
    private readonly Dictionary<string, Task> _closing = new(StringComparer.OrdinalIgnoreCase);
    private bool _closed;

    // Raíces cuya última desconexión (sin forzar) ha fallado (BAJA-N2): la conexión puede seguir viva en el sistema, sin
    // dueño. Se reintenta a la fuerza al volver a conectar la raíz o al soltar otra, y siempre en la parada.
    private readonly HashSet<string> _failedDisconnects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raíces con conexión viva o en curso.</summary>
    public IReadOnlyList<string> ActiveShareRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Keys];
            }
        }
    }

    /// <summary>Servidor de una raíz normalizada <c>\\servidor\recurso</c>.</summary>
    public static string ServerOf(string shareRoot) => shareRoot[2..shareRoot.IndexOf('\\', 2)];

    /// <summary>
    /// Conexión con <see cref="NasCredential.ShareRoot"/>. La credencial sigue siendo del llamante, que no la desecha hasta que
    /// esta tarea termina. El plazo lo pone el llamante; si abandona la tarea, debe desechar la concesión si acaba bien.
    /// </summary>
    /// <exception cref="ShareException">
    /// La conexión falla, o ya hay otra conexión con ese servidor que no se puede compartir con esta credencial
    /// (<see cref="BackboneErrorCode.NasBusy"/>).
    /// </exception>
    public async Task<ShareLease> AcquireAsync(NasCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var identity = new CredentialIdentity(credential.IsExplicit, credential.UpdatedUtc);
        var server = ServerOf(credential.ShareRoot);
        Entry? entry = null;
        var retried = false;
        while (entry is null)
        {
            Task? closing;
            TaskCompletionSource? retry = null;
            lock (_gate)
            {
                if (_closed)
                {
                    throw new ShareException(BackboneErrorCode.Interrupted, 0);
                }

                foreach (var other in _entries.Values)
                {
                    if (string.Equals(other.Server, server, StringComparison.OrdinalIgnoreCase)
                        && !(string.Equals(other.ShareRoot, credential.ShareRoot, StringComparison.OrdinalIgnoreCase)
                             && other.Identity.CanShareWith(identity)))
                    {
                        // M-1: la respuesta no depende de si el usuario o la contraseña coinciden: nunca se comparan.
                        LogBusy(logger, UntrustedText.Describe(server));
                        throw new ShareException(BackboneErrorCode.NasBusy, 0);
                    }
                }

                if (_closing.TryGetValue(credential.ShareRoot, out closing))
                {
                    // B-1: hay una desconexión de esta raíz en curso; conectar ahora la dejaría sin conexión al terminar.
                }
                else if (!retried && !_entries.ContainsKey(credential.ShareRoot) && _failedDisconnects.Remove(credential.ShareRoot))
                {
                    // BAJA-N2: la última desconexión de esta raíz falló y no hay concesiones vivas: se reintenta a la fuerza antes
                    // de conectar (con la raíz "cerrando", como en B-1, para que nadie conecte entretanto).
                    retried = true;
                    retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _closing[credential.ShareRoot] = retry.Task;
                }
                else if (_entries.TryGetValue(credential.ShareRoot, out var existing))
                {
                    entry = existing;
                    entry.References++;
                }
                else
                {
                    entry = new Entry(credential.ShareRoot, server, identity, connector.ConnectAsync(credential, CancellationToken.None));
                    _entries.Add(credential.ShareRoot, entry);
                }
            }

            if (retry is not null)
            {
                await ForceFailedAsync(credential.ShareRoot, retry).ConfigureAwait(false);
            }
            else if (closing is not null)
            {
                await closing.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                ct.ThrowIfCancellationRequested();
            }
        }

        try
        {
            var connection = await entry.Connecting.WaitAsync(ct).ConfigureAwait(false);
            return CreateLease(entry, connection);
        }
        catch
        {
            // Si se abandona por cancelación, la referencia se suelta cuando la conexión termine (y se cierra si era la última).
            if (entry.Connecting.IsCompleted)
            {
                await ReleaseAsync(entry).ConfigureAwait(false);
            }
            else
            {
                _ = entry.Connecting.ContinueWith(_ => ReleaseAsync(entry), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }

            throw;
        }
    }

    private ShareLease CreateLease(Entry entry, IShareConnection connection)
    {
        var lease = new ShareLease(this, entry, connection);
        lock (_gate)
        {
            entry.Leases.Add(lease);

            // Una concesión que se crea tras cerrar a la fuerza la conexión (la conexión terminó de abrirse después) nace revocada.
            if (entry.ForceClosed)
            {
                lease.Revoke();
            }
        }

        return lease;
    }

    /// <summary>
    /// Parada del servicio (B-2): a partir de ahora todo <see cref="AcquireAsync"/> falla con
    /// <see cref="BackboneErrorCode.Interrupted"/>.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    /// <summary>Espera a las desconexiones en curso (<c>WNetCancelConnection2</c> de la última liberación de cada raíz).</summary>
    public async Task WhenReleasesCompleteAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _closing.Values];
        }

        foreach (var task in pending)
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Cierra a la fuerza las conexiones que sigan abiertas, en curso o desconectándose (parada del servicio). Las que aún
    /// se están conectando se cierran cuando terminen de conectar (B-2).
    /// </summary>
    public async Task ForceDisconnectAllAsync(CancellationToken ct)
    {
        var now = new List<string>();
        var withLeases = new List<(string Root, int Leases)>();
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                withLeases.Add((entry.ShareRoot, entry.References));

                // M-A: antes de tocar la red, las concesiones quedan revocadas: nada que las use vuelve a hacer E/S (sin el
                // registro de uso saldría con la cuenta del equipo, §7.5 A-3), ni siquiera si ya iba a soltar el bloqueo.
                entry.ForceClosed = true;
                foreach (var lease in entry.Leases)
                {
                    lease.Revoke();
                }

                if (entry.Connecting.IsCompleted)
                {
                    now.Add(entry.ShareRoot);
                }
                else
                {
                    var root = entry.ShareRoot;
                    _ = entry.Connecting.ContinueWith(
                        _ => ForceDisconnectAsync(root, CancellationToken.None),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default).Unwrap();
                }
            }

            foreach (var root in _closing.Keys.Concat(_failedDisconnects))
            {
                if (!now.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    now.Add(root);
                }
            }
        }

        // BAJA-N1: cerrar a la fuerza con una concesión viva corta la E/S de quien la tiene; queda constancia.
        foreach (var (root, leases) in withLeases)
        {
            LogForcingWithLeases(logger, UntrustedText.Describe(root), leases);
        }

        foreach (var root in now)
        {
            await ForceDisconnectAsync(root, ct).ConfigureAwait(false);
        }
    }

    internal async Task ReleaseAsync(Entry entry)
    {
        TaskCompletionSource closing;
        lock (_gate)
        {
            entry.References--;
            if (entry.References > 0)
            {
                return;
            }

            if (_entries.TryGetValue(entry.ShareRoot, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.ShareRoot);
            }

            // B-1: hasta que termine la desconexión, la raíz sigue "cerrando" y nadie reconecta.
            closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing[entry.ShareRoot] = closing.Task;
        }

        var disconnectFailed = false;
        try
        {
            if (entry.Connecting.IsCompletedSuccessfully)
            {
                var connection = await entry.Connecting.ConfigureAwait(false); // ya terminada: no espera
                disconnectFailed = true; // por si DisposeAsync lanza
                await connection.DisposeAsync().ConfigureAwait(false);
                disconnectFailed = connection.DisconnectFailed;
            }
            else if (entry.Connecting.IsFaulted)
            {
                _ = entry.Connecting.Exception; // observada
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_closing.TryGetValue(entry.ShareRoot, out var task) && ReferenceEquals(task, closing.Task))
                {
                    _closing.Remove(entry.ShareRoot);
                }

                if (disconnectFailed)
                {
                    // BAJA-N2: la conexión puede seguir viva; se recuerda para reintentar y para la parada.
                    _failedDisconnects.Add(entry.ShareRoot);
                    LogDisconnectPending(logger, UntrustedText.Describe(entry.ShareRoot));
                }
            }

            closing.SetResult();
        }

        await RetryFailedDisconnectsAsync(except: entry.ShareRoot).ConfigureAwait(false);
    }

    /// <summary>
    /// Reintenta a la fuerza las desconexiones fallidas de las demás raíces que no tengan concesiones vivas ni una desconexión
    /// en curso (BAJA-N2); no hace nada con el gestor cerrado (la parada las cierra todas).
    /// </summary>
    private async Task RetryFailedDisconnectsAsync(string except)
    {
        List<(string Root, TaskCompletionSource Closing)> retries = [];
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var root in _failedDisconnects.ToArray())
            {
                if (string.Equals(root, except, StringComparison.OrdinalIgnoreCase)
                    || _entries.ContainsKey(root)
                    || _closing.ContainsKey(root))
                {
                    continue;
                }

                _failedDisconnects.Remove(root);
                var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closing[root] = closing.Task;
                retries.Add((root, closing));
            }
        }

        foreach (var (root, closing) in retries)
        {
            await ForceFailedAsync(root, closing).ConfigureAwait(false);
        }
    }

    /// <summary>Fuerza la desconexión de una raíz ya marcada "cerrando" con <paramref name="closing"/> y termina ese estado.</summary>
    private async Task ForceFailedAsync(string root, TaskCompletionSource closing)
    {
        try
        {
            await ForceDisconnectAsync(root, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_closing.TryGetValue(root, out var task) && ReferenceEquals(task, closing.Task))
                {
                    _closing.Remove(root);
                }
            }

            closing.SetResult();
        }
    }

    private async Task ForceDisconnectAsync(string root, CancellationToken ct)
    {
        try
        {
            await connector.ForceDisconnectAsync(root, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _failedDisconnects.Remove(root);
            }

            LogForced(logger, UntrustedText.Describe(root));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                _failedDisconnects.Add(root);
            }

            LogForceFailed(logger, UntrustedText.Describe(root), ex.GetType().Name, ex.HResult);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ya hay otra conexión con el servidor {Server} que no se puede compartir con esta credencial: NasBusy sin conectar (Windows admite un solo usuario por servidor).")]
    private static partial void LogBusy(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parada del servicio: se cierra a la fuerza la conexión con {ShareRoot} con {Leases} concesiones vivas; su E/S en curso fallará.")]
    private static partial void LogForcingWithLeases(ILogger logger, string shareRoot, int leases);

    [LoggerMessage(Level = LogLevel.Warning, Message = "La desconexión de {ShareRoot} ha fallado: la conexión puede seguir abierta; se reintentará a la fuerza.")]
    private static partial void LogDisconnectPending(ILogger logger, string shareRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión con {ShareRoot} cerrada a la fuerza en la parada del servicio.")]
    private static partial void LogForced(ILogger logger, string shareRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo cerrar a la fuerza la conexión con {ShareRoot} ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogForceFailed(ILogger logger, string shareRoot, string exceptionType, int hResult);

    /// <summary>Conexión de una raíz y sus referencias (protegidas por el cerrojo del gestor).</summary>
    internal sealed class Entry(string shareRoot, string server, CredentialIdentity identity, Task<IShareConnection> connecting)
    {
        public string ShareRoot { get; } = shareRoot;

        public string Server { get; } = server;

        public CredentialIdentity Identity { get; } = identity;

        public Task<IShareConnection> Connecting { get; } = connecting;

        public int References { get; set; } = 1;

        /// <summary>Concesiones vivas de la conexión (protegidas por el cerrojo del gestor).</summary>
        public HashSet<ShareLease> Leases { get; } = [];

        /// <summary>La parada del servicio ha cerrado a la fuerza esta conexión: toda concesión nueva nace revocada.</summary>
        public bool ForceClosed { get; set; }
    }

    internal void Unregister(ShareLease lease, Entry entry)
    {
        lock (_gate)
        {
            entry.Leases.Remove(lease);
        }
    }
}

/// <summary>
/// Identidad de la credencial con la que se hizo una conexión (M-1): si es una credencial explícita no guardada y, si es la
/// guardada, su <see cref="NasCredential.UpdatedUtc"/>. Nunca lleva ni se calcula del usuario o la contraseña.
/// </summary>
internal readonly record struct CredentialIdentity(bool IsExplicit, DateTimeOffset UpdatedUtc)
{
    /// <summary>Una credencial explícita nunca comparte conexión; las guardadas, solo con el mismo <see cref="UpdatedUtc"/>.</summary>
    public bool CanShareWith(CredentialIdentity other) => !IsExplicit && !other.IsExplicit && UpdatedUtc == other.UpdatedUtc;
}

/// <summary>
/// Uso de una conexión del <see cref="ShareConnectionManager"/>; desecharla suelta la referencia (idempotente).
/// </summary>
/// <remarks>
/// <b>Revocación</b> (revisión de seguridad de HU-14, M-A): <see cref="Revoke"/> la pone la parada del servicio
/// (<see cref="ShareConnectionManager.ForceDisconnectAllAsync"/>) y <see cref="DisposeAsync"/>, siempre <b>antes</b> de
/// <c>WNetCancelConnection2</c>. Mientras dura, <see cref="Protect"/> da un almacenamiento que, desde ese momento, lanza
/// <see cref="BackboneErrorCode.Interrupted"/> en toda operación sin tocar la red: sin el registro de uso de la conexión, la
/// E/S saldría con la cuenta del equipo (§7.5 A-3).
/// </remarks>
internal sealed class ShareLease : IAsyncDisposable
{
    private readonly ShareConnectionManager _manager;
    private readonly ShareConnectionManager.Entry _entry;
    private int _released;
    private int _revoked;

    internal ShareLease(ShareConnectionManager manager, ShareConnectionManager.Entry entry, IShareConnection connection)
    {
        _manager = manager;
        _entry = entry;
        Connection = connection;
    }

    public IShareConnection Connection { get; }

    /// <summary>La conexión se ha soltado o cerrado a la fuerza: no se puede hacer más E/S con esta concesión.</summary>
    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    /// <summary>Revoca la concesión (idempotente); toda E/S posterior de <see cref="Protect"/> falla sin tocar la red.</summary>
    public void Revoke() => Volatile.Write(ref _revoked, 1);

    /// <summary>Lanza <see cref="BackboneErrorCode.Interrupted"/> si la concesión está revocada.</summary>
    public void ThrowIfRevoked()
    {
        if (IsRevoked)
        {
            throw new ShareException(BackboneErrorCode.Interrupted, 0);
        }
    }

    /// <summary>Envuelve el almacenamiento del repositorio para que deje de hacer E/S cuando se revoque la concesión.</summary>
    public IRepositoryStorage Protect(IRepositoryStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return new RevocableRepositoryStorage(storage, this);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // M-A: revocada antes de que ReleaseAsync llegue a WNetCancelConnection2; lo que siga en segundo plano ya no toca la red.
        Revoke();
        _manager.Unregister(this, _entry);
        return new ValueTask(_manager.ReleaseAsync(_entry));
    }
}
