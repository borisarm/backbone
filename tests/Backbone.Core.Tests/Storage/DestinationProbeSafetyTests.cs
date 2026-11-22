using System.Diagnostics;
using System.Runtime.CompilerServices;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Storage;

/// <summary>
/// Revisión de seguridad de HU-06. MEDIA-3: un error que no es «no encontrado» (red, <c>ACCESS_DENIED</c>, sesión
/// reiniciada) no puede hacer que el destino parezca inexistente o vacío, y la inicialización vuelve a examinar el destino
/// justo antes de publicar <c>repo.json</c>. BAJA-1: también se comprueban los puntos de reanálisis entre la raíz del
/// recurso y la del destino.
/// </summary>
[Trait("Story", "HU-06")]
public sealed class DestinationProbeSafetyTests : IDisposable
{
    private static readonly RepositoryInitializationOptions Options = new() { CreatedBy = "PC-SALON" };

    private readonly TempDirectory directory = new();
    private readonly List<string> links = [];

    private string Root => Path.Join(directory.Path, "repo");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var link in links)
        {
            try
            {
                Directory.Delete(link);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo limpiará el sistema.
            }
        }

        directory.Dispose();
    }

    // ================================================================== MEDIA-3: errores que no son «no encontrado»

    public static TheoryData<string> FailureKinds => ["io", "denied"];

    private static Exception NewFailure(string kind) => kind == "io"
        ? new IOException("El nombre de red especificado ya no está disponible.")
        : new UnauthorizedAccessException("Acceso denegado.");

    private static Type FailureType(string kind) => kind == "io" ? typeof(IOException) : typeof(UnauthorizedAccessException);

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task HU06_A_failure_reading_the_root_propagates_instead_of_reporting_a_missing_destination(string kind)
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(Path.Join(Root, "ajeno.txt"), "no es de Backbone", Ct);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(
            Root,
            getAttributes: p => string.Equals(p, Root, StringComparison.OrdinalIgnoreCase) ? throw NewFailure(kind) : File.GetAttributes(p));

        await Assert.ThrowsAsync(FailureType(kind), () => storage.DirectoryExistsAsync(string.Empty, Ct));
        await Assert.ThrowsAsync(FailureType(kind), () => DrainAsync(storage, string.Empty));
        await Assert.ThrowsAsync(FailureType(kind), () => BackupRepository.ProbeAsync(storage, Ct));
        await Assert.ThrowsAsync(FailureType(kind), () => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        AssertNotInitialized();
    }

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task HU06_A_failure_reading_a_folder_or_file_propagates_from_every_query(string kind)
    {
        Directory.CreateDirectory(Path.Join(Root, "objects"));
        Directory.CreateDirectory(Path.Join(Root, "snapshots"));
        await File.WriteAllTextAsync(Path.Join(Root, "snapshots", "a.json"), "{}", Ct);
        var objects = Path.Join(Root, "objects");
        var snapshot = Path.Join(Root, "snapshots", "a.json");
        var storage = FileSystemRepositoryStorage.ForLocalTesting(
            Root,
            getAttributes: p => p.Equals(objects, StringComparison.OrdinalIgnoreCase) || p.Equals(snapshot, StringComparison.OrdinalIgnoreCase)
                ? throw NewFailure(kind)
                : File.GetAttributes(p));

        await Assert.ThrowsAsync(FailureType(kind), () => storage.DirectoryExistsAsync("objects", Ct));
        await Assert.ThrowsAsync(FailureType(kind), () => DrainAsync(storage, "objects"));
        await Assert.ThrowsAsync(FailureType(kind), () => storage.GetFileInfoAsync("snapshots/a.json", Ct));
    }

    [Fact]
    public async Task HU06_Only_not_found_means_missing_or_empty()
    {
        Directory.CreateDirectory(Path.Join(Root, "objects"));
        await File.WriteAllTextAsync(Path.Join(Root, "objects", "archivo"), "x", Ct);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(Root);
        var missing = FileSystemRepositoryStorage.ForLocalTesting(Path.Join(directory.Path, "no-existe", "repo"));

        Assert.False(await missing.DirectoryExistsAsync(string.Empty, Ct));
        Assert.Empty(await ListAsync(missing, string.Empty));
        Assert.Equal(RepositoryState.Missing, (await BackupRepository.ProbeAsync(missing, Ct)).State);
        Assert.False(await storage.DirectoryExistsAsync("snapshots", Ct));
        Assert.Empty(await ListAsync(storage, "snapshots"));
        Assert.Null(await storage.GetFileInfoAsync("snapshots/x.json", Ct));
        Assert.Null(await storage.GetFileInfoAsync("objects", Ct));

        // Un archivo en lugar de una carpeta: no es una carpeta y no tiene contenido.
        Assert.False(await storage.DirectoryExistsAsync("objects/archivo", Ct));
        Assert.Empty(await ListAsync(storage, "objects/archivo"));
        Assert.Equal(1, (await storage.GetFileInfoAsync("objects/archivo", Ct))?.Length);
    }

    /// <summary>
    /// Propiedad: haga fallar la N-ésima lectura de atributos que haga falta, sobre un destino con archivos ajenos dentro de
    /// una carpeta del formato, el examen nunca da <c>Missing</c> ni <c>Empty</c> y la inicialización nunca publica.
    /// </summary>
    [Fact]
    public async Task HU06_No_single_transient_failure_makes_a_non_empty_destination_look_empty()
    {
        Directory.CreateDirectory(Path.Join(Root, "objects", "ab"));
        Directory.CreateDirectory(Path.Join(Root, "tmp"));
        await File.WriteAllTextAsync(Path.Join(Root, "objects", "ab", "ajeno.bin"), "no es de Backbone", Ct);

        var exhausted = false;
        for (var failAt = 1; !exhausted; failAt++)
        {
            Assert.True(failAt < 1000, "La prueba no termina.");
            var calls = 0;
            var tripped = false;
            var storage = FileSystemRepositoryStorage.ForLocalTesting(Root, getAttributes: p =>
            {
                if (++calls == failAt)
                {
                    tripped = true;
                    throw new IOException("Sesión SMB reiniciada.");
                }

                return File.GetAttributes(p);
            });

            RepositoryProbeResult? probe = null;
            try
            {
                probe = await BackupRepository.ProbeAsync(storage, Ct);
            }
            catch (IOException)
            {
                Assert.True(tripped);
            }

            if (probe is not null)
            {
                Assert.Equal(RepositoryState.NotARepository, probe.State);
            }

            var probeTripped = tripped;
            calls = 0;
            tripped = false;
            var ex = await Record.ExceptionAsync(() => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));
            Assert.True(
                ex is IOException || ex is RepositoryException { Kind: RepositoryErrorKind.NotEmpty },
                $"Fallo en la lectura {failAt}: {ex?.GetType().Name ?? "sin excepción"}.");
            Assert.False(File.Exists(Path.Join(Root, "repo.json")), $"Publicó repo.json con el fallo en la lectura {failAt}.");

            exhausted = !probeTripped;
        }
    }

    // ================================================================== MEDIA-3: archivos ajenos durante la inicialización

    [Theory]
    [InlineData("ajeno.txt")]
    [InlineData("objects/ab/ajeno.bin")]
    [InlineData("snapshots/20261115T020000Z-a1b2.json")]
    [InlineData("otra-carpeta/dato.txt")]
    public async Task HU06_Files_that_appear_before_publishing_abort_the_initialization_without_publishing(string foreign)
    {
        Directory.CreateDirectory(Root);
        var inner = FileSystemRepositoryStorage.ForLocalTesting(Root);
        var storage = new InterferingStorage(inner, () =>
        {
            var path = Path.Join([Root, .. foreign.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
            File.WriteAllText(path, "ajeno");
        });

        Assert.Equal(RepositoryState.Empty, (await BackupRepository.ProbeAsync(inner, Ct)).State);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.NotEmpty, ex.Kind);
        Assert.True(storage.Interfered);
        AssertNotInitialized();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(Root, "tmp")));
        Assert.Equal(RepositoryState.NotARepository, (await BackupRepository.ProbeAsync(inner, Ct)).State);
    }

    [Fact]
    public async Task HU06_A_repo_json_published_by_another_process_meanwhile_reports_AlreadyInitialized_and_is_kept()
    {
        Directory.CreateDirectory(Root);
        var inner = FileSystemRepositoryStorage.ForLocalTesting(Root);
        var storage = new InterferingStorage(inner, () => File.WriteAllText(Path.Join(Root, "repo.json"), "del otro proceso"));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.AlreadyInitialized, ex.Kind);
        Assert.Equal("del otro proceso", await File.ReadAllTextAsync(Path.Join(Root, "repo.json"), Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(Root, "tmp")));
    }

    [Fact]
    public async Task HU06_Without_interference_the_initialization_still_publishes_and_tolerates_temporaries()
    {
        Directory.CreateDirectory(Path.Join(Root, "tmp"));
        await File.WriteAllTextAsync(Path.Join(Root, "tmp", "resto.tmp"), "de un intento anterior", Ct);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(Root);

        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);

        Assert.Equal(RepositoryState.Compatible, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public async Task HU06_The_in_memory_storage_also_aborts_when_foreign_files_appear()
    {
        var inner = new InMemoryRepositoryStorage();
        var storage = new InterferingStorage(inner, () => { }, async ct =>
        {
            await inner.CreateDirectoryAsync("objects/ab", ct);
            var stream = await inner.CreateNewAsync("objects/ab/ajeno", ct);
            await stream.DisposeAsync();
        });

        var ex = await Assert.ThrowsAsync<RepositoryException>(() =>
            BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));

        Assert.Equal(RepositoryErrorKind.NotEmpty, ex.Kind);
        Assert.Null(await inner.GetFileInfoAsync("repo.json", Ct));
    }

    // ================================================================== BAJA-1: segmentos entre el recurso y el destino

    [Theory]
    [InlineData(@"\\nas\recurso", new string[0])]
    [InlineData(@"\\nas\recurso\repo", new string[0])]
    [InlineData(@"\\NAS\Recurso\a\b\repo\", new[] { @"\\nas\recurso\a", @"\\nas\recurso\a\b" })]
    public void HU06_The_UNC_storage_checks_every_folder_between_the_share_and_the_destination(string destination, string[] expected)
    {
        var storage = new FileSystemRepositoryStorage(destination);

        Assert.Equal(expected, storage.AncestorsToCheck);
    }

    [Fact]
    public async Task HU06_A_junction_in_an_intermediate_segment_above_the_destination_is_detected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Los puntos de reanálisis de este ensayo son de Windows.");
        var share = Path.Join(directory.Path, "recurso");
        var outside = Path.Join(directory.Path, "fuera");
        Directory.CreateDirectory(share);
        Directory.CreateDirectory(Path.Join(outside, "b", "repo"));
        CreateJunction(Path.Join(share, "a"), outside);
        var root = Path.Join(share, "a", "b", "repo");

        // Sin límite (como antes de la corrección) el enlace no se ve: la raíz parece una carpeta normal.
        Assert.True(await FileSystemRepositoryStorage.ForLocalTesting(root).DirectoryExistsAsync(string.Empty, Ct));

        var storage = FileSystemRepositoryStorage.ForLocalTesting(root, boundary: share);
        await AssertCorruptAsync(() => storage.DirectoryExistsAsync(string.Empty, Ct));
        await AssertCorruptAsync(() => storage.CreateDirectoryAsync("objects", Ct));
        await AssertCorruptAsync(() => DrainAsync(storage, string.Empty));
        Assert.Equal(RepositoryState.Corrupt, (await BackupRepository.ProbeAsync(storage, Ct)).State);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct));
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(outside, "b", "repo")));
    }

    [Fact]
    public async Task HU06_A_missing_intermediate_segment_means_a_missing_destination_that_can_be_created()
    {
        var share = Path.Join(directory.Path, "recurso");
        Directory.CreateDirectory(share);
        var storage = FileSystemRepositoryStorage.ForLocalTesting(Path.Join(share, "a", "b", "repo"), boundary: share);

        Assert.Equal(RepositoryState.Missing, (await BackupRepository.ProbeAsync(storage, Ct)).State);
        await BackupRepository.InitializeAsync(storage, Options, TimeProvider.System, Ct);
        Assert.Equal(RepositoryState.Compatible, (await BackupRepository.ProbeAsync(storage, Ct)).State);
    }

    [Fact]
    public void HU06_A_boundary_that_does_not_contain_the_root_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            FileSystemRepositoryStorage.ForLocalTesting(Path.Join(directory.Path, "repo"), boundary: Path.Join(directory.Path, "otra")));
    }

    // ================================================================== Auxiliares

    private void AssertNotInitialized() => Assert.False(File.Exists(Path.Join(Root, "repo.json")));

    private static async Task AssertCorruptAsync(Func<Task> operation)
    {
        var ex = await Assert.ThrowsAsync<RepositoryException>(operation);
        Assert.Equal(RepositoryErrorKind.Corrupt, ex.Kind);
        Assert.Contains("reanálisis", ex.Message, StringComparison.Ordinal);
    }

    private static async Task DrainAsync(FileSystemRepositoryStorage storage, string path)
    {
        await foreach (var _ in storage.EnumerateAsync(path, Ct))
        {
        }
    }

    private static async Task<List<StorageEntry>> ListAsync(FileSystemRepositoryStorage storage, string path)
    {
        var list = new List<StorageEntry>();
        await foreach (var entry in storage.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo ejecutar cmd.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J falló: {output}");
        links.Insert(0, link);
    }

    /// <summary>
    /// Decorador que, al crear el temporal de <c>repo.json</c> (es decir, después del examen inicial y antes de publicar),
    /// hace aparecer archivos en el destino como lo haría otro equipo o proceso.
    /// </summary>
    private sealed class InterferingStorage(
        IRepositoryStorage inner,
        Action interfere,
        Func<CancellationToken, Task>? interfereAsync = null) : IRepositoryStorage
    {
        public bool Interfered { get; private set; }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) => inner.OpenReadAsync(path, cancellationToken);

        public async Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            var stream = await inner.CreateNewAsync(path, cancellationToken);
            if (!Interfered && path.StartsWith("tmp/", StringComparison.Ordinal))
            {
                Interfered = true;
                interfere();
                if (interfereAsync is not null)
                {
                    await interfereAsync(cancellationToken);
                }
            }

            return stream;
        }

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

        public async IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken))
            {
                yield return entry;
            }
        }
    }
}
