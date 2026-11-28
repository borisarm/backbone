using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-15 (RF-22, §8.4 pasos 5 y 9) con <see cref="IVolumeSnapshotter"/> simulado: la copia se lee de la instantánea, una
/// instantánea por volumen en un único conjunto, eliminación en todos los caminos y copia en vivo con aviso y evento 4000
/// si VSS falla.
/// </summary>
[Trait("Story", "HU-15")]
public sealed class VssJobRunnerTests : IAsyncLifetime
{
    private readonly RunRig _rig = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _rig.SaveCredentialAsync();
        _rig.Sources.Source.AddFile(@"C:\Datos\notas.txt", "hola, mundo");
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<RunRecord> RunAsync(JobDefinition job)
    {
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        return await _rig.NextCompletedAsync();
    }

    private static string VolumeOf(char letter) => FakeLocalFileSystemInspector.FakeGuidPath($@"{letter}:\")[..49];

    // ---- Criterio 1: se lee de la instantánea ----

    [Fact]
    public async Task The_copy_is_read_from_the_snapshot_and_completes_without_warnings()
    {
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.Completed, 1L, true), (run.State, run.Stats.Files, run.UsedVss));
        var set = Assert.Single(_rig.Snapshotter.Sets);
        var snapshot = Assert.Single(set.Snapshots);
        var source = Assert.Single(Assert.Single(_rig.Sources.Created));
        Assert.Equal(snapshot.DevicePath + @"\Datos", source.ReadPath);
        Assert.Equal(@"C:\Datos", source.Path); // lo que se guarda en el snapshot sigue siendo la ruta configurada
        Assert.Contains(snapshot.DevicePath + @"\Datos", _rig.Sources.Source.Enumerated);
        Assert.DoesNotContain(@"C:\Datos", _rig.Sources.Source.Enumerated);
        Assert.DoesNotContain(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
        Assert.Equal(VolumeOf('C'), Assert.Single(Assert.Single(_rig.Snapshotter.Requests)));
    }

    [Fact]
    public async Task The_snapshot_is_flagged_in_the_stored_snapshot_header()
    {
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        var header = await RepositoryCheck.ReadSnapshotHeaderAsync(_rig.Memory(), run.SnapshotId!);
        Assert.True(header.UsedVss);
        Assert.Empty(header.Warnings);
    }

    [Fact]
    public async Task The_snapshot_devices_of_this_run_are_the_only_ones_handed_to_the_reader()
    {
        var job = await _rig.AddJobAsync();

        await RunAsync(job);

        var devices = Assert.Single(_rig.Sources.Devices);
        var snapshot = Assert.Single(Assert.Single(_rig.Snapshotter.Sets).Snapshots);
        Assert.Equal([snapshot.DeviceName], devices);
    }

    [Fact]
    public async Task The_snapshot_path_comes_from_the_final_path_of_the_source_not_from_the_configured_one()
    {
        // Un junction legítimo: C:\Datos\enlace lleva a D:\Real\Datos. La instantánea es la del volumen D, y no se resuelve
        // el enlace dentro de ella hacia el volumen en vivo (§8.2 "Raíz del origen en VSS").
        _rig.Inspector.AddFolder(@"C:\Datos\enlace", finalPath: @"D:\Real\Datos");
        _rig.Sources.Source.AddFile(@"D:\Real\Datos\dentro.txt", "contenido");
        var job = await _rig.AddJobAsync(sources: [("Enlace", @"C:\Datos\enlace")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.Completed, 1L), (run.State, run.Stats.Files));
        Assert.Equal(VolumeOf('D'), Assert.Single(Assert.Single(_rig.Snapshotter.Requests)));
        var snapshot = Assert.Single(Assert.Single(_rig.Snapshotter.Sets).Snapshots);
        Assert.Equal(snapshot.DevicePath + @"\Real\Datos", Assert.Single(Assert.Single(_rig.Sources.Created)).ReadPath);
    }

