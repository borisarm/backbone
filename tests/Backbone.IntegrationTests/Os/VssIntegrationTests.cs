using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.IntegrationTests.Sources;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Os;

/// <summary>
/// HU-15 (RF-22, R1) con VSS real: crear, leer y eliminar instantáneas del volumen que contiene la carpeta temporal, y limpiar
/// las huérfanas. <b>Requieren PowerShell elevado</b> (VSS solo admite a Administradores, Operadores de copia y SYSTEM) y se
/// omiten si no lo está. Usan el token del administrador que las lanza, no el del servicio instalado con los privilegios de §10:
/// eso lo comprueba PM-HU15-01. Pruebas por separado (xUnit las ejecuta en paralelo entre clases, no dentro de una), con
/// instantáneas propias de la prueba: nunca tocan las de otros programas.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-15")]
public sealed class VssIntegrationTests : IDisposable
{
    private const string NeedsElevation = "Requires an elevated process (VSS is only available to Administrators, Backup Operators and SYSTEM).";

    private readonly TempDirectory _temp = new();
    private readonly List<IAsyncDisposable> _toDispose = [];
    private readonly List<Guid> _persistentToDelete = [];
    private readonly VssSnapshotRegistry _registry;
    private readonly VssSnapshotter _snapshotter;
    private readonly string _origin;

    public VssIntegrationTests()
    {
        Directory.CreateDirectory(_temp.Combine("data"));
        _origin = Directory.CreateDirectory(_temp.Combine("origen")).FullName;
        _registry = new VssSnapshotRegistry(_temp.Combine("data", "vss-snapshots.json"), TestFileTrust.CurrentUser, TimeProvider.System, NullLogger<VssSnapshotRegistry>.Instance);
        _snapshotter = new VssSnapshotter(new VssBackupFactory(), _registry, TimeProvider.System, new VssOptions(), NullLogger<VssSnapshotter>.Instance);
    }

    public static bool IsElevated => WindowsTestEnvironment.IsElevated;

