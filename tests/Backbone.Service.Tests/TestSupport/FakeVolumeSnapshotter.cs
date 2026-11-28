using System.Collections.Concurrent;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Un conjunto de instantáneas simulado (HU-15): anota cuándo se desecha.</summary>
internal sealed class FakeSnapshotSet(IReadOnlyList<VolumeSnapshot> snapshots, FakeVolumeSnapshotter owner) : IVolumeSnapshotSet
{
    private int _disposals;

    public IReadOnlyList<VolumeSnapshot> Snapshots { get; } = snapshots;

    /// <summary>Veces que se ha desechado.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    public bool IsDisposed => Disposals > 0;

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        if (owner.OnDispose is { } hook)
        {
            return new ValueTask(hook());
        }

        return owner.DisposeError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
    }
}

/// <summary>
/// Doble de <see cref="IVolumeSnapshotter"/> (HU-15): por defecto crea un conjunto con un dispositivo
/// <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy&lt;n&gt;</c> por volumen pedido y el mismo instante para todos; con
/// <see cref="CreateError"/> falla como VSS no disponible. <see cref="ToLive"/> traduce una ruta de la instantánea a la del
/// volumen en vivo, para que <see cref="FakeSourceFileSystem"/> sirva en una instantánea los mismos archivos que ya registra
/// "en vivo" sin tener que repetirlos.
/// </summary>
internal sealed class FakeVolumeSnapshotter : IVolumeSnapshotter
{
    private readonly ConcurrentQueue<FakeSnapshotSet> _sets = new();
    private int _next;
    private int _orphanCalls;

    /// <summary>Volúmenes pedidos en cada llamada a <see cref="CreateAsync"/>, en orden.</summary>
    public ConcurrentQueue<string[]> Requests { get; } = new();

    /// <summary>Si no es nulo, <see cref="CreateAsync"/> lo lanza (VSS no disponible: <see cref="VolumeSnapshotException"/>).</summary>
    public Exception? CreateError { get; set; }

    /// <summary>Si no es nulo, el desecho del conjunto lo lanza (después de anotarlo).</summary>
    public Exception? DisposeError { get; set; }

    /// <summary>Si no es nulo, el desecho del conjunto espera a esta tarea (p. ej. una eliminación que no vuelve).</summary>
    public Func<Task>? OnDispose { get; set; }

    /// <summary>Volúmenes que VSS no admite: pedir alguno falla como <see cref="VolumeSnapshotException"/> y no se crea nada.</summary>
    public HashSet<string> UnsupportedVolumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Se llama al crear, antes de entregar el conjunto: sirve para esperar o cancelar en ese punto.</summary>
    public Func<CancellationToken, Task>? OnCreate { get; set; }

    /// <summary>Si es verdad, el conjunto no incluye el último volumen pedido (conjunto incompleto).</summary>
    public bool OmitLastVolume { get; set; }

    /// <summary>Conjuntos creados, en orden.</summary>
    public IReadOnlyCollection<FakeSnapshotSet> Sets => _sets;

    /// <summary>Conjuntos creados y todavía sin desechar.</summary>
    public int ActiveSets => _sets.Count(s => !s.IsDisposed);

    /// <summary>Instante de creación de los conjuntos (el mismo para todas las instantáneas de cada uno).</summary>
    public DateTimeOffset CreatedUtc { get; set; } = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Cuántas veces se ha pedido la limpieza de huérfanas.</summary>
    public int OrphanCalls => Volatile.Read(ref _orphanCalls);

    /// <summary>Lo que devuelve <see cref="DeleteOrphansAsync"/>.</summary>
    public int OrphansToReport { get; set; }

    /// <summary>Si no es nulo, <see cref="DeleteOrphansAsync"/> lo lanza.</summary>
    public Exception? OrphanError { get; set; }

    public async Task<IVolumeSnapshotSet> CreateAsync(IReadOnlyCollection<string> volumes, CancellationToken cancellationToken)
    {
        Requests.Enqueue([.. volumes]);
        if (OnCreate is { } hook)
        {
            await hook(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (CreateError is { } error)
        {
            throw error;
        }

        if (volumes.FirstOrDefault(UnsupportedVolumes.Contains) is { } unsupported)
        {
            throw new VolumeSnapshotException($"VSS could not prepare the shadow copy set: AddToSnapshotSet failed (HRESULT 0x8004230C) for {unsupported}.");
        }

        var requested = OmitLastVolume ? volumes.Take(volumes.Count - 1) : volumes;
        var snapshots = requested
            .Select(volume => new VolumeSnapshot(volume, $@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy{Interlocked.Increment(ref _next)}", CreatedUtc))
            .ToList();
        var set = new FakeSnapshotSet(snapshots, this);
        _sets.Enqueue(set);
        return set;
    }

    public Task<int> DeleteOrphansAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _orphanCalls);
        return OrphanError is { } error ? Task.FromException<int>(error) : Task.FromResult(OrphansToReport);
    }

    /// <summary>
    /// Ruta en vivo equivalente a una de una instantánea de este doble: la raíz de su volumen (<c>X:\</c> si el GUID es el de
    /// <see cref="FakeLocalFileSystemInspector.FakeGuidPath"/>; si no, <c>\\?\Volume{guid}\</c>) más lo que sigue. Una ruta que
    /// no es de ninguna instantánea se devuelve igual.
    /// </summary>
    public string ToLive(string path)
    {
        foreach (var snapshot in _sets.SelectMany(s => s.Snapshots))
        {
            if (!path.StartsWith(snapshot.DevicePath, StringComparison.OrdinalIgnoreCase)
                || (path.Length > snapshot.DevicePath.Length && path[snapshot.DevicePath.Length] != '\\'))
            {
                continue;
            }

            var rest = path[snapshot.DevicePath.Length..].TrimStart('\\');
            var root = LiveRoot(snapshot.Volume);
            return rest.Length == 0 ? root : root + rest;
        }

        return path;
    }

    private static string LiveRoot(string volume)
    {
        // \\?\Volume{00000000-0000-0000-0000-0000000000XX}\ con XX = la letra en hexadecimal.
        var guid = volume.AsSpan(@"\\?\Volume{".Length, 36);
        if (guid[..34].SequenceEqual("00000000-0000-0000-0000-0000000000")
            && byte.TryParse(guid[34..], System.Globalization.NumberStyles.HexNumber, null, out var letter))
        {
            return $"{(char)letter}:\\";
        }

        return volume;
    }
}
