using Backbone.Service.Hosting;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Os;

/// <summary>HU-15: traducción de rutas a la instantánea, registro de instantáneas, limpieza al arrancar y cableado en el host.</summary>
[Trait("Story", "HU-15")]
public sealed class VssSupportTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // ---- VssPaths ----

    [Theory]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\Users\ana\Documentos", @"\\?\Volume{00000000-0000-0000-0000-000000000043}\", @"Users\ana\Documentos")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\Datos\", @"\\?\Volume{00000000-0000-0000-0000-000000000043}\", "Datos")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\", @"\\?\Volume{00000000-0000-0000-0000-000000000043}\", "")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}", @"\\?\Volume{00000000-0000-0000-0000-000000000043}\", "")]
    public void A_final_path_in_volume_guid_form_is_split_into_volume_and_relative_path(string path, string volume, string relative)
    {
        Assert.True(VssPaths.TrySplit(path, out var v, out var r));
        Assert.Equal((volume, relative), (v, r));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Datos")]
    [InlineData(@"\\?\C:\Datos")]
    [InlineData(@"\\?\Volume{no-es-un-guid}\Datos")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\a\..\b")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\a\\b")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000043}\.\a")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\a")]
    public void Anything_else_does_not_translate(string? path) => Assert.False(VssPaths.TrySplit(path, out _, out _));

    [Fact]
    public void The_read_path_in_the_snapshot_is_the_device_plus_the_relative_path()
    {
        const string Device = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7";
        Assert.Equal(Device + @"\Users\ana", VssPaths.Combine(Device, @"Users\ana"));
        Assert.Equal(Device, VssPaths.Combine(Device, ""));
    }

    // ---- Registro ----

    private VssSnapshotRegistry NewRegistry(ILoggerFactory? factory = null)
    {
        Directory.CreateDirectory(_temp.Combine("data"));
        return new VssSnapshotRegistry(_temp.Combine("data", "vss-snapshots.json"), TestFileTrust.CurrentUser, TimeProvider.System, (factory ?? NullLoggerFactory.Instance).CreateLogger<VssSnapshotRegistry>());
    }

    [Fact]
    public async Task The_registry_keeps_what_is_added_and_forgets_what_is_removed()
    {
        using var registry = NewRegistry();
        var owner = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        await registry.AddAsync(owner, a, [(snapshot, @"\\?\Volume{00000000-0000-0000-0000-000000000043}\")], Ct);
        await registry.AddAsync(owner, b, Array.Empty<(Guid, string)>(), Ct);

        using var reopened = NewRegistry();
        var all = await reopened.LoadAsync(Ct);
        Assert.Equal(new[] { a, b }.Order(), all.Select(r => r.SetId).Order());
        Assert.Equal(snapshot, Assert.Single(all.Single(r => r.SetId == a).Snapshots.OfType<VssSnapshotRecord>()).SnapshotId);

        await registry.RemoveAsync([a], Ct);
        Assert.Equal(b, Assert.Single(await reopened.LoadAsync(Ct)).SetId);
    }

    [Fact]
    public async Task Entries_with_empty_ids_in_the_file_are_ignored()
    {
        using var registry = NewRegistry();
        await File.WriteAllTextAsync(registry.FilePath, """{"schemaVersion":1,"sets":[null,{"setId":"00000000-0000-0000-0000-000000000000","snapshots":[]},{"setId":"11111111-1111-1111-1111-111111111111","owner":"22222222-2222-2222-2222-222222222222","snapshots":[null,{"snapshotId":"00000000-0000-0000-0000-000000000000","volume":"x"}]}]}""", Ct);

        var set = Assert.Single(await registry.LoadAsync(Ct));

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), set.SetId);
        Assert.Empty(set.Snapshots);
    }

    // ---- Limpieza al arrancar ----

    [Fact]
    public async Task The_startup_cleanup_asks_the_snapshotter_in_the_background_and_never_throws()
    {
        var snapshotter = new FakeVolumeSnapshotter { OrphansToReport = 2 };
        using var cleanup = new VssStartupCleanup(snapshotter, NullLogger<VssStartupCleanup>.Instance);

        await cleanup.StartAsync(Ct);
        await RunRig.WaitUntilAsync(() => snapshotter.OrphanCalls == 1);
        await cleanup.StopAsync(Ct);

        snapshotter.OrphanError = new InvalidOperationException("VSS roto");
        await cleanup.CleanAsync(); // no lanza
        Assert.Equal(2, snapshotter.OrphanCalls);
    }

    // ---- Cableado ----

    [Fact]
    public void The_host_wires_the_real_snapshotter_the_registry_in_the_data_folder_and_the_startup_cleanup()
    {
        using var host = BackboneHost.CreateBuilder(new BackboneHostOptions { DataRoot = _temp.Path }).Build();

        Assert.IsType<VssSnapshotter>(host.Services.GetRequiredService<IVolumeSnapshotter>());
        Assert.Equal(Path.Combine(_temp.Path, "data", "vss-snapshots.json"), host.Services.GetRequiredService<VssSnapshotRegistry>().FilePath);
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is VssStartupCleanup);
    }

    // ---- Interfaz con COM: sin elevar, solo comprueba que la API existe y que un fallo es un VssException ----

    [Fact]
    public async Task The_native_entry_points_exist_and_a_failure_to_open_VSS_is_a_VssException()
    {
        // Sin elevar (o con VSS parado) falla con E_ACCESSDENIED u otro HRESULT; elevado, crea el objeto y lo suelta. En los dos
        // casos CreateVssBackupComponentsInternal existe en vssapi.dll y la tabla de métodos de InitializeForBackup responde.
        using var worker = new VssWorker();
        try
        {
            var opened = await worker.RunAsync(() =>
            {
                using var backup = new VssBackupFactory().Create();
                return true;
            });
            Assert.True(opened);
        }
        catch (VssException ex)
        {
            Assert.True(ex.HResult < 0);
        }
    }
}