    public static bool HasTwoFixedVolumes => IsElevated && FixedNtfsVolumes().Count >= 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var item in _toDispose)
        {
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_persistentToDelete.Count > 0)
        {
            using var worker = new VssWorker();
            foreach (var id in _persistentToDelete)
            {
                try
                {
                    worker.RunAsync(() => DeleteById(id)).GetAwaiter().GetResult();
                }
                catch (VssException)
                {
                    // Ya no existe.
                }
            }
        }

        _snapshotter.Dispose();
        _registry.Dispose();
        _temp.Dispose();
    }

    // ---- Criterio 1 ----

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task A_file_open_exclusively_by_another_handle_is_read_from_the_snapshot_with_its_real_content()
    {
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 7);
        var path = Path.Combine(_origin, "correo.pst");
        await using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await locked.WriteAsync(content, Ct);
        await locked.FlushAsync(Ct);
        Assert.Throws<IOException>(() => File.OpenRead(path).Dispose()); // en vivo no se puede leer

        var (set, readPath, devices) = await CreateForOriginAsync();
        _toDispose.Add(set);
        using var fs = NewReader(readPath, devices);

        var entries = await ListAsync(fs, readPath);
        var entry = Assert.Single(entries);
        Assert.Equal(("correo.pst", content.Length), (entry.Name, entry.Length));
        await using var stream = await fs.OpenReadAsync(entry.FullPath, Ct);
        Assert.Equal(SHA256.HashData(content), await SHA256.HashDataAsync(stream, Ct));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_engine_copies_a_locked_file_from_the_snapshot_and_completes_without_warnings_while_a_live_copy_warns()
    {
        var content = RandomNumberGenerator.GetBytes(300_000);
        await using var locked = new FileStream(Path.Combine(_origin, "bloqueado.dat"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await locked.WriteAsync(content, Ct);
        await locked.FlushAsync(Ct);
        await File.WriteAllTextAsync(Path.Combine(_origin, "normal.txt"), "normal", Ct);

        // Con VSS.
        var (set, readPath, devices) = await CreateForOriginAsync();
        _toDispose.Add(set);
        using (var fs = NewReader(readPath, devices))
        {
            var result = await BackupAsync(fs, readPath, usedVss: true);
            Assert.Equal((RunState.Completed, 0L, 2L), (result.State, result.WarningCount, result.Snapshot.Stats.Files));
            Assert.True(result.Snapshot.UsedVss);
        }

        // Control, en vivo: el bloqueado se omite con aviso (RF-25) y la ejecución termina con avisos.
        using var live = NewReader(SourceHandles.FinalPath(_origin), []);
        var liveResult = await BackupAsync(live, SourceHandles.FinalPath(_origin), usedVss: false);
        Assert.Equal(RunState.CompletedWithWarnings, liveResult.State);
        Assert.Contains(liveResult.Warnings, w => w.Path.EndsWith("bloqueado.dat", StringComparison.Ordinal));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_snapshot_keeps_the_content_of_its_instant_and_a_device_of_another_run_is_not_accepted()
    {
        var path = Path.Combine(_origin, "a.txt");
        await File.WriteAllTextAsync(path, "antes", Ct);
        var (set, readPath, devices) = await CreateForOriginAsync();
        _toDispose.Add(set);
        await File.WriteAllTextAsync(path, "despues", Ct);
        using var fs = NewReader(readPath, devices);

        var entry = Assert.Single(await ListAsync(fs, readPath));
        await using var stream = await fs.OpenReadAsync(entry.FullPath, Ct);
        using var reader = new StreamReader(stream);
        Assert.Equal("antes", await reader.ReadToEndAsync(Ct));

        // B-2: sin declarar el dispositivo como de esta ejecución, la lectura de la instantánea se rechaza.
        Assert.Throws<ArgumentException>(() => NewReader(readPath, []));
    }

    // ---- Criterio 2 ----

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task Disposing_the_set_removes_the_snapshot_and_the_registry_entry()
    {
        await File.WriteAllTextAsync(Path.Combine(_origin, "a.txt"), "a", Ct);
        var (set, readPath, _) = await CreateForOriginAsync();
        var record = Assert.Single(await _registry.LoadAsync(Ct));
        var ids = record.Snapshots.OfType<VssSnapshotRecord>().Select(s => s.SnapshotId).ToList();
        Assert.True(Directory.Exists(readPath));
        Assert.All(ids, id => Assert.True(Exists(id)));

        await set.DisposeAsync();

        Assert.All(ids, id => Assert.False(Exists(id)));
        Assert.False(Directory.Exists(readPath));
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task Cancelling_while_the_set_is_being_created_leaves_no_snapshot_and_no_registry_entry()
    {
        var volume = VolumeOf(_origin);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));

        try
        {
            await using var set = await _snapshotter.CreateAsync([volume], cts.Token); // por si VSS fue más rápido que el plazo
        }
        catch (OperationCanceledException)
        {
            // Lo esperado casi siempre.
        }

        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    // ---- Criterio 5 ----

    [Fact(Skip = "Requires two fixed local NTFS volumes.", SkipUnless = nameof(HasTwoFixedVolumes))]
    public async Task Two_volumes_give_one_snapshot_each_with_the_same_set_and_instant()
    {
        var volumes = FixedNtfsVolumes().Take(2).ToList();

        var set = await _snapshotter.CreateAsync(volumes, Ct);
        _toDispose.Add(set);

        Assert.Equal(volumes, set.Snapshots.Select(s => s.Volume));
        Assert.Equal(2, set.Snapshots.Select(s => s.DevicePath).Distinct().Count());
        Assert.Single(set.Snapshots.Select(s => s.CreatedUtc).Distinct());
        Assert.Single(await _registry.LoadAsync(Ct)); // un solo conjunto
    }

    // ---- Criterio 3 ----

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task Orphans_registered_by_a_previous_run_are_deleted_and_snapshots_of_other_programs_are_kept()
    {
        var volume = VolumeOf(_origin);
        var ours = CreatePersistent(volume); // sobrevive a su solicitante, como una huérfana de un servicio que murió
        var foreign = CreatePersistent(volume); // la de "otro programa": no está en el registro
        await _registry.AddAsync(Guid.NewGuid(), ours.SetId, [(ours.SnapshotId, volume)], Ct);

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(1, deleted);
        Assert.False(Exists(ours.SnapshotId));
        Assert.True(Exists(foreign.SnapshotId)); // no era de Backbone
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task A_registered_id_whose_set_does_not_match_is_not_deleted()
    {
        var volume = VolumeOf(_origin);
        var foreign = CreatePersistent(volume);
        await _registry.AddAsync(Guid.NewGuid(), Guid.NewGuid(), [(foreign.SnapshotId, volume)], Ct); // conjunto distinto

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(0, deleted);
        Assert.True(Exists(foreign.SnapshotId));
    }

    // ---- Apoyo ----

    private async Task<(IVolumeSnapshotSet Set, string ReadPath, string[] Devices)> CreateForOriginAsync()
    {
        var guidPath = SourceHandlesGuidPath(_origin);
        Assert.True(VssPaths.TrySplit(guidPath, out var volume, out var relative));
        var set = await _snapshotter.CreateAsync([volume], Ct);
        var snapshot = Assert.Single(set.Snapshots);
        return (set, VssPaths.Combine(snapshot.DevicePath, relative), [snapshot.DeviceName]);
    }

    private static SourceFileSystem NewReader(string readPath, string[] devices) =>
        new(
            [new BackupSource("origen", @"C:\origen", readPath)],
            new Win32SourceFileApi(),
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLoggerFactory.Instance.CreateLogger<SourceFileSystem>(),
            snapshotDevices: devices);

    private static async Task<BackupResult> BackupAsync(SourceFileSystem fs, string readPath, bool usedVss)
    {
        var storage = new InMemoryRepositoryStorage();
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "Backbone.IntegrationTests" }, TimeProvider.System, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, owner: null, Ct);
        var request = new BackupRequest
        {
            JobId = Guid.NewGuid().ToString("D"),
            JobName = "vss",
            Host = "PRUEBAS",
            Sources = [new BackupSource("origen", @"C:\origen", readPath)],
            UsedVss = usedVss,
        };
        return await new BackupEngine(TimeProvider.System).RunAsync(repository, repositoryLock, fs, request, new Progress<BackupProgress>(), Ct);
    }

    private static async Task<List<SourceEntry>> ListAsync(SourceFileSystem fs, string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in fs.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private static string SourceHandlesGuidPath(string path)
    {
        using var handle = SourceHandles.Open(path, SourceOpenMode.Directory);
        return handle.GetFinalPath(FinalPathFormat.Guid, CancellationToken.None);
    }

    private static string VolumeOf(string path)
    {
        Assert.True(VssPaths.TrySplit(SourceHandlesGuidPath(path), out var volume, out _));
        return volume;
    }

    private static List<string> FixedNtfsVolumes() =>
    [
        .. DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            .Select(d => VolumeOf(d.RootDirectory.FullName))
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>Una instantánea persistente y sin escritores (contexto NAS_ROLLBACK), que sobrevive a su solicitante.</summary>
    private (Guid SetId, Guid SnapshotId) CreatePersistent(string volume)
    {
        using var worker = new VssWorker();
        var created = worker.RunAsync(() =>
        {
            using var backup = new VssBackupFactory().Create();
            backup.SetContext(NativeMethods.Vss.ContextNasRollback);
            var setId = backup.StartSnapshotSet();
            var snapshotId = backup.AddToSnapshotSet(volume);
            backup.DoSnapshotSet(Ct);
            return (setId, snapshotId);
        }).GetAwaiter().GetResult();
        _persistentToDelete.Add(created.snapshotId);
        return created;
    }

    private static bool Exists(Guid snapshotId)
    {
        using var worker = new VssWorker();
        return worker.RunAsync(() =>
        {
            using var backup = new VssBackupFactory().Create();
            backup.SetContext(NativeMethods.Vss.ContextAll);
            try
            {
                _ = backup.GetSnapshotProperties(snapshotId);
                return true;
            }
            catch (VssException ex) when (ex.IsObjectNotFound)
            {
                return false;
            }
        }).GetAwaiter().GetResult();
    }

    private static bool DeleteById(Guid snapshotId)
    {
        using var backup = new VssBackupFactory().Create();
        backup.SetContext(NativeMethods.Vss.ContextAll);
        _ = backup.DeleteSnapshots(snapshotId, isSet: false);
        return true;
    }
}
