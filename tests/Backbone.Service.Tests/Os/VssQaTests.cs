using System.Text.Json;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// HU-15, ampliación de QA: inyección de fallos en cada paso de VSS, volúmenes que no admiten VSS, parada del servicio a mitad
/// de la creación, eliminación que no vuelve y registro adulterado (duplicados, identificadores mal formados).
/// </summary>
[Trait("Story", "HU-15")]
public sealed class VssQaTests : IDisposable
{
    private const string C = @"\\?\Volume{00000000-0000-0000-0000-000000000043}\";
    private const string D = @"\\?\Volume{00000000-0000-0000-0000-000000000044}\";

    private readonly TempDirectory _temp = new();
    private readonly FakeVssSystem _vss = new();
    private readonly VssSnapshotRegistry _registry;
    private readonly List<VssSnapshotter> _snapshotters = [];

    public VssQaTests()
    {
        Directory.CreateDirectory(_temp.Combine("data"));
        _registry = new VssSnapshotRegistry(_temp.Combine("data", "vss-snapshots.json"), TestFileTrust.CurrentUser, TimeProvider.System, NullLogger<VssSnapshotRegistry>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var snapshotter in _snapshotters)
        {
            snapshotter.Dispose();
        }

        _registry.Dispose();
        _temp.Dispose();
    }

    private VssSnapshotter NewSnapshotter(VssOptions? options = null, Guid? instance = null)
    {
        var snapshotter = new VssSnapshotter(_vss, _registry, TimeProvider.System, options ?? new VssOptions(), NullLogger<VssSnapshotter>.Instance, initializeCom: false, instanceId: instance);
        _snapshotters.Add(snapshotter);
        return snapshotter;
    }

    // ---- Criterio 4: cada paso que falla deja el sistema limpio ----

