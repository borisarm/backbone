using System.Security.Cryptography;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Sources;

/// <summary>
/// HU-08, auditoría de QA de <see cref="SourceFileSystem"/> con el sistema de archivos simulado: orden de la clasificación
/// (§8.2 "Clasificación por etiqueta", ADR-19) sobre el handle, cambio de clase entre la enumeración y la apertura,
/// archivos y carpetas solo en la nube (nunca se lee un marcador no descargado), lista blanca con segundo handle,
/// raíces de origen y fallos inyectados en cada operación, comprobando siempre que no queda ningún handle abierto.
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceFileSystemQaTests : IDisposable
{
    private const uint CloudTag = 0x9000601A;
    private const uint CloudVariantTag = 0x9000F01A;
    private const uint UnknownTag = 0x90001234;
    private const uint UnknownSurrogateTag = 0x20000001;
    private const FileAttributes Reparse = FileAttributes.ReparsePoint;
    private const FileAttributes RecallOpen = SourceAttributes.RecallOnOpen;
    private const FileAttributes RecallData = SourceAttributes.RecallOnDataAccess;
    private const FileAttributes Offline = FileAttributes.Offline;

    private const uint StatePartial = 0x1 | 0x10;
    private const uint StatePartiallyOnDisk = 0x1 | 0x20;
    private const uint StateInvalid = 0xFFFFFFFF;

    private readonly FakeSourceFileApi _api = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly SourceFileSystem _fs;

    public SourceFileSystemQaTests()
    {
        _fs = new SourceFileSystem(
            [new BackupSource("src", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath)],
            _api,
            _time,
            SourceReadOptions.Default with { ReadBlockBytes = 4096 },
            new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
    }

    public void Dispose()
    {
        _fs.Dispose();
        _api.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================== Orden de la clasificación sobre el handle

    /// <summary>Resultado esperado: un código de aviso, o "Read" si se lee por su contenido.</summary>
    public static TheoryData<int, uint, string> ClassificationOrder => new()
    {
        // Un sustituto de nombre es siempre un enlace, tenga los atributos que tenga.
        { (int)(Reparse | RecallData), ReparseTags.Symlink, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Reparse | RecallOpen), ReparseTags.MountPoint, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Reparse | Offline | RecallData), UnknownSurrogateTag, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Reparse | Offline), ReparseTags.Symlink, nameof(RunIssueCode.ReparsePointSkipped) },

        // Después, solo en la nube: antes que "no legible" y antes que la lista blanca.
        { (int)(Reparse | RecallData), ReparseTags.ProjFs, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | RecallOpen), UnknownTag, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | RecallData), ReparseTags.Wof, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | RecallData), ReparseTags.Dedup, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | RecallData), CloudTag, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | Offline), CloudTag, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | Offline), CloudVariantTag, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | Offline), ReparseTags.Hsm, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | Offline), ReparseTags.Hsm2, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)RecallData, 0u, nameof(RunIssueCode.CloudOnlySkipped) }, // los RECALL_* valen aun sin punto de reanálisis

        // Después, no legible: cualquier etiqueta fuera de la lista blanca, y la etiqueta 0 con punto de reanálisis.
        { (int)Reparse, ReparseTags.ProjFs, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, ReparseTags.AppExecLink, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, UnknownTag, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, 0u, nameof(RunIssueCode.ReadError) },
        { (int)(Reparse | Offline), ReparseTags.ProjFs, nameof(RunIssueCode.ReadError) }, // OFFLINE sin etiqueta de nube ni HSM no cuenta
        { (int)(Reparse | Offline), 0u, nameof(RunIssueCode.ReadError) },

        // Por último, contenido o normal.
        { (int)Reparse, ReparseTags.Wof, "Read" },
        { (int)Reparse, ReparseTags.Dedup, "Read" },
        { (int)Reparse, CloudTag, "Read" },
        { (int)Reparse, CloudVariantTag, "Read" },
        { (int)(Reparse | Offline), ReparseTags.Wof, "Read" }, // con otra etiqueta, manda la etiqueta
        { (int)(Reparse | Offline), ReparseTags.Dedup, "Read" },
        { (int)Offline, 0u, "Read" }, // OFFLINE solo: lo puede poner cualquier usuario, no significa nada
        { (int)FileAttributes.Archive, ReparseTags.Symlink, "Read" }, // sin FILE_ATTRIBUTE_REPARSE_POINT la etiqueta se ignora
        { (int)FileAttributes.Archive, 0u, "Read" },
    };

    [Theory]
    [MemberData(nameof(ClassificationOrder))]
    public async Task HU08_3_4_On_the_handle_a_file_is_classified_in_the_order_of_the_specification(int attributes, uint tag, string expected)
    {
        var content = RandomNumberGenerator.GetBytes(5000);
        _api.AddFile("x.bin", content, (FileAttributes)attributes | FileAttributes.Archive, tag);

        if (expected == "Read")
        {
            Assert.Equal(content, await ReadAllAsync(FakeSourceFileApi.Child("x.bin")));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("x.bin")));
            Assert.Equal(Enum.Parse<RunIssueCode>(expected), ex.Code);
            if (ex.Code == RunIssueCode.ReadError)
            {
                Assert.Contains(ReparseTags.Format(tag), ex.Message, StringComparison.Ordinal);
            }

            Assert.Empty(_api.ReOpens); // nunca se abre el contenido de lo que no se lee
            Assert.Empty(_api.AllocatedRangeQueries);
            Assert.Equal(0, _api.ReadCalls);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    private static readonly FileAttributes[] GridAttributes =
    [
        0, Reparse, Reparse | Offline, Offline, RecallOpen, RecallData, Reparse | RecallOpen, Reparse | RecallData, Reparse | Offline | RecallData,
    ];

    private static readonly uint[] GridTags =
    [
        0, ReparseTags.MountPoint, ReparseTags.Symlink, UnknownSurrogateTag, ReparseTags.Hsm, ReparseTags.Hsm2, ReparseTags.Dedup,
        ReparseTags.Wof, CloudTag, CloudVariantTag, ReparseTags.ProjFs, ReparseTags.AppExecLink, UnknownTag,
    ];

    /// <summary>
    /// El servicio repite sobre el handle la misma clasificación que hace el motor con lo enumerado: ninguna combinación de
    /// atributos y etiqueta da una clase distinta en uno y otro, para archivos (<c>GetFileInfoAsync</c>) y carpetas
    /// (<c>EnumerateAsync</c>).
    /// </summary>
    [Fact]
    public async Task HU08_3_4_The_classification_on_the_handle_agrees_with_the_engine_for_every_combination_of_files_and_folders()
    {
        var folder = _api.AddDirectory("d");
        var file = _api.AddFile("f.bin", [1, 2, 3]);
        var disagreements = new List<string>();
        foreach (var attributes in GridAttributes)
        {
            foreach (var tag in GridTags)
            {
                foreach (var isDirectory in new[] { false, true })
                {
                    var node = isDirectory ? folder : file;
                    node.Attributes = attributes | (isDirectory ? FileAttributes.Directory : FileAttributes.Archive);
                    node.ReparseTag = tag;
                    var expected = SourceAttributes.Classify(node.Attributes, tag) switch
                    {
                        SourceEntryKind.Link => RunIssueCode.ReparsePointSkipped,
                        SourceEntryKind.CloudOnly => RunIssueCode.CloudOnlySkipped,
                        SourceEntryKind.Unreadable => RunIssueCode.ReadError,
                        _ => (RunIssueCode?)null,
                    };

                    var path = FakeSourceFileApi.Child(isDirectory ? "d" : "f.bin");
                    RunIssueCode? actual = null;
                    try
                    {
                        if (isDirectory)
                        {
                            await ListAsync(path);
                        }
                        else
                        {
                            await _fs.GetFileInfoAsync(path, Ct);
                        }
                    }
                    catch (SourceAccessException ex)
                    {
                        actual = ex.Code;
                    }

                    if (actual != expected)
                    {
                        disagreements.Add($"{(isDirectory ? "carpeta" : "archivo")} atributos=0x{(uint)node.Attributes:X} etiqueta=0x{tag:X8}: motor {expected?.ToString() ?? "lee"}, servicio {actual?.ToString() ?? "lee"}");
                    }
                }
            }
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    // ================================================================== Cambio de clase entre la enumeración y la apertura

    public static TheoryData<int, uint, uint, string> BecomesDuringTheRun => new()
    {
        // atributos, etiqueta, estado del marcador, resultado
        { (int)Reparse, ReparseTags.MountPoint, 0x9u, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)Reparse, ReparseTags.Symlink, 0x9u, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Reparse | RecallData), CloudTag, 0x9u, nameof(RunIssueCode.CloudOnlySkipped) }, // liberado a la nube
        { (int)(Reparse | Offline), CloudTag, 0x9u, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, CloudTag, StatePartial, nameof(RunIssueCode.CloudOnlySkipped) }, // atributos descargados, marcador parcial
        { (int)Reparse, CloudTag, StatePartiallyOnDisk, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, CloudTag, StateInvalid, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, ReparseTags.ProjFs, 0x9u, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, 0u, 0x9u, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, ReparseTags.Wof, 0x9u, "Read" }, // pasa a la lista blanca: se lee por su contenido
        { (int)Reparse, CloudTag, 0x9u, "Read" }, // descargado
    };

    [Theory]
    [MemberData(nameof(BecomesDuringTheRun))]
    public async Task HU08_3_4_A_file_that_changes_class_after_enumeration_is_classified_again_when_it_is_opened_and_in_GetFileInfo(int attributes, uint tag, uint state, string expected)
    {
        var content = RandomNumberGenerator.GetBytes(3000);
        var node = _api.AddFile("x.bin", content);
        var listed = Assert.Single(await ListAsync(FakeSourceFileApi.ReadPath));
        Assert.Equal(SourceEntryKind.Normal, SourceAttributes.Classify(listed));

        node.Attributes = (FileAttributes)attributes | FileAttributes.Archive;
        node.ReparseTag = tag;
        node.PlaceholderState = state;

        if (expected == "Read")
        {
            Assert.Equal(content, await ReadAllAsync(listed.FullPath));
            Assert.NotNull(await _fs.GetFileInfoAsync(listed.FullPath, Ct));
        }
        else
        {
            var code = Enum.Parse<RunIssueCode>(expected);
            Assert.Equal(code, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(listed.FullPath))).Code);
            Assert.Equal(code, (await Assert.ThrowsAsync<SourceAccessException>(() => _fs.GetFileInfoAsync(listed.FullPath, Ct))).Code);
            Assert.Empty(_api.ReOpens);
            Assert.Equal(0, _api.ReadCalls);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData((int)Reparse, ReparseTags.MountPoint, nameof(RunIssueCode.ReparsePointSkipped))]
    [InlineData((int)Reparse, ReparseTags.Symlink, nameof(RunIssueCode.ReparsePointSkipped))]
    [InlineData((int)(Reparse | RecallOpen), CloudTag, nameof(RunIssueCode.CloudOnlySkipped))]
    [InlineData((int)(Reparse | RecallData), CloudTag, nameof(RunIssueCode.CloudOnlySkipped))]
    [InlineData((int)Reparse, ReparseTags.ProjFs, nameof(RunIssueCode.ReadError))]
    public async Task HU08_3_4_A_folder_that_changes_class_after_enumeration_is_not_traversed(int attributes, uint tag, string expected)
    {
        var folder = _api.AddDirectory("d");
        _api.AddFile(@"d\dentro.txt", [1]);
        var listed = Assert.Single(await ListAsync(FakeSourceFileApi.ReadPath));
        Assert.Equal(SourceEntryKind.Normal, SourceAttributes.Classify(listed));
        var callsBefore = _api.ReadDirectoryCalls;

        folder.Attributes = (FileAttributes)attributes | FileAttributes.Directory;
        folder.ReparseTag = tag;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(listed.FullPath));
        Assert.Equal(Enum.Parse<RunIssueCode>(expected), ex.Code);
        Assert.Equal(callsBefore, _api.ReadDirectoryCalls); // ni una llamada de enumeración sobre la carpeta cambiada
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_3_A_file_replaced_by_a_folder_and_a_folder_by_a_file_are_read_errors_without_leaking_handles()
    {
        var file = _api.AddFile("a", [1]);
        var folder = _api.AddDirectory("b");
        file.Attributes = FileAttributes.Directory;
        folder.Attributes = FileAttributes.Archive;

        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("a")))).Code);
        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.Child("b")))).Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.Equal(0, _api.ReadDirectoryCalls);
    }

    // ================================================================== Carpetas de nube

    [Theory]
    [InlineData((int)(Reparse | RecallOpen), CloudTag, 0x9u)]
    [InlineData((int)(Reparse | RecallData), CloudTag, 0x9u)]
    [InlineData((int)(Reparse | Offline), CloudTag, 0x9u)]
    [InlineData((int)Reparse, CloudTag, StatePartial)]
    [InlineData((int)Reparse, CloudTag, StatePartiallyOnDisk)]
    [InlineData((int)Reparse, CloudTag, StateInvalid)]
    [InlineData((int)Reparse, CloudVariantTag, StatePartial)]
    public async Task HU08_4_An_unpopulated_cloud_folder_is_never_enumerated_live_or_in_a_snapshot(int attributes, uint tag, uint state)
    {
        var folder = _api.AddDirectory("OneDrive", (FileAttributes)attributes, tag);
        folder.PlaceholderState = state;
        _api.AddFile(@"OneDrive\hijo.txt", [1], Reparse | RecallData, CloudTag);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.Child("OneDrive")));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Equal(0, _api.ReadDirectoryCalls);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_4_A_populated_cloud_folder_is_enumerated_and_each_child_keeps_its_own_class()
    {
        var folder = _api.AddDirectory("OneDrive", FileAttributes.Directory | Reparse, CloudTag);
        folder.PlaceholderState = 0x1 | 0x8;
        _api.AddFile(@"OneDrive\descargado.bin", [1], Reparse, CloudTag);
        _api.AddFile(@"OneDrive\nube.bin", [], Reparse | RecallData, CloudTag);
        _api.AddDirectory(@"OneDrive\vacia", FileAttributes.Directory | Reparse | RecallOpen, CloudTag);

        var entries = await ListAsync(FakeSourceFileApi.Child("OneDrive"));

        Assert.Equal(
            new[] { ("descargado.bin", SourceEntryKind.Content), ("nube.bin", SourceEntryKind.CloudOnly), ("vacia", SourceEntryKind.CloudOnly) },
            entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => (e.Name, SourceAttributes.Classify(e))));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.Equal(0, _api.ReadCalls);
    }

    // ================================================================== Nunca se guarda un archivo de ceros

    public static TheoryData<string> NotFullyOnDisk => new()
    {
        "live-recall-on-data-access",
        "live-offline-with-cloud-tag",
        "placeholder-partial",
        "placeholder-invalid",
        "vss-no-filter-with-holes",
        "second-handle-with-holes",
        "second-handle-turned-cloud-only",
        "second-handle-offline",
    };

    [Theory]
    [MemberData(nameof(NotFullyOnDisk))]
    public async Task HU08_4_A_cloud_file_that_is_not_fully_on_disk_is_never_read_so_no_zeros_reach_the_repository(string scenario)
    {
        var zeros = new byte[20_000]; // lo que saldría de leer los huecos sin el filtro
        var node = _api.AddFile("nube.bin", zeros, Reparse, CloudTag);
        switch (scenario)
        {
            case "live-recall-on-data-access":
                node.Attributes = Reparse | RecallData;
                break;
            case "live-offline-with-cloud-tag":
                node.Attributes = Reparse | Offline;
                break;
            case "placeholder-partial":
                node.PlaceholderState = StatePartial;
                break;
            case "placeholder-invalid":
                node.PlaceholderState = StateInvalid;
                break;
            case "vss-no-filter-with-holes":
                node.ReOpenError = 1920;
                node.FullyAllocated = false;
                break;
            case "second-handle-with-holes":
                node.SeenThroughSecondHandle = Mirror(node, n => n.FullyAllocated = false);
                break;
            case "second-handle-turned-cloud-only":
                node.SeenThroughSecondHandle = Mirror(node, n => n.Attributes = Reparse | RecallData);
                break;
            case "second-handle-offline":
                node.SeenThroughSecondHandle = Mirror(node, n => n.Attributes = Reparse | Offline);
                break;
        }

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("nube.bin")));

        Assert.Equal(RunIssueCode.CloudOnlySkipped, ex.Code);
        Assert.Equal(0, _api.ReadCalls); // ni un solo bloque pedido al sistema
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_4_The_allocated_ranges_are_checked_for_the_whole_size_of_the_file_with_and_without_the_filter()
    {
        var content = RandomNumberGenerator.GetBytes(12_345);
        _api.AddFile("con-filtro.bin", content, Reparse, CloudTag);
        var without = _api.AddFile("sin-filtro.bin", content, Reparse, CloudTag);
        without.ReOpenError = 1920;

        Assert.Equal(content, await ReadAllAsync(FakeSourceFileApi.Child("con-filtro.bin")));
        Assert.Equal(content, await ReadAllAsync(FakeSourceFileApi.Child("sin-filtro.bin")));

        Assert.Equal<IEnumerable<long>>([12_345L, 12_345L], _api.AllocatedRangeLengths);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(CloudTag, 0)]
    [InlineData(ReparseTags.Wof, 0)]
    [InlineData(0u, 0)]
    public async Task HU08_An_empty_file_is_read_as_empty_in_every_readable_class(uint tag, int length)
    {
        _api.AddFile("vacio.bin", new byte[length], tag == 0 ? FileAttributes.Archive : Reparse, tag);

        Assert.Empty(await ReadAllAsync(FakeSourceFileApi.Child("vacio.bin")));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    // ================================================================== Lista blanca: segundo handle

    public static TheoryData<int, ulong, ulong> DifferentIdentifiers => new()
    {
        { 1, 0, 0 }, // otro número de serie del volumen
        { 0, 1, 0 }, // otra parte baja
        { 0, 0, 1 }, // otra parte alta
    };

    [Theory]
    [MemberData(nameof(DifferentIdentifiers))]
    public async Task HU08_3_A_second_handle_whose_identifier_differs_in_any_component_is_rejected(int volume, ulong low, ulong high)
    {
        var node = _api.AddFile("lista.bin", [1], Reparse, ReparseTags.Wof);
        var other = Mirror(node, n => n.Id = new SourceFileId(node.Id.VolumeSerialNumber + (ulong)volume, node.Id.Low + low, node.Id.High + high));
        node.SeenThroughSecondHandle = other;

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(0, _api.ReadCalls);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(CloudTag, ReparseTags.Wof, false)]
    [InlineData(ReparseTags.Wof, CloudTag, false)]
    [InlineData(ReparseTags.Dedup, ReparseTags.Wof, false)]
    [InlineData(ReparseTags.Wof, ReparseTags.ProjFs, false)]
    [InlineData(ReparseTags.Wof, ReparseTags.MountPoint, false)]
    [InlineData(CloudTag, CloudVariantTag, true)] // la misma clase (IO_REPARSE_TAG_CLOUD*)
    [InlineData(ReparseTags.Wof, ReparseTags.Wof, true)]
    public async Task HU08_3_The_second_handle_must_have_the_same_tag_class(uint firstTag, uint secondTag, bool allowed)
    {
        var content = RandomNumberGenerator.GetBytes(2000);
        var node = _api.AddFile("lista.bin", content, Reparse, firstTag);
        node.SeenThroughSecondHandle = Mirror(node, n => n.ReparseTag = secondTag);

        if (allowed)
        {
            Assert.Equal(content, await ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));
            // Una etiqueta que ha pasado a no legible (ProjFS) se clasifica como tal sobre el segundo handle: ReadError, que
            // conserva la versión del padre. §8.2 punto 2 dice ReparsePointSkipped "si algo no coincide"; ver el informe de QA.
            Assert.Equal(secondTag == ReparseTags.ProjFs ? RunIssueCode.ReadError : RunIssueCode.ReparsePointSkipped, ex.Code);
            Assert.Equal(0, _api.ReadCalls);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_3_A_second_handle_that_is_a_folder_or_that_lost_its_reparse_point_is_rejected()
    {
        var a = _api.AddFile("a.bin", [1], Reparse, ReparseTags.Wof);
        a.SeenThroughSecondHandle = Mirror(a, n => n.Attributes = FileAttributes.Directory | Reparse);
        var b = _api.AddFile("b.bin", [1], Reparse, ReparseTags.Wof);
        b.SeenThroughSecondHandle = Mirror(b, n => { n.Attributes = FileAttributes.Archive; n.ReparseTag = 0; });

        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("a.bin")))).Code);
        Assert.Equal(RunIssueCode.ReparsePointSkipped, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("b.bin")))).Code);
        Assert.Equal(0, _api.ReadCalls);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_3_A_second_handle_whose_final_path_falls_in_a_mandatory_exclusion_is_rejected()
    {
        using var fs = new SourceFileSystem(
            [new BackupSource("c", FakeSourceFileApi.ReadPath, FakeSourceFileApi.ReadPath) { MandatoryExclusions = [@"datos\Backbone"] }],
            _api,
            _time,
            SourceReadOptions.Default,
            new LoggerFactory([_logs]).CreateLogger<SourceFileSystem>());
        var node = _api.AddFile("lista.bin", [1], Reparse, CloudTag);
        node.SeenThroughSecondHandle = Mirror(node, n => n.FinalNt = FakeSourceFileApi.RootFinalNt + @"\datos\BACKBONE\credentials\x.bin");

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => fs.OpenReadAsync(FakeSourceFileApi.Child("lista.bin"), Ct));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(0, _api.ReadCalls);
        Assert.Equal(fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(CloudTag, 5, typeof(UnauthorizedAccessException), null)]
    [InlineData(CloudTag, 32, typeof(SourceAccessException), RunIssueCode.FileLocked)]
    [InlineData(CloudTag, 21, typeof(SourceAccessException), RunIssueCode.ReadError)]
    [InlineData(ReparseTags.Wof, 5, typeof(UnauthorizedAccessException), null)]
    [InlineData(ReparseTags.Dedup, 32, typeof(SourceAccessException), RunIssueCode.FileLocked)]
    [InlineData(ReparseTags.Dedup, 1117, typeof(SourceAccessException), RunIssueCode.ReadError)]
    public async Task HU08_The_second_open_errors_other_than_the_missing_filter_are_translated_and_do_not_fall_back_to_the_first_handle(uint tag, int error, Type expected, RunIssueCode? code)
    {
        var node = _api.AddFile("x.bin", [1], Reparse, tag);
        node.ReOpenError = error;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(FakeSourceFileApi.Child("x.bin")));

        Assert.IsType(expected, ex);
        if (code is { } c)
        {
            Assert.Equal(c, ((SourceAccessException)ex).Code);
        }

        Assert.Equal(0, _api.ReadCalls);
        Assert.Empty(_api.AllocatedRangeQueries); // con un error distinto de ERROR_CANT_ACCESS_FILE no se lee el flujo crudo
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_Without_the_filter_the_cloud_file_is_read_from_the_first_handle_only_after_the_ranges_are_checked_on_that_handle()
    {
        var content = RandomNumberGenerator.GetBytes(9000);
        var node = _api.AddFile("nube.bin", content, Reparse, CloudVariantTag);
        node.ReOpenError = 1920;

        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("nube.bin"), Ct);

        Assert.Equal(1 + _fs.HeldHandles, _api.OpenHandles); // solo el primero
        Assert.Equal(new[] { node.Win32Path }, _api.AllocatedRangeQueries.ToArray());
        Assert.Equal(0, _api.ReadCalls); // la comprobación va antes de leer
        Assert.Equal(content, await ReadToEndAsync(stream));
    }

    // ================================================================== Inyección de fallos en cada operación

    public static TheoryData<uint, FakeOp, bool, int> FaultPoints()
    {
        var data = new TheoryData<uint, FakeOp, bool, int>();
        foreach (var tag in new[] { CloudTag, ReparseTags.Wof })
        {
            foreach (var secondHandle in new[] { false, true })
            {
                foreach (var op in new[] { FakeOp.Attributes, FakeOp.FinalPath, FakeOp.FileId, FakeOp.Metadata, FakeOp.AllocatedRanges })
                {
                    // Los rangos asignados (y el tamaño que necesitan) solo se consultan, con etiqueta de nube, sobre el segundo handle.
                    if (op is FakeOp.Metadata or FakeOp.AllocatedRanges && !(tag == CloudTag && secondHandle))
                    {
                        continue;
                    }

                    foreach (var error in new[] { 5, 32, 1117 })
                    {
                        data.Add(tag, op, secondHandle, error);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FaultPoints))]
    public async Task HU08_A_failure_in_any_operation_on_either_handle_is_translated_leaves_no_handle_open_and_the_next_file_is_read(uint tag, FakeOp op, bool onSecondHandle, int error)
    {
        var node = _api.AddFile("lista.bin", [1, 2, 3], Reparse, tag);
        var second = Mirror(node, _ => { });
        node.SeenThroughSecondHandle = second;
        (onSecondHandle ? second : node).Faults[op] = error;
        _api.AddFile("bien.bin", [7, 8]);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ReadAllAsync(FakeSourceFileApi.Child("lista.bin")));

        switch (error)
        {
            case 5:
                Assert.IsType<UnauthorizedAccessException>(ex);
                break;
            case 32:
                Assert.Equal(RunIssueCode.FileLocked, Assert.IsType<SourceAccessException>(ex).Code);
                break;
            default:
                Assert.Equal(RunIssueCode.ReadError, Assert.IsType<SourceAccessException>(ex).Code);
                break;
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(0, _api.ReadCalls);
        Assert.Equal(new byte[] { 7, 8 }, await ReadAllAsync(FakeSourceFileApi.Child("bien.bin")));
    }

    [Theory]
    [InlineData(FakeOp.Attributes, 5)]
    [InlineData(FakeOp.FinalPath, 32)]
    [InlineData(FakeOp.Open, 1117)]
    [InlineData(FakeOp.Metadata, 5)]
    public async Task HU08_A_failure_getting_the_metadata_or_the_attributes_closes_the_handle(FakeOp op, int error)
    {
        var node = _api.AddFile("a.bin", [1]);
        node.Faults[op] = error;

        var ex = await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            try
            {
                await _fs.GetFileInfoAsync(FakeSourceFileApi.Child("a.bin"), Ct);
            }
            catch (UnauthorizedAccessException denied)
            {
                throw new IOException("denegado", denied);
            }
        });

        Assert.True(ex is SourceAccessException || ex.InnerException is UnauthorizedAccessException);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_The_enumeration_delivers_every_entry_once_across_batches_without_dot_entries()
    {
        var folder = _api.AddDirectory("d");
        folder.EnumerationBatchSize = 2;
        for (var i = 0; i < 7; i++)
        {
            _api.AddFile($@"d\f{i}.txt", [(byte)i]);
        }

        var entries = await ListAsync(FakeSourceFileApi.Child("d"));

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"f{i}.txt"), entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.True(_api.ReadDirectoryCalls >= 4);
    }

    [Fact]
    public async Task HU08_A_failure_in_a_later_batch_surfaces_after_the_earlier_entries_and_closes_the_handle()
    {
        var folder = _api.AddDirectory("d");
        folder.EnumerationBatchSize = 2;
        folder.Faults[FakeOp.ReadDirectory] = 1117;
        folder.ReadDirectoryFaultFromCall = 3;
        for (var i = 0; i < 7; i++)
        {
            _api.AddFile($@"d\f{i}.txt", [(byte)i]);
        }

        var delivered = new List<string>();
        var ex = await Assert.ThrowsAsync<SourceAccessException>(async () =>
        {
            await foreach (var entry in _fs.EnumerateAsync(FakeSourceFileApi.Child("d"), Ct))
            {
                delivered.Add(entry.Name);
            }
        });

        Assert.Equal(RunIssueCode.ReadError, ex.Code);
        Assert.Equal(2, delivered.Count); // el primer lote solo trae "." y ".."; el segundo, dos entradas; el tercero falla
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_Leaving_an_enumeration_early_or_cancelling_it_closes_the_folder_handle()
    {
        var folder = _api.AddDirectory("d");
        folder.EnumerationBatchSize = 2;
        for (var i = 0; i < 6; i++)
        {
            _api.AddFile($@"d\f{i}.txt", [(byte)i]);
        }

        await foreach (var _ in _fs.EnumerateAsync(FakeSourceFileApi.Child("d"), Ct))
        {
            break;
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);

        using var cancellation = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _fs.EnumerateAsync(FakeSourceFileApi.Child("d"), cancellation.Token))
            {
                if (++seen == 1)
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
        Assert.False(_fs.IsAbandoned);
    }

    [Fact]
    public async Task HU08_Names_are_delivered_exactly_as_the_system_gives_them_nfd_surrogates_and_long_names_included()
    {
        var nfd = "Café.txt";
        var emoji = "\U0001F600 \U0001F4C1.txt";
        var longName = new string('x', 250) + ".txt";
        _api.AddFile(nfd, [1]);
        _api.AddFile(emoji, [1]);
        _api.AddFile(longName, [1]);

        var names = (await ListAsync(FakeSourceFileApi.ReadPath)).Select(e => e.Name).ToList();

        Assert.Contains(nfd, names);
        Assert.Contains(emoji, names);
        Assert.Contains(longName, names);
        Assert.DoesNotContain("Café.txt", names);
        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(FakeSourceFileApi.Child(nfd)));
    }

    // ================================================================== Errores de lectura en medio del archivo

    [Theory]
    [InlineData(32, RunIssueCode.FileLocked)]
    [InlineData(33, RunIssueCode.FileLocked)]
    [InlineData(23, RunIssueCode.ReadError)]
    [InlineData(1117, RunIssueCode.ReadError)]
    public async Task HU08_A_read_error_in_the_middle_of_a_file_is_translated_and_disposing_the_stream_closes_both_handles(int error, RunIssueCode expected)
    {
        var node = _api.AddFile("a.bin", new byte[20_000], Reparse, ReparseTags.Wof);
        node.ReadFault = offset => offset >= 8192 ? new SourceFileApiException(error, "ReadFile") : null;

        var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), Ct);
        Assert.Equal(2 + _fs.HeldHandles, _api.OpenHandles);
        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadToEndAsync(stream));
        await stream.DisposeAsync();

        Assert.Equal(expected, ex.Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_A_lock_violation_reported_as_an_IOException_is_FileLocked()
    {
        var node = _api.AddFile("a.bin", new byte[20_000]);
        node.ReadFault = offset => offset >= 4096 ? new IOException("lock", unchecked((int)0x8007_0021)) : null;

        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), Ct);

        Assert.Equal(RunIssueCode.FileLocked, (await Assert.ThrowsAsync<SourceAccessException>(() => ReadToEndAsync(stream))).Code);
    }

    [Fact]
    public async Task HU08_Cancelling_a_blocked_read_throws_OperationCanceledException_and_the_run_can_still_read_other_files()
    {
        var node = _api.AddFile("a.bin", new byte[10_000]);
        node.BlockAt = FakeBlockAt.Read;
        node.BlockMode = FakeBlockMode.HonorCancellation;
        _api.AddFile("b.bin", [1]);
        await using var stream = await _fs.OpenReadAsync(FakeSourceFileApi.Child("a.bin"), Ct);
        using var cancellation = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[4096], cancellation.Token).AsTask();
        Assert.True(_api.Blocked.Wait(TimeSpan.FromSeconds(10), Ct));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False(_fs.IsAbandoned);
        Assert.Equal(new byte[] { 1 }, await ReadAllAsync(FakeSourceFileApi.Child("b.bin")));
    }

    // ================================================================== Raíz de un origen en cada clase

    public static TheoryData<int, uint, uint, string> RootClasses => new()
    {
        // atributos, etiqueta, estado del marcador, resultado
        { (int)Reparse, ReparseTags.MountPoint, 0x9u, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)Reparse, ReparseTags.Symlink, 0x9u, nameof(RunIssueCode.ReparsePointSkipped) },
        { (int)(Reparse | RecallData), ReparseTags.MountPoint, 0x9u, nameof(RunIssueCode.ReparsePointSkipped) }, // enlace antes que nube
        { (int)(Reparse | RecallOpen), CloudTag, 0x9u, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)(Reparse | Offline), CloudTag, 0x9u, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, CloudTag, StatePartial, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, CloudTag, StateInvalid, nameof(RunIssueCode.CloudOnlySkipped) },
        { (int)Reparse, ReparseTags.ProjFs, 0x9u, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, ReparseTags.AppExecLink, 0x9u, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, 0u, 0x9u, nameof(RunIssueCode.ReadError) },
        { (int)Reparse, CloudTag, 0x1u | 0x8u, "Read" }, // raíz de nube poblada (la carpeta de OneDrive): se recorre
        { (int)Reparse, ReparseTags.Wof, 0x9u, "Read" },
        { (int)FileAttributes.Directory, 0u, 0x9u, "Read" },
    };

    [Theory]
    [MemberData(nameof(RootClasses))]
    public async Task HU08_The_root_of_a_source_follows_the_same_classification(int attributes, uint tag, uint state, string expected)
    {
        _api.Root.Attributes = (FileAttributes)attributes | FileAttributes.Directory;
        _api.Root.ReparseTag = tag;
        _api.Root.PlaceholderState = state;
        _api.AddFile("a.txt", [1]);

        if (expected == "Read")
        {
            Assert.Single(await ListAsync(FakeSourceFileApi.ReadPath));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.ReadPath));
            Assert.Equal(Enum.Parse<RunIssueCode>(expected), ex.Code);
            Assert.Equal(0, _api.ReadDirectoryCalls);
        }

        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Theory]
    [InlineData(3, typeof(DirectoryNotFoundException))]
    [InlineData(2, typeof(FileNotFoundException))]
    [InlineData(5, typeof(UnauthorizedAccessException))]
    public async Task HU08_A_root_that_cannot_be_opened_throws_the_translated_error(int error, Type expected)
    {
        _api.Root.OpenError = error;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ListAsync(FakeSourceFileApi.ReadPath));

        Assert.IsType(expected, ex);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_A_root_that_is_a_file_is_a_read_error()
    {
        _api.Root.Attributes = FileAttributes.Archive;

        Assert.Equal(RunIssueCode.ReadError, (await Assert.ThrowsAsync<SourceAccessException>(() => ListAsync(FakeSourceFileApi.ReadPath))).Code);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    [Fact]
    public async Task HU08_3_When_the_root_check_fails_no_child_is_opened_through_it()
    {
        _api.Root.Attributes = FileAttributes.Directory | Reparse;
        _api.Root.ReparseTag = ReparseTags.MountPoint;
        _api.AddFile("secreto.txt", [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<SourceAccessException>(() => ReadAllAsync(FakeSourceFileApi.Child("secreto.txt")));

        Assert.Equal(RunIssueCode.ReparsePointSkipped, ex.Code);
        Assert.Equal(new[] { FakeSourceFileApi.OpenRoot }, _api.Opens.Select(o => o.Path).ToArray()); // solo se abrió la raíz
        Assert.Equal(0, _api.ReadCalls);
        Assert.Equal(_fs.HeldHandles, _api.OpenHandles);
    }

    // ================================================================== Ayudas

    /// <summary>Otro nodo para el segundo handle: igual que <paramref name="node"/> salvo lo que haga <paramref name="change"/>.</summary>
    private FakeNode Mirror(FakeNode node, Action<FakeNode> change)
    {
        var mirror = _api.AddNode(node.Win32Path + "#2", node.FinalNt, node.Attributes, node.ReparseTag, node.Content);
        mirror.Id = node.Id;
        mirror.PlaceholderState = node.PlaceholderState;
        mirror.FullyAllocated = node.FullyAllocated;
        change(mirror);
        return mirror;
    }

    private async Task<List<SourceEntry>> ListAsync(string path)
    {
        var list = new List<SourceEntry>();
        await foreach (var entry in _fs.EnumerateAsync(path, Ct))
        {
            list.Add(entry);
        }

        return list;
    }

    private async Task<byte[]> ReadAllAsync(string path)
    {
        await using var stream = await _fs.OpenReadAsync(path, Ct);
        return await ReadToEndAsync(stream);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, Ct)) > 0)
        {
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}
