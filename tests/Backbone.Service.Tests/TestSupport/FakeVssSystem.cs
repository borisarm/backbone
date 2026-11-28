using System.Collections.Concurrent;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// VSS simulado (HU-15): las instantáneas que "existen" en el sistema (las de Backbone y las de otros programas) y un registro
/// de llamadas. Cada <see cref="IVssBackup"/> que entrega se comporta como el real: las instantáneas existen tras
/// <c>DoSnapshotSet</c>, y <c>DeleteSnapshots</c> da <c>VSS_E_OBJECT_NOT_FOUND</c> si ya no existen.
/// </summary>
internal sealed class FakeVssSystem : IVssBackupFactory
{
    private readonly Lock _gate = new();
    private int _device = 100;

    /// <summary>Instantáneas existentes en el sistema, por identificador.</summary>
    public Dictionary<Guid, VssSnapshotProperties> Existing { get; } = [];

    /// <summary>Llamadas en orden: <c>Create</c>, <c>SetContext(0)</c>, <c>AddToSnapshotSet(volumen)</c>…</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Identificadores que se han consultado o eliminado.</summary>
    public ConcurrentQueue<Guid> Touched { get; } = new();

    /// <summary>Número de objetos <see cref="IVssBackup"/> creados y sin soltar.</summary>
    public int OpenBackups => _opened - _released;

    private int _opened;
    private int _released;

    /// <summary>Si no es nulo, <see cref="Create"/> lo lanza (VSS parado, acceso denegado…).</summary>
    public Exception? CreateError { get; set; }

    /// <summary>Método → <c>HRESULT</c> con el que falla.</summary>
    public Dictionary<string, int> FailOn { get; } = [];

    /// <summary>Volúmenes (<c>\?\Volume{guid}\</c>) que VSS no admite (FAT, USB): <c>AddToSnapshotSet</c> falla con VSS_E_VOLUME_NOT_SUPPORTED.</summary>
    public HashSet<string> UnsupportedVolumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Se llama en <c>DeleteSnapshots</c> con el token de la eliminación no disponible: sirve para colgarla.</summary>
    public Action? OnDelete { get; set; }

    /// <summary>Se llama justo antes de confirmar el conjunto (<c>DoSnapshotSet</c>).</summary>
    public Action? BeforeDoSnapshotSet { get; set; }

    /// <summary>Se llama en <c>GatherWriterMetadata</c> con su token: sirve para esperar o cancelar.</summary>
    public Action<CancellationToken>? OnGather { get; set; }

    /// <summary>Instante de creación de los conjuntos.</summary>
    public DateTimeOffset CreatedUtc { get; set; } = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Añade una instantánea ajena (de otro programa) o una de Backbone ya existente.</summary>
    public Guid AddExisting(string volume, Guid? setId = null, Guid? id = null)
    {
        var snapshotId = id ?? Guid.NewGuid();
        lock (_gate)
        {
            Existing[snapshotId] = new VssSnapshotProperties(
                snapshotId,
                setId ?? Guid.NewGuid(),
                volume,
                $@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy{++_device}",
                Attributes: 0,
                CreatedUtc);
        }

        return snapshotId;
    }

    public IVssBackup Create()
    {
        Calls.Enqueue("Create");
        if (CreateError is { } error)
        {
            throw error;
        }

        Interlocked.Increment(ref _opened);
        return new Backup(this);
    }

    private void Record(string call) => Calls.Enqueue(call);

    private void FailIf(string method)
    {
        if (FailOn.TryGetValue(method, out var hresult))
        {
            throw new VssException(hresult, method);
        }
    }

    private sealed class Backup(FakeVssSystem system) : IVssBackup
    {
        private readonly List<(Guid Id, string Volume)> _pending = [];
        private Guid _setId;
        private bool _disposed;

        public void SetContext(int context)
        {
            system.Record($"SetContext({context})");
            system.FailIf(nameof(SetContext));
        }

        public void SetBackupState()
        {
            system.Record(nameof(SetBackupState));
            system.FailIf(nameof(SetBackupState));
        }

        public void GatherWriterMetadata(CancellationToken cancellationToken)
        {
            system.Record(nameof(GatherWriterMetadata));
            system.OnGather?.Invoke(cancellationToken);
            system.FailIf(nameof(GatherWriterMetadata));
        }

        public Guid StartSnapshotSet()
        {
            system.Record(nameof(StartSnapshotSet));
            system.FailIf(nameof(StartSnapshotSet));
            return _setId = Guid.NewGuid();
        }

        public Guid AddToSnapshotSet(string volume)
        {
            system.Record($"AddToSnapshotSet({volume})");
            system.FailIf(nameof(AddToSnapshotSet));
            if (system.UnsupportedVolumes.Contains(volume))
            {
                throw new VssException(NativeMethods.Vss.VssEVolumeNotSupported, nameof(AddToSnapshotSet));
            }

            var id = Guid.NewGuid();
            _pending.Add((id, volume));
            return id;
        }

        public void PrepareForBackup(CancellationToken cancellationToken)
        {
            system.Record(nameof(PrepareForBackup));
            system.FailIf(nameof(PrepareForBackup));
        }

        public void DoSnapshotSet(CancellationToken cancellationToken)
        {
            system.Record(nameof(DoSnapshotSet));
            system.BeforeDoSnapshotSet?.Invoke();
            system.FailIf(nameof(DoSnapshotSet));
            lock (system._gate)
            {
                foreach (var (id, volume) in _pending)
                {
                    system.Existing[id] = new VssSnapshotProperties(
                        id,
                        _setId,
                        volume,
                        $@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy{++system._device}",
                        Attributes: 0,
                        system.CreatedUtc);
                }
            }
        }

        public VssSnapshotProperties GetSnapshotProperties(Guid snapshotId)
        {
            system.Record(nameof(GetSnapshotProperties));
            system.Touched.Enqueue(snapshotId);
            system.FailIf(nameof(GetSnapshotProperties));
            lock (system._gate)
            {
                return system.Existing.TryGetValue(snapshotId, out var found) ? found : throw new VssException(NativeMethods.Vss.VssEObjectNotFound, nameof(GetSnapshotProperties));
            }
        }

        public void BackupComplete(CancellationToken cancellationToken)
        {
            system.Record(nameof(BackupComplete));
            system.FailIf(nameof(BackupComplete));
        }

        public void AbortBackup()
        {
            system.Record(nameof(AbortBackup));
            system.FailIf(nameof(AbortBackup));
        }

        public int DeleteSnapshots(Guid id, bool isSet)
        {
            system.Record($"DeleteSnapshots({(isSet ? "set" : "snapshot")})");
            system.Touched.Enqueue(id);
            system.OnDelete?.Invoke();
            system.FailIf(nameof(DeleteSnapshots));
            lock (system._gate)
            {
                var ids = isSet ? system.Existing.Values.Where(p => p.SnapshotSetId == id).Select(p => p.SnapshotId).ToList() : [id];
                var deleted = ids.Count(system.Existing.Remove);
                return deleted > 0 ? deleted : throw new VssException(NativeMethods.Vss.VssEObjectNotFound, nameof(DeleteSnapshots));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                system.Record("Release");
                Interlocked.Increment(ref system._released);
            }
        }
    }
}