    [Theory]
    [InlineData("SetContext")]
    [InlineData("SetBackupState")]
    [InlineData("GatherWriterMetadata")]
    [InlineData("StartSnapshotSet")]
    [InlineData("AddToSnapshotSet")]
    [InlineData("PrepareForBackup")]
    [InlineData("DoSnapshotSet")]
    [InlineData("GetSnapshotProperties")]
    public async Task Each_step_failing_with_two_volumes_leaves_no_snapshot_no_object_and_no_registry_entry(string method)
    {
        _vss.FailOn[method] = NativeMethods.Vss.VssEUnexpected;

        var ex = await Assert.ThrowsAsync<VolumeSnapshotException>(() => NewSnapshotter().CreateAsync([C, D], Ct));

        Assert.Contains(method, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(_vss.Existing);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task A_failing_AbortBackup_after_a_failed_step_still_releases_the_object()
    {
        _vss.FailOn["AddToSnapshotSet"] = NativeMethods.Vss.VssEUnexpected;
        _vss.FailOn["AbortBackup"] = NativeMethods.Vss.VssEBadState;

        await Assert.ThrowsAsync<VolumeSnapshotException>(() => NewSnapshotter().CreateAsync([C], Ct));

        Assert.Equal(0, _vss.OpenBackups);
    }

    [Fact]
    public async Task A_failing_deletion_after_a_failed_commit_keeps_the_registry_entry_and_a_later_start_removes_it()
    {
        _vss.FailOn["DoSnapshotSet"] = NativeMethods.Vss.VssEProviderVeto;
        _vss.FailOn["DeleteSnapshots"] = NativeMethods.Vss.VssEProviderVeto;
        await Assert.ThrowsAsync<VolumeSnapshotException>(() => NewSnapshotter().CreateAsync([C], Ct));

        Assert.Equal(0, _vss.OpenBackups);
        Assert.Single(await _registry.LoadAsync(Ct)); // registrado: el siguiente arranque lo reintenta

        _vss.FailOn.Clear();
        Assert.Equal(0, await NewSnapshotter().DeleteOrphansAsync(Ct)); // nunca llegó a existir: se descarta
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task A_volume_that_does_not_support_VSS_mixed_with_one_that_does_creates_nothing()
    {
        _vss.UnsupportedVolumes.Add(D);

        var ex = await Assert.ThrowsAsync<VolumeSnapshotException>(() => NewSnapshotter().CreateAsync([C, D], Ct));

        Assert.Contains("AddToSnapshotSet", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x8004230C", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_vss.Existing);
        Assert.DoesNotContain("DoSnapshotSet", _vss.Calls);
        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task Volumes_written_with_different_case_are_asked_for_once()
    {
        var lower = C.Replace("Volume{", "volume{", StringComparison.Ordinal);

        await using var set = await NewSnapshotter().CreateAsync([C, lower], Ct);

        Assert.Single(set.Snapshots);
    }

    [Fact]
    public async Task Every_snapshot_of_a_set_has_the_same_instant_and_a_distinct_device_from_a_single_VSS_set()
    {
        await using var set = await NewSnapshotter().CreateAsync([C, D], Ct);

        Assert.Equal(2, set.Snapshots.Select(s => s.DevicePath).Distinct().Count());
        Assert.Single(set.Snapshots.Select(s => s.CreatedUtc).Distinct());
        Assert.Equal(1, _vss.Calls.Count(c => c == "StartSnapshotSet"));
        Assert.Equal(1, _vss.Calls.Count(c => c == "DoSnapshotSet"));
    }

    // ---- Criterio 2: parada del servicio y eliminación que no vuelve ----

    [Fact(Skip = "Falla: VssSnapshotter.cs:105/241-282, si el trabajador de VSS se desecha entre Prepare y Commit el IVssBackup preparado no se suelta — pendiente de backbone-service")]
    public async Task Stopping_the_snapshotter_in_the_middle_of_CreateAsync_leaves_nothing_behind()
    {
        var snapshotter = NewSnapshotter();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        _vss.OnGather = _ =>
        {
            entered.Set();
            proceed.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        };
        var create = snapshotter.CreateAsync([C], Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));

        var stop = Task.Run(snapshotter.Dispose, Ct);
        proceed.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        // Termine como termine la creación (completa o con error), no queda ni instantánea ni objeto de VSS sin soltar.
        try
        {
            await using var set = await create;
        }
        catch (Exception ex) when (ex is VolumeSnapshotException or OperationCanceledException or ObjectDisposedException)
        {
        }

        Assert.Equal(0, _vss.OpenBackups);
        Assert.Empty(_vss.Existing);
    }

    [Fact(Skip = "Falla: VssSnapshotter.cs:277-282, ReleaseTimeout no corta una DeleteSnapshots que no vuelve y DisposeAsync queda colgado — pendiente de backbone-service")]
    public async Task A_deletion_that_never_returns_is_given_up_within_the_release_timeout()
    {
        using var hang = new ManualResetEventSlim();
        var snapshotter = NewSnapshotter(new VssOptions { ReleaseTimeout = TimeSpan.FromMilliseconds(300) });
        var set = await snapshotter.CreateAsync([C], Ct);
        _vss.OnDelete = () => hang.Wait(TimeSpan.FromSeconds(10));

        try
        {
            await set.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        }
        finally
        {
            hang.Set();
        }

        Assert.Single(await _registry.LoadAsync(Ct)); // queda registrado para el siguiente arranque
    }

    // ---- Criterio 3: registro adulterado ----

    private Task WriteRegistryAsync(string json) => File.WriteAllTextAsync(_registry.FilePath, json, Ct);

    [Fact]
    public async Task A_registry_with_malformed_ids_is_treated_as_empty_and_nothing_is_deleted()
    {
        var foreign = _vss.AddExisting(C);
        await WriteRegistryAsync("""{"schemaVersion":1,"sets":[{"owner":"x","setId":"no-es-un-guid","snapshots":[{"snapshotId":"123","volume":"C:"}]}]}""");

        Assert.Equal(0, await NewSnapshotter().DeleteOrphansAsync(Ct));

        Assert.Contains(foreign, _vss.Existing.Keys);
        Assert.Empty(_vss.Calls);
    }

    [Fact]
    public async Task A_registry_with_null_entries_and_duplicate_sets_deletes_each_registered_snapshot_once_and_nothing_else()
    {
        var setId = Guid.NewGuid();
        var ours = _vss.AddExisting(C, setId);
        var foreign = _vss.AddExisting(C);
        var entry = new VssSnapshotSetRecord
        {
            Owner = Guid.NewGuid(),
            SetId = setId,
            Snapshots = [new VssSnapshotRecord { SnapshotId = ours, Volume = C }, null, new VssSnapshotRecord { SnapshotId = ours, Volume = C }],
        };
        var document = new VssSnapshotsDocument { Sets = [entry, null, entry] };
        await WriteRegistryAsync(JsonSerializer.Serialize(document, VssSnapshotRegistry.JsonOptions));

        var deleted = await NewSnapshotter().DeleteOrphansAsync(Ct);

        Assert.Equal(1, deleted);
        Assert.DoesNotContain(ours, _vss.Existing.Keys);
        Assert.Contains(foreign, _vss.Existing.Keys);
        Assert.All(_vss.Touched, id => Assert.Equal(ours, id));
        Assert.Empty(await _registry.LoadAsync(Ct));
    }

    [Fact]
    public async Task An_orphan_set_with_snapshots_on_two_volumes_deletes_both()
    {
        var setId = Guid.NewGuid();
        var onC = _vss.AddExisting(C, setId);
        var onD = _vss.AddExisting(D, setId);
        await _registry.AddAsync(Guid.NewGuid(), setId, [(onC, C), (onD, D)], Ct);

        Assert.Equal(2, await NewSnapshotter().DeleteOrphansAsync(Ct));

        Assert.Empty(_vss.Existing);
        Assert.Empty(await _registry.LoadAsync(Ct));
    }
}
