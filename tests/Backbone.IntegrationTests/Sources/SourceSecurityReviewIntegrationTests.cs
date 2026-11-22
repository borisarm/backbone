using System.Diagnostics;
using System.Text;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Sources;

/// <summary>
/// Revisión de seguridad del <c>SourceFileSystem</c> de HU-08 con el sistema de archivos real (sin elevar): la reapertura
/// relativa al propio handle de la lista blanca (BAJA-4, §8.2 "Apertura para leer el contenido" punto 2) y las carpetas que
/// distinguen mayúsculas (BAJA-3).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-08")]
public sealed class SourceSecurityReviewIntegrationTests : IDisposable
{
    private const uint WofTag = 0x80000017;

    private readonly TempDirectory _temp = new();
    private readonly List<SourceFileSystem> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var fs in _created)
        {
            fs.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>BAJA-4: la reapertura con nombre vacío relativa al handle abre el mismo archivo (mismo FileIdInfo y ruta final).</summary>
    [Fact]
    public void BAJA4_Reopening_relative_to_the_own_handle_opens_the_same_file()
    {
        var path = _temp.Combine("normal.bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("contenido normal"));

        using var first = SourceHandles.Open(path, SourceOpenMode.Content);
        using var second = first.ReOpenForContent(CancellationToken.None);

        Assert.Equal(first.GetFileId(CancellationToken.None), second.GetFileId(CancellationToken.None));
        Assert.Equal(first.GetFinalPath(FinalPathFormat.Nt, CancellationToken.None), second.GetFinalPath(FinalPathFormat.Nt, CancellationToken.None));
        Assert.Equal(first.GetFinalPath(FinalPathFormat.Dos, CancellationToken.None), second.GetFinalPath(FinalPathFormat.Dos, CancellationToken.None));
        Assert.False(second.GetAttributeTag(CancellationToken.None).IsDirectory);
    }

    /// <summary>
    /// BAJA-4 con un archivo comprimido por WOF (<c>compact /exe</c>, de la lista blanca): el primer handle se abre con
    /// <c>FILE_OPEN_REPARSE_POINT</c>, la reapertura sin él es el mismo archivo y el contenido leído es el original.
    /// </summary>
    [Fact]
    public async Task BAJA4_A_wof_compressed_file_is_reopened_as_the_same_file_and_read_with_its_real_content()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("wof")).FullName;
        var path = Path.Combine(folder, "comprimido.bin");
        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Backbone WOF ", 20_000)));
        await File.WriteAllBytesAsync(path, content, Ct);
        if (!RunTool("compact.exe", $"/c /exe:xpress4k \"{path}\""))
        {
            Assert.Skip("compact /exe no ha podido comprimir el archivo con WOF en este equipo.");
        }

        using (var first = SourceHandles.Open(path, SourceOpenMode.Content))
        {
            // El filtro WOF puede ocultar la etiqueta también en FileAttributeTagInfo (en Windows 11 26xx la oculta): el
            // archivo se ve como normal. En los dos casos la reapertura tiene que ser el mismo archivo.
            var tag = first.GetAttributeTag(CancellationToken.None);
            Assert.Contains(tag.ReparseTag, new[] { 0u, WofTag });
            using var second = first.ReOpenForContent(CancellationToken.None);
            Assert.Equal(first.GetFileId(CancellationToken.None), second.GetFileId(CancellationToken.None));
            Assert.Equal(first.GetFinalPath(FinalPathFormat.Nt, CancellationToken.None), second.GetFinalPath(FinalPathFormat.Nt, CancellationToken.None));
        }

        var fs = Create(folder);
        await using var stream = await fs.OpenReadAsync(Path.Combine(SourceHandles.FinalPath(folder), "comprimido.bin"), Ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, Ct);
        Assert.Equal(content, memory.ToArray());
    }

    /// <summary>
    /// BAJA-3: en una carpeta que distingue mayúsculas, <c>a.txt</c>/<c>A.txt</c> y <c>d\</c>/<c>D\</c> son elementos distintos y
    /// cada uno se lee con su contenido (sin <c>OBJ_CASE_INSENSITIVE</c>, sin confundirlos en la caché de carpetas y sin
    /// <c>ReparsePointSkipped</c> por la comparación de la ruta final).
    /// </summary>
    [Fact]
    public async Task BAJA3_In_a_case_sensitive_folder_items_that_differ_only_in_case_are_read_separately()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("mayusculas")).FullName;
        if (!RunTool("fsutil.exe", $"file setCaseSensitiveInfo \"{folder}\" enable"))
        {
            Assert.Skip("No se puede activar la distinción de mayúsculas en una carpeta en este equipo.");
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "minúscula", Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "A.txt"), "MAYÚSCULA", Ct);
        Directory.CreateDirectory(Path.Combine(folder, "d"));
        Directory.CreateDirectory(Path.Combine(folder, "D"));
        await File.WriteAllTextAsync(Path.Combine(folder, "d", "f.txt"), "en d", Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "D", "F.txt"), "en D", Ct);
        Assert.Equal(4, Directory.GetFileSystemEntries(folder).Length);

        var root = SourceHandles.FinalPath(folder);
        var fs = Create(folder);
        var names = (await ListAsync(fs, root)).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["A.txt", "D", "a.txt", "d"], names);

        // Primero las carpetas, para que estén en la caché al leer sus hijos.
        Assert.Equal(["f.txt"], (await ListAsync(fs, root + @"\d")).Select(e => e.Name));
        Assert.Equal(["F.txt"], (await ListAsync(fs, root + @"\D")).Select(e => e.Name));
        Assert.Equal("minúscula", await ReadTextAsync(fs, root + @"\a.txt"));
        Assert.Equal("MAYÚSCULA", await ReadTextAsync(fs, root + @"\A.txt"));
        Assert.Equal("en d", await ReadTextAsync(fs, root + @"\d\f.txt"));
        Assert.Equal("en D", await ReadTextAsync(fs, root + @"\D\F.txt"));

        // Un nombre que no existe con esas mayúsculas no se abre "por parecido".
        await Assert.ThrowsAnyAsync<IOException>(() => ReadTextAsync(fs, root + @"\d\F.txt"));
    }

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

    private static async Task<string> ReadTextAsync(SourceFileSystem fs, string path)
    {
        await using var stream = await fs.OpenReadAsync(path, Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    private SourceFileSystem Create(string root)
    {
        var fs = new SourceFileSystem(
            [new BackupSource("origen", root, SourceHandles.FinalPath(root))],
            new Win32SourceFileApi(),
            TimeProvider.System,
            SourceReadOptions.Default,
            NullLoggerFactory.Instance.CreateLogger<SourceFileSystem>());
        _created.Add(fs);
        return fs;
    }
}
