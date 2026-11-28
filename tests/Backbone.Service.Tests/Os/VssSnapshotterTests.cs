using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// HU-15 (§8.2 <c>VssSnapshotter</c>): el flujo de llamadas a VSS, el registro previo a confirmar, la eliminación en todos los
/// caminos y la limpieza de huérfanas, que solo toca lo registrado por Backbone. Con VSS simulado (<see cref="FakeVssSystem"/>).
/// </summary>
[Trait("Story", "HU-15")]
public sealed class VssSnapshotterTests : IDisposable
{
    private const string C = @"\\?\Volume{00000000-0000-0000-0000-000000000043}\";
    private const string D = @"\\?\Volume{00000000-0000-0000-0000-000000000044}\";

    private readonly TempDirectory _temp = new();
    private readonly FakeVssSystem _vss = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly VssSnapshotRegistry _registry;
    private readonly VssSnapshotter _snapshotter;

    public VssSnapshotterTests()
    {
        Directory.CreateDirectory(_temp.Combine("data"));
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        _registry = NewRegistry(_temp.Combine("data", "vss-snapshots.json"));
        _snapshotter = NewSnapshotter(_registry);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _snapshotter.Dispose();
        _registry.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    private VssSnapshotRegistry NewRegistry(string path) =>
        new(path, TestFileTrust.CurrentUser, TimeProvider.System, _loggerFactory.CreateLogger<VssSnapshotRegistry>());

    private VssSnapshotter NewSnapshotter(VssSnapshotRegistry registry, Guid? instance = null) =>
        new(_vss, registry, TimeProvider.System, new VssOptions(), _loggerFactory.CreateLogger<VssSnapshotter>(), initializeCom: false, instanceId: instance);

    // ---- Creación ----

    [Fact]
    public async Task A_set_is_created_with_the_documented_sequence_and_one_snapshot_per_volume_with_the_same_instant()
    {
        await using var set = await _snapshotter.CreateAsync([C, D], Ct);

        Assert.Equal(
            [
                "Create", "SetContext(0)", "SetBackupState", "GatherWriterMetadata", "StartSnapshotSet",
                $"AddToSnapshotSet({C})", $"AddToSnapshotSet({D})", "PrepareForBackup", "DoSnapshotSet",
                "GetSnapshotProperties", "GetSnapshotProperties",
            ],
            _vss.Calls);
        Assert.Equal([C, D], set.Snapshots.Select(s => s.Volume));
        Assert.All(set.Snapshots, s => Assert.Matches(@"^\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy[1-9][0-9]*$", s.DevicePath));
        Assert.Equal(2, set.Snapshots.Select(s => s.DevicePath).Distinct().Count());
        Assert.Single(set.Snapshots.Select(s => s.CreatedUtc).Distinct());
        Assert.Equal(set.Snapshots[0].DevicePath.Split('\\')[^1], set.Snapshots[0].DeviceName);
    }

    [Fact]
    public async Task Repeated_volumes_are_asked_for_once()
    {
        await using var set = await _snapshotter.CreateAsync([C, C.ToUpperInvariant(), D], Ct);

        Assert.Equal(2, set.Snapshots.Count);
        Assert.Equal(2, _vss.Calls.Count(c => c.StartsWith("AddToSnapshotSet", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\Datos")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\")]
    [InlineData("")]
    public async Task A_volume_that_is_not_a_volume_root_is_rejected_before_calling_VSS(string volume)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _snapshotter.CreateAsync([volume], Ct));
        Assert.Empty(_vss.Calls);
    }

    [Fact]
    public async Task At_least_one_volume_is_required()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _snapshotter.CreateAsync([], Ct));
    }

    [Fact]
    public async Task The_set_is_written_in_the_registry_before_it_is_committed_in_VSS()
    {
        string? atCommit = null;
        _vss.BeforeDoSnapshotSet = () => atCommit = File.ReadAllText(_registry.FilePath);

        await using var set = await _snapshotter.CreateAsync([C], Ct);

        Assert.NotNull(atCommit);
        var record = Assert.Single(await _registry.LoadAsync(Ct));
        Assert.Equal(_snapshotter.InstanceId, record.Owner);
        Assert.Contains(record.SetId.ToString("D"), atCommit, StringComparison.OrdinalIgnoreCase);
        var snapshot = Assert.Single(record.Snapshots.OfType<VssSnapshotRecord>());
        Assert.Equal(C, snapshot.Volume);
        Assert.Contains(snapshot.SnapshotId.ToString("D"), atCommit, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Eliminación ----

    [Fact]
    public async Task Disposing_the_set_completes_the_backup_deletes_the_set_releases_the_object_and_clears_the_registry()
    {
        var set = await _snapshotter.CreateAsync([C, D], Ct);
        Assert.Equal(2, _vss.Existing.Count);

        await set.DisposeAsync();

        Assert.Empty(_vss.Existing);
        Assert.Equal(["BackupComplete", "DeleteSnapshots(set)", "Release"], _vss.Calls.TakeLast(3));
        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task Disposing_twice_deletes_once()
    {
        var set = await _snapshotter.CreateAsync([C], Ct);

        await set.DisposeAsync();
        await set.DisposeAsync();

        Assert.Equal(1, _vss.Calls.Count(c => c == "DeleteSnapshots(set)"));
        Assert.Equal(1, _vss.Calls.Count(c => c == "Release"));
    }

    [Fact]
    public async Task A_failed_deletion_still_releases_the_object_and_keeps_the_registry_entry_for_the_next_start()
    {
        var set = await _snapshotter.CreateAsync([C], Ct);
        _vss.FailOn["DeleteSnapshots"] = NativeMethods.Vss.VssEProviderVeto;

        await set.DisposeAsync(); // no lanza

        Assert.Equal(0, _vss.OpenBackups);
        Assert.Single(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task A_failing_BackupComplete_does_not_stop_the_deletion()
    {
        var set = await _snapshotter.CreateAsync([C], Ct);
        _vss.FailOn["BackupComplete"] = NativeMethods.Vss.VssEBadState;

        await set.DisposeAsync();

        Assert.Empty(_vss.Existing);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task A_set_already_gone_counts_as_deleted()
    {
        var set = await _snapshotter.CreateAsync([C], Ct);
        _vss.Existing.Clear(); // VSS la eliminó al soltar el solicitante

        await set.DisposeAsync();

        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    // ---- Fallos de creación ----

    [Theory]
    [InlineData("GatherWriterMetadata", false)]
    [InlineData("StartSnapshotSet", false)]
    [InlineData("AddToSnapshotSet", false)]
    [InlineData("PrepareForBackup", true)]
    [InlineData("DoSnapshotSet", true)]
    [InlineData("GetSnapshotProperties", true)]
    public async Task A_VSS_failure_leaves_nothing_behind_and_surfaces_as_VolumeSnapshotException(string method, bool afterRegistering)
    {
        _vss.FailOn[method] = NativeMethods.Vss.VssEProviderVeto;

        var ex = await Assert.ThrowsAsync<VolumeSnapshotException>(() => _snapshotter.CreateAsync([C], Ct));

        Assert.Contains(method, ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x80042306", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(_vss.Existing);
        Assert.Empty(await _registry.LoadAsync(Ct));
        Assert.Contains("Release", _vss.Calls);
        Assert.Equal(afterRegistering, _vss.Calls.Contains("DoSnapshotSet") || _vss.Calls.Contains("PrepareForBackup"));
    }

    [Fact]
    public async Task A_failure_after_committing_deletes_what_was_created()
    {
        _vss.FailOn["GetSnapshotProperties"] = NativeMethods.Vss.VssEUnexpected;

        await Assert.ThrowsAsync<VolumeSnapshotException>(() => _snapshotter.CreateAsync([C, D], Ct));

        Assert.Contains("DeleteSnapshots(set)", _vss.Calls);
        Assert.Empty(_vss.Existing);
    }

    [Fact]
    public async Task VSS_unavailable_when_creating_the_object_is_a_VolumeSnapshotException()
    {
        _vss.CreateError = new VssException(NativeMethods.Vss.EAccessDenied, "CreateVssBackupComponents");

        var ex = await Assert.ThrowsAsync<VolumeSnapshotException>(() => _snapshotter.CreateAsync([C], Ct));

        Assert.Contains("0x80070005", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_cannot_be_written_means_no_snapshot_is_ever_committed()
    {
        using var broken = NewRegistry(_temp.Combine("no-existe", "vss-snapshots.json"));
        using var snapshotter = NewSnapshotter(broken);

        await Assert.ThrowsAsync<VolumeSnapshotException>(() => snapshotter.CreateAsync([C], Ct));

        Assert.DoesNotContain("DoSnapshotSet", _vss.Calls);
        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(_vss.Existing);
    }

    [Fact]
    public async Task A_cancellation_is_propagated_and_releases_everything()
    {
        using var cts = new CancellationTokenSource();
        _vss.OnGather = token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _snapshotter.CreateAsync([C], cts.Token));

        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(_vss.Existing);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task A_token_already_cancelled_does_not_touch_VSS()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _snapshotter.CreateAsync([C], cts.Token));

        Assert.Empty(_vss.Calls);
    }

    [Fact]
    public async Task The_creation_timeout_cancels_VSS_and_the_copy_goes_on_live()
    {
        using var snapshotter = new VssSnapshotter(
            _vss, _registry, TimeProvider.System, new VssOptions { CreateTimeout = TimeSpan.FromMilliseconds(50) }, NullLogger<VssSnapshotter>.Instance, initializeCom: false);
        _vss.OnGather = token => token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<VolumeSnapshotException>(() => snapshotter.CreateAsync([C], Ct));

        Assert.Contains("in time", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _vss.OpenBackups);
    }

    // ---- Criterio 3: huérfanas ----

    private async Task RegisterAsync(Guid owner, Guid setId, params (Guid Id, string Volume)[] snapshots) =>
        await _registry.AddAsync(owner, setId, snapshots, Ct);

    [Fact]
    public async Task At_start_the_orphans_registered_by_a_previous_run_are_deleted_and_nothing_else()
    {
        var previous = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var ours = _vss.AddExisting(C, setId);
        var foreign = _vss.AddExisting(C); // de otro programa, mismo volumen
        var foreignOtherSet = _vss.AddExisting(D);
        await RegisterAsync(previous, setId, (ours, C));

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(1, deleted);
        Assert.DoesNotContain(ours, _vss.Existing.Keys);
        Assert.Contains(foreign, _vss.Existing.Keys);
        Assert.Contains(foreignOtherSet, _vss.Existing.Keys);
        Assert.Empty(await _registry.LoadAsync(Ct));

        // Solo se ha consultado y eliminado el identificador registrado: nunca se enumera el resto de instantáneas.
        Assert.All(_vss.Touched, id => Assert.Equal(ours, id));
        Assert.Contains("SetContext(-1)", _vss.Calls);
        Assert.DoesNotContain("StartSnapshotSet", _vss.Calls);
    }

    [Fact]
    public async Task An_orphan_whose_set_or_volume_does_not_match_the_registry_is_not_touched()
    {
        var stranger = _vss.AddExisting(C);                              // su conjunto no es el registrado
        var otherVolume = _vss.AddExisting(D, setId: Guid.Empty);        // se sustituye abajo
        var knownSet = Guid.NewGuid();
        _vss.Existing[otherVolume] = _vss.Existing[otherVolume] with { SnapshotSetId = knownSet };
        await RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), (stranger, C));
        await RegisterAsync(Guid.NewGuid(), knownSet, (otherVolume, C)); // el volumen registrado no coincide

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(0, deleted);
        Assert.Equal(2, _vss.Existing.Count);
        Assert.DoesNotContain("DeleteSnapshots(snapshot)", _vss.Calls);
        Assert.Empty(await _registry.LoadAsync(Ct)); // las entradas se descartan: no son de Backbone
    }

    [Fact]
    public async Task What_the_current_instance_created_is_never_cleaned_as_an_orphan()
    {
        await using var set = await _snapshotter.CreateAsync([C], Ct);

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(0, deleted);
        Assert.Single(_vss.Existing);
        Assert.Single(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task An_orphan_that_no_longer_exists_is_dropped_from_the_registry()
    {
        await RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), (Guid.NewGuid(), C));

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(0, deleted);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task An_orphan_that_cannot_be_deleted_stays_registered_for_the_next_start()
    {
        var setId = Guid.NewGuid();
        var ours = _vss.AddExisting(C, setId);
        await RegisterAsync(Guid.NewGuid(), setId, (ours, C));
        _vss.FailOn["DeleteSnapshots"] = NativeMethods.Vss.VssEProviderVeto;

        var deleted = await _snapshotter.DeleteOrphansAsync(Ct);

        Assert.Equal(0, deleted);
        Assert.Contains(ours, _vss.Existing.Keys);
        Assert.Single(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task With_VSS_unavailable_at_start_nothing_throws_and_the_registry_is_kept()
    {
        await RegisterAsync(Guid.NewGuid(), Guid.NewGuid(), (Guid.NewGuid(), C));
        _vss.CreateError = new VssException(NativeMethods.Vss.EAccessDenied, "CreateVssBackupComponents");

        Assert.Equal(0, await _snapshotter.DeleteOrphansAsync(Ct));

        Assert.Single(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task Without_registered_orphans_VSS_is_not_even_opened()
    {
        Assert.Equal(0, await _snapshotter.DeleteOrphansAsync(Ct));

        Assert.Empty(_vss.Calls);
    }

    [Fact]
    public async Task A_damaged_or_tampered_registry_is_empty_and_deletes_nothing()
    {
        var foreign = _vss.AddExisting(C);
        await File.WriteAllTextAsync(_registry.FilePath, "{ esto no es json", Ct);

        Assert.Equal(0, await _snapshotter.DeleteOrphansAsync(Ct));

        Assert.Contains(foreign, _vss.Existing.Keys);
        Assert.Empty(_vss.Calls);
    }

    // ---- Dispositivo de la instantánea ----

    [Theory]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12")]
    [InlineData(@"\Device\HarddiskVolumeShadowCopy3", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3")]
    public void The_device_of_a_snapshot_is_normalised_to_the_root_the_reader_accepts(string device, string expected) =>
        Assert.Equal(expected, VssSnapshotter.ToDevicePath(device));

    [Theory]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\servidor\recurso")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy012")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\Datos")]
    [InlineData(@"\\servidor\recurso")]
    [InlineData("")]
    public void A_device_that_is_not_a_shadow_copy_is_rejected(string device) =>
        Assert.Throws<VolumeSnapshotException>(() => VssSnapshotter.ToDevicePath(device));
}
