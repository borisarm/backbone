using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// HU-08, de extremo a extremo con el motor real (<see cref="BackupEngine"/>) sobre <see cref="SourceFileSystem"/> con el
/// sistema de archivos simulado: lo que el usuario ve en el snapshot y en los avisos para enlaces, archivos y carpetas solo
/// en la nube, lista blanca, etiquetas desconocidas y exclusiones, y que nunca se guarda un archivo de ceros.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceFileSystemEngineQaTests : IDisposable
{
    private const uint CloudTag = 0x9000601A;
    private const FileAttributes Reparse = FileAttributes.ReparsePoint;

    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly InMemoryRepositoryStorage _storage = new();

    public void Dispose() => _api.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HU08_3_4_Links_cloud_only_items_and_unknown_tags_are_skipped_with_their_warning_whitelisted_ones_are_copied_and_nothing_is_zeros()
    {
        var normal = RandomNumberGenerator.GetBytes(10_000);
        var wof = RandomNumberGenerator.GetBytes(6_000);
        var hydrated = RandomNumberGenerator.GetBytes(7_000);
        var inFolder = RandomNumberGenerator.GetBytes(300);
        _api.AddFile("a.txt", normal);
        _api.AddFile("wof.bin", wof, Reparse, ReparseTags.Wof);
        _api.AddFile("descargado.bin", hydrated, Reparse, CloudTag);
        _api.AddFile("nube.bin", new byte[50_000], Reparse | SourceAttributes.RecallOnDataAccess, CloudTag); // ceros si se abriera
        _api.AddDirectory("enlace", Reparse, ReparseTags.MountPoint);
        _api.AddFile(@"enlace\secreto.txt", [1, 2, 3]);
        _api.AddFile("simbolico.txt", [1], Reparse, ReparseTags.Symlink);
        _api.AddFile("proj.bin", [1], Reparse, ReparseTags.ProjFs);
        _api.AddDirectory("nube-vacia", FileAttributes.Directory | Reparse | SourceAttributes.RecallOnOpen, CloudTag);
        _api.AddFile(@"nube-vacia\hijo.txt", [9], Reparse | SourceAttributes.RecallOnDataAccess, CloudTag);
        _api.AddDirectory("sub");
        _api.AddFile(@"sub\b.txt", inFolder);
        _api.AddFile(@"node_modules\x.js", [1]);
        _api.AddDirectory("node_modules");
        _api.AddFile("basura.tmp", [1]);

        using var fs = CreateFileSystem();
        var result = await RunAsync(fs, exclusions: ["*.tmp", "**/node_modules/**"]);

        var entries = await ReadEntriesAsync(result);
        var files = entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, e => e);
        Assert.Equal<IEnumerable<string>>(
            ["Docs/a.txt", "Docs/descargado.bin", "Docs/sub/b.txt", "Docs/wof.bin"],
            files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Sha(normal), files["Docs/a.txt"].Hash!.ToString());
        Assert.Equal(Sha(wof), files["Docs/wof.bin"].Hash!.ToString());
        Assert.Equal(Sha(hydrated), files["Docs/descargado.bin"].Hash!.ToString());
        Assert.Equal(Sha(inFolder), files["Docs/sub/b.txt"].Hash!.ToString());

        var warnings = result.Warnings.Select(w => (w.Path, w.Code)).OrderBy(w => w.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                ("Docs/enlace", nameof(RunIssueCode.ReparsePointSkipped)),
                ("Docs/nube-vacia", nameof(RunIssueCode.CloudOnlySkipped)),
                ("Docs/nube.bin", nameof(RunIssueCode.CloudOnlySkipped)),
                ("Docs/proj.bin", nameof(RunIssueCode.ReadError)),
                ("Docs/simbolico.txt", nameof(RunIssueCode.ReparsePointSkipped)),
            },
            warnings);
        Assert.Contains(ReparseTags.Format(ReparseTags.ProjFs), result.Warnings.Single(w => w.Code == nameof(RunIssueCode.ReadError)).Message, StringComparison.Ordinal);

        // Lo que no se lee ni se abre; lo excluido, ni siquiera se recorre.
        var opened = _api.Opens.Select(o => o.Path).ToList();
        Assert.DoesNotContain(opened, p => p.Contains("nube.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(opened, p => p.Contains("secreto.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(opened, p => p.Contains("hijo.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(opened, p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(opened, p => p.EndsWith("basura.tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);

        // Ningún objeto del repositorio es el de un archivo de ceros (el tamaño de nube.bin, 50 000 bytes, es todo ceros).
        Assert.DoesNotContain(entries, e => e.Hash?.ToString() == Sha(new byte[50_000]));
        Assert.DoesNotContain(_storage.FilePaths, p => p.Contains(Sha(new byte[50_000]), StringComparison.Ordinal));
    }

    [Fact]
    public async Task HU08_4_A_file_that_goes_cloud_only_between_runs_keeps_its_previous_version_and_returns_when_downloaded_and_a_hung_one_is_not_left_half_written()
    {
        var content = RandomNumberGenerator.GetBytes(9_000);
        var node = _api.AddFile("doc.bin", content, Reparse, CloudTag);
        using (var fs = CreateFileSystem())
        {
            await RunAsync(fs);
        }

        _time.Advance(TimeSpan.FromDays(1));
        node.Attributes = Reparse | SourceAttributes.RecallOnDataAccess; // "Liberar espacio"
        node.Content = new byte[9_000]; // lo que daría leerlo sin los datos
        ISourceFileSystem second = CreateFileSystem();
        var kept = await RunAsync(second);
        (second as IDisposable)?.Dispose();

        var keptEntry = (await ReadEntriesAsync(kept)).Single(e => !e.IsDirectory);
        Assert.Equal(Sha(content), keptEntry.Hash!.ToString()); // la versión del padre
        Assert.Equal(nameof(RunIssueCode.CloudOnlySkipped), Assert.Single(kept.Warnings).Code);

        _time.Advance(TimeSpan.FromDays(1));
        node.Attributes = Reparse; // descargado otra vez, con otro contenido
        var newer = RandomNumberGenerator.GetBytes(9_100);
        node.Content = newer;
        using var third = CreateFileSystem();
        var back = await RunAsync(third);

        Assert.Empty(back.Warnings);
        Assert.Equal(Sha(newer), (await ReadEntriesAsync(back)).Single(e => !e.IsDirectory).Hash!.ToString());
    }

    /// <summary>
    /// Decisión del coordinador (HU-08): un archivo que pasa a ser una carpeta real entre la enumeración y la apertura da un
    /// aviso <c>ReadError</c>, conserva su versión anterior y no se recorre en esa ejecución.
    /// </summary>
    [Fact]
    public async Task A_file_that_becomes_a_real_folder_during_the_run_keeps_its_previous_version_and_is_not_walked()
    {
        var content = RandomNumberGenerator.GetBytes(5_000);
        _api.AddFile("x.bin", content);
        using (var fs = CreateFileSystem())
        {
            await RunAsync(fs);
        }

        _time.Advance(TimeSpan.FromDays(1));
        var folder = _api.AddDirectory("x.bin"); // sustituye al archivo
        folder.EnumerationAttributes = FileAttributes.Archive; // la enumeración aún lo vio como archivo
        _api.AddFile(@"x.bin\dentro.txt", [7]);
        using var second = CreateFileSystem();
        var result = await RunAsync(second);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("Docs/x.bin", nameof(RunIssueCode.ReadError)), (warning.Path, warning.Code));
        var entries = await ReadEntriesAsync(result);
        var kept = Assert.Single(entries, e => !e.IsDirectory);
        Assert.Equal("Docs/x.bin", kept.Path);
        Assert.Equal(Sha(content), kept.Hash!.ToString());
        Assert.DoesNotContain(_api.RelativeOpens, o => o.Name == "dentro.txt");
    }

    [Fact]
    public async Task HU08_A_source_operation_that_never_returns_ends_the_run_with_an_exception_and_publishes_no_snapshot()
    {
        _api.AddFile("a.txt", [1]);
        var stuck = _api.AddFile("b.txt", [2]);
        stuck.BlockAt = FakeBlockAt.Open;
        stuck.BlockMode = FakeBlockMode.IgnoreCancellation;
        using var fs = CreateFileSystem();

        var run = RunAsync(fs);
        for (var i = 0; i < 400 && !run.IsCompleted; i++)
        {
            await Task.Delay(10, CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(run.IsCompleted);
        // Debe llegar tal cual al servicio (que la traduce a Failed/Internal), no convertida en un aviso de un elemento.
        await Assert.ThrowsAsync<SourceOperationAbandonedException>(() => run);
        Assert.True(fs.IsAbandoned);
        var repository = await BackupRepository.OpenAsync(_storage, _time, Ct);
        Assert.Empty(await repository.ListSnapshotIdsAsync(Ct));
    }

    // ================================================================== Ayudas

    private SourceFileSystem CreateFileSystem() => new(
        [Source],
        _api,
        _time,
        SourceReadOptions.Default with { ReadBlockBytes = 4096 },
        new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());

    private static BackupSource Source => new("Docs", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath);

    private async Task<BackupResult> RunAsync(ISourceFileSystem source, IReadOnlyList<string>? exclusions = null)
    {
        var probe = await BackupRepository.ProbeAsync(_storage, Ct);
        var repository = probe.State == Backbone.Core.Repository.RepositoryState.Compatible
            ? await BackupRepository.OpenAsync(_storage, _time, Ct)
            : await BackupRepository.InitializeAsync(_storage, new RepositoryInitializationOptions { CreatedBy = "Backbone pruebas" }, _time, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, new LockOwner("PC-PRUEBA", 4242), Ct);
        var engine = new BackupEngine(_time);
        var result = await engine.RunAsync(
            repository,
            repositoryLock,
            source,
            new BackupRequest
            {
                JobId = "1b2c3d4e-0000-0000-0000-0000000000aa",
                JobName = "Prueba",
                Host = "PC-PRUEBA",
                Sources = [Source],
                Exclusions = exclusions ?? [],
            },
            null,
            Ct);
        await repositoryLock.ReleaseAsync(Ct);
        return result;
    }

    private async Task<List<SnapshotEntry>> ReadEntriesAsync(BackupResult result)
    {
        var repository = await BackupRepository.OpenAsync(_storage, TimeProvider.System, Ct);
        var entries = new List<SnapshotEntry>();
        await using var reader = await repository.OpenSnapshotAsync(result.Snapshot.Id, Ct);
        await foreach (var entry in reader.ReadEntriesAsync(Ct))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