    [Fact]
    public async Task The_mandatory_exclusions_stay_relative_and_apply_inside_the_snapshot()
    {
        var programData = Path.GetDirectoryName(_rig.DataRoot) ?? _rig.DataRoot;
        _rig.Inspector.AddFolder(programData);
        _rig.Sources.Source.AddFile(Path.Combine(programData, "otro", "x.txt"), "x");
        var job = await _rig.AddJobAsync(sources: [("PD", programData)]);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        var source = Assert.Single(Assert.Single(_rig.Sources.Created));
        Assert.Equal([Path.GetFileName(_rig.DataRoot)], source.MandatoryExclusions);
        Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy", source.ReadPath, StringComparison.Ordinal);
    }

    // ---- Criterio 5: una instantánea por volumen, en un único conjunto ----

    [Fact]
    public async Task Sources_on_several_volumes_get_one_snapshot_each_from_a_single_set_with_the_same_instant()
    {
        _rig.Inspector.AddFolder(@"D:\Fotos");
        _rig.Sources.Source.AddFile(@"D:\Fotos\a.jpg", "foto");
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Fotos", @"D:\Fotos")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.Completed, 2L), (run.State, run.Stats.Files));
        Assert.Equal([VolumeOf('C'), VolumeOf('D')], Assert.Single(_rig.Snapshotter.Requests)); // una sola llamada: un conjunto
        var set = Assert.Single(_rig.Snapshotter.Sets);
        Assert.Equal(2, set.Snapshots.Count);
        Assert.Single(set.Snapshots.Select(s => s.CreatedUtc).Distinct());
        var sources = Assert.Single(_rig.Sources.Created);
        Assert.Equal(set.Snapshots[0].DevicePath + @"\Datos", sources[0].ReadPath);
        Assert.Equal(set.Snapshots[1].DevicePath + @"\Fotos", sources[1].ReadPath);
        Assert.Equal(set.Snapshots.Select(s => s.DeviceName), Assert.Single(_rig.Sources.Devices));
    }

    [Fact]
    public async Task Sources_on_the_same_volume_share_one_snapshot()
    {
        _rig.Inspector.AddFolder(@"C:\Otros");
        _rig.Sources.Source.AddFile(@"C:\Otros\b.txt", "b");
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Otros", @"C:\Otros")]);

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        Assert.Equal([VolumeOf('C')], Assert.Single(_rig.Snapshotter.Requests));
        var snapshot = Assert.Single(Assert.Single(_rig.Snapshotter.Sets).Snapshots);
        var sources = Assert.Single(_rig.Sources.Created);
        Assert.Equal([snapshot.DevicePath + @"\Datos", snapshot.DevicePath + @"\Otros"], sources.Select(s => s.ReadPath));
    }

