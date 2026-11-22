using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Sources;

/// <summary>
/// <see cref="SourceFileSystem"/> con el sistema de archivos real (HU-08, §8.2, ADR-18, ADR-19). No necesitan elevación
/// (los <i>junction</i> los crea cualquier usuario); el enlace simbólico se omite si el proceso no puede crearlo. OneDrive y
/// VSS quedan en PM-HU08-01.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-08")]
public sealed class SourceFileSystemIntegrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly List<SourceFileSystem> _created = [];

    public SourceFileSystemIntegrationTests()
    {
        _root = Directory.CreateDirectory(_temp.Combine("origen")).FullName;
    }

    public void Dispose()
    {
        foreach (var fs in _created)
        {
            fs.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task A_normal_tree_is_enumerated_and_read_with_its_real_content()
    {
        var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 5);
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.bin"), content, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var fs = Create(_root);
        var readPath = FinalPath(_root);

        var entries = await ListAsync(fs, readPath);

        Assert.Equal(["a.bin", "sub"], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        var a = entries.Single(e => e.Name == "a.bin");
        Assert.Equal(content.Length, a.Length);
        Assert.Equal(SourceEntryKind.Normal, SourceAttributes.Classify(a));
        Assert.Equal(SHA256.HashData(content), await HashAsync(fs, a.FullPath));
        var info = await fs.GetFileInfoAsync(a.FullPath, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal(content.Length, info.Length);
        Assert.Equal(a.LastWriteTimeUtc, info.LastWriteTimeUtc);
    }

    [Fact]
    public async Task A_junction_is_enumerated_with_its_tag_and_never_followed()
    {
        var outside = Directory.CreateDirectory(_temp.Combine("fuera")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secreto.txt"), "secreto", TestContext.Current.CancellationToken);
        CreateJunction(Path.Combine(_root, "enlace"), outside);
        var fs = Create(_root);
        var readPath = FinalPath(_root);

        var link = (await ListAsync(fs, readPath)).Single(e => e.Name == "enlace");

        Assert.Equal(ReparseTags.MountPoint, link.ReparseTag);
        Assert.Equal(SourceEntryKind.Link, SourceAttributes.Classify(link));
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(fs, link.FullPath))).Code);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, link.FullPath + @"\secreto.txt"))).Code);
    }

    [Fact]
    public async Task A_symbolic_link_to_a_file_is_not_followed()
    {
        var target = _temp.Combine("objetivo.txt");
        await File.WriteAllTextAsync(target, "secreto", TestContext.Current.CancellationToken);
        var linkPath = Path.Combine(_root, "enlace.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This process cannot create symbolic links (needs Developer Mode or elevation).");
        }

        var fs = Create(_root);
        var link = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "enlace.txt");

        Assert.Equal(ReparseTags.Symlink, link.ReparseTag);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, link.FullPath))).Code);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => fs.GetFileInfoAsync(link.FullPath, TestContext.Current.CancellationToken))).Code);
    }

    /// <summary>
    /// Riesgo señalado por backbone-core: si la enumeración diera <c>FILE_ATTRIBUTE_REPARSE_POINT</c> con etiqueta 0 para un
    /// archivo WOF, el motor lo daría por no legible. En Windows 11 26320 WOF no expone su etiqueta (atributos 0x20, EaSize 0).
    /// </summary>
    [Fact]
    public async Task A_wof_compressed_file_is_read_with_its_real_sha256()
    {
        var path = Path.Combine(_root, "wof.bin");
        var content = new byte[2 * 1024 * 1024];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i / 7 % 50);
        }

        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        if (!RunTool("compact.exe", $"/c /exe:xpress4k \"{path}\""))
        {
            Assert.Skip("compact /exe:xpress4k is not available on this volume.");
        }

        var fs = Create(_root);
        var entry = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "wof.bin");
        TestContext.Current.SendDiagnosticMessage($"WOF: attributes=0x{(uint)entry.Attributes:X} tag=0x{entry.ReparseTag:X}");

        Assert.False(
            (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.ReparseTag == 0,
            $"WOF enumerated as a reparse point with tag 0 (attributes 0x{(uint)entry.Attributes:X}): the engine would treat it as unreadable.");
        Assert.Contains(SourceAttributes.Classify(entry), new[] { SourceEntryKind.Normal, SourceEntryKind.Content });
        Assert.Equal(SHA256.HashData(content), await HashAsync(fs, entry.FullPath));
    }

    [Fact]
    public async Task Long_paths_are_enumerated_and_read()
    {
        var deep = _root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 50));
        }

        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "largo.txt");
        await File.WriteAllTextAsync(file, "contenido", TestContext.Current.CancellationToken);
        var fs = Create(_root);
        var readPath = FinalPath(_root);

        var current = readPath;
        SourceEntry? found = null;
        while (found is null)
        {
            var entries = await ListAsync(fs, current);
            found = entries.SingleOrDefault(e => e.Name == "largo.txt");
            current = found is null ? entries.Single(e => e.IsDirectory).FullPath : current;
        }

        Assert.True(found.FullPath.Length > 400);
        Assert.Equal(SHA256.HashData("contenido"u8), await HashAsync(fs, found.FullPath));
    }

    [Fact]
    public async Task A_root_that_is_a_junction_is_not_enumerated()
    {
        var real = Directory.CreateDirectory(_temp.Combine("real")).FullName;
        await File.WriteAllTextAsync(Path.Combine(real, "a.txt"), "x", TestContext.Current.CancellationToken);
        var junction = _temp.Combine("raiz-enlace");
        CreateJunction(junction, real);
        var fs = Create(junction);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(fs, junction));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
    }

    /// <summary>Prueba obligatoria de M-A: una carpeta intermedia ya enumerada se sustituye por un <i>junction</i>.</summary>
    [Fact]
    public async Task An_intermediate_folder_replaced_by_a_junction_after_enumeration_is_not_read()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        await File.WriteAllTextAsync(Path.Combine(a, "secreto.txt"), "propio", TestContext.Current.CancellationToken);
        var secret = Directory.CreateDirectory(_temp.Combine("credentials")).FullName;
        await File.WriteAllTextAsync(Path.Combine(secret, "secreto.txt"), "SECRETO", TestContext.Current.CancellationToken);
        var fs = Create(_root);
        var folder = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "A");

        Directory.Move(a, Path.Combine(_root, "A-movida"));
        CreateJunction(a, secret);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, folder.FullPath + @"\secreto.txt"));
        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(fs, folder.FullPath))).Code);
    }

    /// <summary>Plazo real: una E/S síncrona bloqueada (lectura de un pipe sin datos) se cancela con <c>CancelSynchronousIo</c>.</summary>
    [Fact]
    public async Task A_blocked_synchronous_operation_is_cancelled_with_CancelSynchronousIo_when_the_deadline_expires()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var options = SourceReadOptions.Default with
        {
            OperationTimeout = TimeSpan.FromMilliseconds(300),
            CancellationGrace = TimeSpan.FromSeconds(10),
            RecancelInterval = TimeSpan.FromMilliseconds(100),
        };
        using var runner = new SourceOperationRunner(TimeProvider.System, options, NullLogger.Instance);
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => runner.RunAsync(
            "prueba",
            "pipe",
            null,
            null,
            _ => client.Read(new byte[1], 0, 1), // ReadFile síncrono: se bloquea hasta que lo cancelen
            TestContext.Current.CancellationToken));

        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.False(runner.IsAbandoned);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"Took {watch.Elapsed}.");

        // El hilo sigue disponible para la siguiente operación.
        Assert.Equal(42, await runner.RunAsync("prueba", "x", null, null, _ => 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Allocated_ranges_detect_holes_in_a_sparse_file()
    {
        var full = Path.Combine(_root, "lleno.bin");
        File.WriteAllBytes(full, RandomNumberGenerator.GetBytes(256 * 1024));
        var sparse = Path.Combine(_root, "hueco.bin");
        File.WriteAllBytes(sparse, RandomNumberGenerator.GetBytes(256 * 1024));
        if (!RunTool("fsutil.exe", $"sparse setflag \"{sparse}\"") || !RunTool("fsutil.exe", $"sparse setrange \"{sparse}\" 65536 131072"))
        {
            Assert.Skip("fsutil sparse is not available to this process.");
        }

        using (var handle = SourceHandles.Open(full, SourceOpenMode.Content))
        {
            Assert.True(handle.IsFullyAllocated(256 * 1024, CancellationToken.None));
        }

        using (var handle = SourceHandles.Open(sparse, SourceOpenMode.Content))
        {
            Assert.False(handle.IsFullyAllocated(256 * 1024, CancellationToken.None));
        }
    }

    [Fact]
    public void Placeholders_can_be_exposed_in_this_process() =>
        Assert.True(new WindowsPlaceholderCompatibility().ExposePlaceholders() >= 0);

    private static string FinalPath(string path) => SourceHandles.FinalPath(path);

    private static void CreateJunction(string link, string target) =>
        Assert.True(RunTool("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\""), "mklink /J failed.");

    private static bool RunTool(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null)
        {
            return false;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task<List<SourceEntry>> ListAsync(SourceFileSystem fs, string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in fs.EnumerateAsync(path, TestContext.Current.CancellationToken))
        {
            list.Add(entry);
        }

        return list;
    }

    private static async Task<byte[]> HashAsync(SourceFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, TestContext.Current.CancellationToken);
        return await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
    }

    private SourceFileSystem Create(string root)
    {
        // ReadPath es la ruta final de la raíz (ADR-20 punto 6). Para una raíz que es un junction se pasa tal cual.
        var readPath = Directory.ResolveLinkTarget(root, returnFinalTarget: false) is null ? FinalPath(root) : root;
        var fs = new SourceFileSystem(
            [new BackupSource("origen", root, readPath)],
            new Win32SourceFileApi(),
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLoggerFactory.Instance.CreateLogger<SourceFileSystem>());
        _created.Add(fs);
        return fs;
    }
}
