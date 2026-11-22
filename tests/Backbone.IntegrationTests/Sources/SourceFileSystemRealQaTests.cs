using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Sources;

/// <summary>
/// HU-08, auditoría de QA de <see cref="SourceFileSystem"/> con el sistema de archivos real (sin elevar): análisis de
/// <c>FILE_ID_BOTH_DIR_INFO</c> con miles de entradas y nombres difíciles, códigos de error reales de Windows, rangos
/// asignados en archivos dispersos, enlaces que sustituyen a un archivo ya enumerado y cancelación. OneDrive, Dedup y VSS
/// siguen en PM-HU08-01.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-08")]
public sealed class SourceFileSystemRealQaTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;
    private readonly List<SourceFileSystem> _created = [];

    public SourceFileSystemRealQaTests()
    {
        _root = Directory.CreateDirectory(_temp.Combine("origen")).FullName;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var fs in _created)
        {
            fs.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task HU08_A_folder_with_thousands_of_entries_and_difficult_names_is_enumerated_once_each_with_exact_data()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "muchos")).FullName;
        var names = new List<string>();
        for (var i = 0; i < 3000; i++)
        {
            names.Add($"archivo {i:D5}.txt");
        }

        names.Add("Cafe\u0301.txt"); // NFD
        names.Add("Caf\u00E9.txt"); // NFC: otro archivo distinto en NTFS
        names.Add("\U0001F600 \U0001F4C1.txt");
        names.Add(new string('n', 200) + ".txt");
        names.Add("año ñandú.TXT");
        names.Add("vacío.txt");
        foreach (var name in names)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, name), name == "vacío.txt" ? [] : [1, 2, 3], Ct);
        }

        var hidden = Path.Combine(folder, "oculto.ini");
        await File.WriteAllTextAsync(hidden, "x", Ct);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);
        names.Add("oculto.ini");
        Directory.CreateDirectory(Path.Combine(folder, "carpeta ñ"));
        names.Add("carpeta ñ");
        var fs = Create(_root);

        var entries = await ListAsync(fs, Path.Combine(FinalPath(_root), "muchos"));

        Assert.Equal(names.Count, entries.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, entries.Single(e => e.Name == "vacío.txt").Length);
        Assert.Equal(3, entries.Single(e => e.Name == "Cafe\u0301.txt").Length);
        Assert.True(entries.Single(e => e.Name == "carpeta ñ").IsDirectory);
        var hiddenEntry = entries.Single(e => e.Name == "oculto.ini");
        Assert.Equal(FileAttributes.Hidden | FileAttributes.ReadOnly, hiddenEntry.Attributes & (FileAttributes.Hidden | FileAttributes.ReadOnly));
        Assert.All(entries, e => Assert.Equal(0u, e.ReparseTag)); // EaSize sin punto de reanálisis no es una etiqueta
        Assert.All(entries.Where(e => !e.IsDirectory), e => Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(folder, e.Name)), e.LastWriteTimeUtc));
    }

    [Fact]
    public async Task HU08_A_zero_length_file_is_read_as_empty_and_a_missing_file_is_null_in_GetFileInfo()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "vacio.bin"), [], Ct);
        var fs = Create(_root);
        var readPath = FinalPath(_root);

        await using (var stream = await fs.OpenReadAsync(readPath + @"\vacio.bin", Ct))
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[16], Ct));
        }

        var info = await fs.GetFileInfoAsync(readPath + @"\vacio.bin", Ct);
        Assert.Equal(0, info?.Length);
        Assert.Null(await fs.GetFileInfoAsync(readPath + @"\no-existe.bin", Ct));
        Assert.Null(await fs.GetFileInfoAsync(readPath + @"\no-existe\dentro.bin", Ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => fs.OpenReadAsync(readPath + @"\no-existe.bin", Ct));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => fs.OpenReadAsync(readPath + @"\no-existe\dentro.bin", Ct));
    }

    [Fact]
    public async Task HU08_A_file_held_exclusively_by_another_handle_is_FileLocked()
    {
        var path = Path.Combine(_root, "bloqueado.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fs = Create(_root);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => fs.OpenReadAsync(FinalPath(_root) + @"\bloqueado.bin", Ct));

        Assert.Equal(RunIssueCode.FileLocked, ex.Code);
    }

    [Fact]
    public async Task HU08_A_file_the_account_cannot_read_is_UnauthorizedAccess_and_the_next_one_is_read()
    {
        var denied = Path.Combine(_root, "denegado.bin");
        var allowed = Path.Combine(_root, "permitido.bin");
        await File.WriteAllBytesAsync(denied, [1], Ct);
        await File.WriteAllBytesAsync(allowed, [2], Ct);
        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes, AccessControlType.Deny);
        var info = new FileInfo(denied);
        try
        {
            var security = info.GetAccessControl();
            security.AddAccessRule(rule);
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Cannot change the ACL of a temporary file in this environment.");
        }

        try
        {
            var fs = Create(_root);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fs.OpenReadAsync(FinalPath(_root) + @"\denegado.bin", Ct));
            await using var stream = await fs.OpenReadAsync(FinalPath(_root) + @"\permitido.bin", Ct);
            Assert.Equal(1, await stream.ReadAsync(new byte[4], Ct));
        }
        finally
        {
            var security = info.GetAccessControl();
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task HU08_3_A_file_replaced_by_a_symbolic_link_after_enumeration_is_not_read_and_a_directory_link_is_not_followed()
    {
        var secret = _temp.Combine("secreto.txt");
        await File.WriteAllTextAsync(secret, "SECRETO", Ct);
        var victim = Path.Combine(_root, "victima.txt");
        await File.WriteAllTextAsync(victim, "propio", Ct);
        var outside = Directory.CreateDirectory(_temp.Combine("fuera")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "x.txt"), "x", Ct);
        var fs = Create(_root);
        var listed = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "victima.txt");
        Assert.Equal(SourceEntryKind.Normal, SourceAttributes.Classify(listed));

        File.Delete(victim);
        try
        {
            File.CreateSymbolicLink(victim, secret);
            Directory.CreateSymbolicLink(Path.Combine(_root, "dir-simbolico"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This process cannot create symbolic links (needs Developer Mode or elevation).");
        }

        var replaced = await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, listed.FullPath));
        Assert.Equal(RunIssueCode.ReparsePointSkipped, replaced.Code);
        var link = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "dir-simbolico");
        Assert.Equal(SourceEntryKind.Link, SourceAttributes.Classify(link));
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(fs, link.FullPath))).Code);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, link.FullPath + @"\x.txt"))).Code);
    }

    [Fact]
    public async Task HU08_A_file_replaced_by_a_junction_to_a_folder_is_not_read_as_a_file()
    {
        var victim = Path.Combine(_root, "victima");
        await File.WriteAllTextAsync(victim, "propio", Ct);
        var outside = Directory.CreateDirectory(_temp.Combine("fuera")).FullName;
        var fs = Create(_root);
        var listed = (await ListAsync(fs, FinalPath(_root))).Single(e => e.Name == "victima");

        File.Delete(victim);
        Assert.True(RunTool("cmd.exe", $"/c mklink /J \"{victim}\" \"{outside}\""));

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => HashAsync(fs, listed.FullPath));
        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
    }

    [Fact]
    public async Task HU08_A_cancelled_token_throws_OperationCanceledException_without_leaving_a_handle_open()
    {
        var path = Path.Combine(_root, "a.bin");
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(4 * 1024 * 1024), Ct);
        var fs = Create(_root);
        var readPath = FinalPath(_root) + @"\a.bin";
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fs.OpenReadAsync(readPath, cancelled.Token));

        // El archivo no queda bloqueado por un handle que se hubiera dejado abierto.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task HU08_A_file_with_a_trailing_dot_or_space_in_its_name_is_enumerated_and_read_when_the_system_allows_it()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "raros")).FullName;
        var dotted = @"\\?\" + folder + @"\punto.";
        var spaced = @"\\?\" + folder + @"\espacio ";
        try
        {
            await File.WriteAllBytesAsync(dotted, [1, 2], Ct);
            await File.WriteAllBytesAsync(spaced, [3], Ct);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            Assert.Skip("This volume does not allow names with a trailing dot or space.");
        }

        var fs = Create(_root);
        var entries = await ListAsync(fs, FinalPath(_root) + @"\raros");

        Assert.Equal(["espacio ", "punto."], entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new byte[] { 1, 2 }, await ReadAsync(fs, entries.Single(e => e.Name == "punto.").FullPath));
        Assert.Equal(new byte[] { 3 }, await ReadAsync(fs, entries.Single(e => e.Name == "espacio ").FullPath));
    }

    // ================================================================== Rangos asignados reales

    [Fact]
    public void HU08_4_Allocated_ranges_detect_a_hole_at_the_start_a_hole_at_the_end_and_an_all_hole_file()
    {
        const int size = 256 * 1024;
        var leading = SparseFile("inicio.bin", size, holeOffset: 0, holeLength: 65536);
        var trailing = SparseFile("final.bin", size, holeOffset: size - 65536, holeLength: 65536);
        var all = SparseFile("todo.bin", size, holeOffset: 0, holeLength: size);
        var none = Path.Combine(_root, "lleno.bin");
        File.WriteAllBytes(none, RandomNumberGenerator.GetBytes(size));
        var empty = Path.Combine(_root, "vacio.bin");
        File.WriteAllBytes(empty, []);
        Assert.False(IsFullyAllocated(leading, size));
        Assert.False(IsFullyAllocated(trailing, size));
        Assert.False(IsFullyAllocated(all, size));
        Assert.True(IsFullyAllocated(none, size));
        Assert.True(IsFullyAllocated(empty, 0));
        // Pedir más de lo que mide el archivo no puede dar "todo asignado".
        Assert.False(IsFullyAllocated(none, size + 4096));
    }

    private string SparseFile(string name, int size, int holeOffset, int holeLength)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        if (!RunTool("fsutil.exe", $"sparse setflag \"{path}\"") || !RunTool("fsutil.exe", $"sparse setrange \"{path}\" {holeOffset} {holeLength}"))
        {
            Assert.Skip("fsutil sparse is not available to this process.");
        }

        return path;
    }

    private static bool IsFullyAllocated(string path, long length)
    {
        using var handle = SourceHandles.Open(path, SourceOpenMode.Content);
        return handle.IsFullyAllocated(length, CancellationToken.None);
    }

    // ================================================================== Ayudas

    private static string FinalPath(string path) => SourceHandles.FinalPath(path);

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
        await foreach (var entry in fs.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private static async Task<byte[]> ReadAsync(SourceFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, Ct);
        return memory.ToArray();
    }

    private static async Task<byte[]> HashAsync(SourceFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        return await SHA256.HashDataAsync(stream, Ct);
    }

    private SourceFileSystem Create(string root)
    {
        var fs = new SourceFileSystem(
            [new BackupSource("origen", root, FinalPath(root))],
            new Win32SourceFileApi(),
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLoggerFactory.Instance.CreateLogger<SourceFileSystem>());
        _created.Add(fs);
        return fs;
    }
}