    [Fact]
    public async Task Only_unreadable_sources_ask_for_no_snapshot()
    {
        _rig.Inspector.AddStatus(@"C:\Falta", LocalFolderStatus.NotFound);
        var job = await _rig.AddJobAsync(sources: [("Falta", @"C:\Falta")]);

        var run = await RunAsync(job);

        Assert.Empty(_rig.Snapshotter.Requests);
        Assert.False(run.UsedVss);
        Assert.DoesNotContain(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
    }

    // ---- Criterio 2: la instantánea se elimina siempre ----

    [Fact]
    public async Task The_snapshot_is_deleted_when_the_run_completes()
    {
        var job = await _rig.AddJobAsync();

        await RunAsync(job);

        await RunRig.WaitUntilAsync(() => _rig.Snapshotter.ActiveSets == 0);
        Assert.Equal(1, Assert.Single(_rig.Snapshotter.Sets).Disposals);
    }

    [Fact]
    public async Task The_snapshot_is_deleted_when_the_run_fails()
    {
        var job = await _rig.AddJobAsync();
        _rig.Sources.CreateError = new InvalidOperationException("la lectura no se puede crear");

        var run = await RunAsync(job);

        Assert.Equal(RunState.Failed, run.State);
        Assert.Equal(1, Assert.Single(_rig.Snapshotter.Sets).Disposals);
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    [Fact]
    public async Task The_snapshot_is_deleted_when_the_user_cancels_the_run()
    {
        var job = await _rig.AddJobAsync();
        var (entered, release) = _rig.HoldAtFirstFile();

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(1, _rig.Snapshotter.ActiveSets);
        _rig.Runs.Cancel(job.Id);

        var run = await _rig.NextCompletedAsync();
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Equal(0, _rig.Snapshotter.ActiveSets);
        Assert.Equal(1, Assert.Single(_rig.Snapshotter.Sets).Disposals);
        release.TrySetResult();
    }

    [Fact]
    public async Task The_snapshot_is_deleted_when_the_service_stops()
    {
        var job = await _rig.AddJobAsync();
        _rig.Sources.Source.OnOpenRead = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await RunRig.WaitUntilAsync(() => !_rig.Sources.Source.Opened.IsEmpty);

        await _rig.Runs.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var run = await _rig.NextCompletedAsync();
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Equal(0, _rig.Snapshotter.ActiveSets);
    }

    [Fact]
    public async Task A_cancellation_while_the_snapshot_is_being_created_is_a_cancellation_not_a_VSS_failure()
    {
        var job = await _rig.AddJobAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Snapshotter.OnCreate = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        _rig.Runs.Cancel(job.Id);

        var run = await _rig.NextCompletedAsync();
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.DoesNotContain(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
        Assert.DoesNotContain(_rig.Memory().FilePaths, p => p.StartsWith("snapshots/", StringComparison.Ordinal));
        Assert.Equal(0, _rig.Connector.OpenConnections);
    }

    [Fact]
    public async Task A_failure_deleting_the_snapshot_does_not_change_the_result_and_releases_the_rest()
    {
        var job = await _rig.AddJobAsync();
        _rig.Snapshotter.DisposeError = new IOException("VSS no responde");

        var run = await RunAsync(job);

        Assert.Equal(RunState.Completed, run.State);
        await RunRig.WaitUntilAsync(() => _rig.Connector.OpenConnections == 0);
        Assert.Equal(1, Assert.Single(_rig.Snapshotter.Sets).Disposals);
    }

    // ---- Criterio 4: sin VSS, copia en vivo con aviso y evento 4000 ----

    [Fact]
    public async Task When_VSS_fails_the_copy_goes_on_live_with_a_warning_and_event_4000()
    {
        _rig.Snapshotter.CreateError = new VolumeSnapshotException("VSS could not create the shadow copies: DoSnapshotSet failed (HRESULT 0x80042306).");
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal((RunState.CompletedWithWarnings, 1L, false), (run.State, run.Stats.Files, run.UsedVss));
        Assert.Equal(1, run.WarningCount);
        Assert.Equal(@"C:\Datos", Assert.Single(Assert.Single(_rig.Sources.Created)).ReadPath); // en vivo, de la ruta final
        Assert.Empty(Assert.Single(_rig.Sources.Devices));

        var entry = Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
        Assert.Equal(4000, entry.EventId);
        Assert.Equal(EventLogLevel.Warning, entry.Level);
        Assert.Contains("Documentos", entry.Message, StringComparison.Ordinal);
        Assert.Contains("0x80042306", entry.Message, StringComparison.Ordinal);

        var detail = await RepositoryCheck.ReadSnapshotHeaderAsync(_rig.Memory(), run.SnapshotId!);
        var warning = Assert.Single(detail.Warnings);
        Assert.Equal(("", nameof(RunIssueCode.VssUnavailable)), (warning.Path, warning.Code));
        Assert.False(detail.UsedVss);
    }

    [Fact]
    public async Task An_unexpected_error_of_the_snapshotter_is_also_a_live_copy_not_a_failed_run()
    {
        _rig.Snapshotter.CreateError = new InvalidOperationException("error interno de VSS");
        var job = await _rig.AddJobAsync();

        var run = await RunAsync(job);

        Assert.Equal(RunState.CompletedWithWarnings, run.State);
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
        Assert.DoesNotContain("error interno", _rig.EventLog.Entries.Single(e => e.EventId == BackboneEventIds.VssUnavailable).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_incomplete_set_is_discarded_and_everything_is_read_live()
    {
        _rig.Inspector.AddFolder(@"D:\Fotos");
        _rig.Sources.Source.AddFile(@"D:\Fotos\a.jpg", "foto");
        _rig.Snapshotter.OmitLastVolume = true;
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Fotos", @"D:\Fotos")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.CompletedWithWarnings, 2L, false), (run.State, run.Stats.Files, run.UsedVss));
        Assert.Equal([@"C:\Datos", @"D:\Fotos"], Assert.Single(_rig.Sources.Created).Select(s => s.ReadPath));
        Assert.Equal(0, _rig.Snapshotter.ActiveSets); // lo creado se elimina
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
    }

    [Fact]
    public async Task Two_runs_in_a_row_each_create_and_delete_their_own_snapshot()
    {
        var job = await _rig.AddJobAsync();
        await RunAsync(job);
        await RunRig.WaitUntilAsync(() => _rig.Runs.GetActivity(job.Id) == JobActivity.Idle);

        await RunAsync(job);

        Assert.Equal(2, _rig.Snapshotter.Sets.Count);
        await RunRig.WaitUntilAsync(() => _rig.Snapshotter.ActiveSets == 0);
    }

    // ---- Ampliación de QA ----

    [Fact]
    public async Task A_volume_without_VSS_support_mixed_with_one_that_has_it_reads_everything_live_with_one_warning_and_event_4000()
    {
        _rig.Inspector.AddFolder(@"E:\Usb");
        _rig.Sources.Source.AddFile(@"E:\Usb\u.txt", "usb");
        _rig.Snapshotter.UnsupportedVolumes.Add(VolumeOf('E'));
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Usb", @"E:\Usb")]);

        var run = await RunAsync(job);

        Assert.Equal((RunState.CompletedWithWarnings, 2L, false, 1), (run.State, run.Stats.Files, run.UsedVss, run.WarningCount));
        Assert.Equal([@"C:\Datos", @"E:\Usb"], Assert.Single(_rig.Sources.Created).Select(s => s.ReadPath));
        Assert.Empty(_rig.Snapshotter.Sets);
        Assert.Single(_rig.EventLog.Entries, e => e.EventId == BackboneEventIds.VssUnavailable);
        var detail = await RepositoryCheck.ReadSnapshotHeaderAsync(_rig.Memory(), run.SnapshotId!);
        Assert.False(detail.UsedVss);
    }

    [Fact]
    public async Task A_readable_source_next_to_an_unreadable_one_uses_VSS_only_for_the_readable_volume()
    {
        _rig.Inspector.AddStatus(@"D:\Falta", LocalFolderStatus.NotFound);
        var job = await _rig.AddJobAsync(sources: [("Datos", @"C:\Datos"), ("Falta", @"D:\Falta")]);

        var run = await RunAsync(job);

        Assert.True(run.UsedVss);
        Assert.Equal([VolumeOf('C')], Assert.Single(_rig.Snapshotter.Requests));
        Assert.Single(Assert.Single(_rig.Snapshotter.Sets).Snapshots);
        await RunRig.WaitUntilAsync(() => _rig.Snapshotter.ActiveSets == 0);
    }

    [Fact]
    public async Task A_cancelled_run_has_no_snapshot_and_its_VSS_snapshot_is_gone()
    {
        var job = await _rig.AddJobAsync();
        var (entered, release) = _rig.HoldAtFirstFile();
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);
        await entered.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        _rig.Runs.Cancel(job.Id);

        var run = await _rig.NextCompletedAsync();

        Assert.Equal((RunState.Cancelled, (string?)null), (run.State, run.SnapshotId));
        Assert.Equal(0, _rig.Snapshotter.ActiveSets);
        release.TrySetResult();
    }

    [Fact]
    public async Task A_snapshot_deletion_that_never_returns_does_not_block_the_run_nor_the_connection()
    {
        using var never = new CancellationTokenSource();
        _rig.Snapshotter.OnDispose = () => Task.Delay(Timeout.Infinite, never.Token);
        var job = await _rig.AddJobAsync();
        _rig.Runs.Enqueue(job.Id, RunTrigger.Manual);

        var completion = _rig.NextCompletedAsync(30);
        for (var i = 0; i < 400 && !completion.IsCompleted; i++)
        {
            await Task.Delay(10, CancellationToken.None);
            _rig.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var run = await completion;
        Assert.Equal(RunState.Completed, run.State);
        await RunRig.WaitUntilAsync(() => _rig.Connector.OpenConnections == 0);
        Assert.Equal(1, Assert.Single(_rig.Snapshotter.Sets).Disposals);
        await never.CancelAsync();
    }
}
