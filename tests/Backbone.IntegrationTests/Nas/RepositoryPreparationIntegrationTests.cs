using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.IntegrationTests.Nas;

/// <summary>
/// HU-06 contra un recurso SMB real (<see cref="WindowsShareConnector"/>, <see cref="UncRepositoryStorageFactory"/> y
/// <see cref="FileSystemRepositoryStorage"/>). No requieren elevación. Usan las mismas variables de entorno que
/// <see cref="WindowsShareConnectorTests"/> (<c>BACKBONE_IT_SMB_SHARE</c>, <c>BACKBONE_IT_SMB_USER</c>,
/// <c>BACKBONE_IT_SMB_PASSWORD</c>); sin ellas se omiten. Cada ejecución trabaja en una subcarpeta única del recurso
/// (<c>backbone-it-hu06-*</c>) que se borra al terminar.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-06")]
public sealed class RepositoryPreparationIntegrationTests : IAsyncLifetime
{
    private const string NoShare = "Define BACKBONE_IT_SMB_SHARE, BACKBONE_IT_SMB_USER y BACKBONE_IT_SMB_PASSWORD para probar contra un recurso SMB real.";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialStore _store;
    private readonly ShareConnectionManager _connections;
    private readonly string _folderName = "backbone-it-hu06-" + Guid.NewGuid().ToString("N")[..12];
    private string _root = string.Empty;
    private string _destination = string.Empty;

    public RepositoryPreparationIntegrationTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), TimeProvider.System, _loggerFactory.CreateLogger<CredentialStore>());
        _connections = new ShareConnectionManager(new WindowsShareConnector(_loggerFactory.CreateLogger<WindowsShareConnector>()), _loggerFactory.CreateLogger<ShareConnectionManager>());
    }

    public static bool HasShare => Share is not null && User is not null && Password is not null;

    private static string? Share => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_SHARE");

    private static string? User => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_USER");

    private static string? Password => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_PASSWORD");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!HasShare)
        {
            return;
        }

        Assert.True(UncPaths.TryParse(Share, out var root, out var baseDestination, out _));
        _root = root;
        _destination = baseDestination.TrimEnd('\\') + "\\" + _folderName;
        await _store.SaveAsync(root, new CredentialInput { UserName = User!, Password = Password! }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (HasShare && _root.Length > 0)
            {
                using var credential = (await _store.GetAsync(_root, CancellationToken.None))!;
                await using var lease = await _connections.AcquireAsync(credential, CancellationToken.None);
                if (Directory.Exists(_destination))
                {
                    Directory.Delete(_destination, recursive: true);
                }
            }
        }
        finally
        {
            _store.Dispose();
            _loggerFactory.Dispose();
            _temp.Dispose();
        }
    }

    private RepositoryPreparer NewPreparer() => new(
        new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
        _connections,
        new UncRepositoryStorageFactory(),
        new ServiceRuntimeInfo(TimeProvider.System),
        TimeProvider.System,
        new RepositoryPreparationOptions { Timeout = TimeSpan.FromSeconds(60) },
        _loggerFactory.CreateLogger<RepositoryPreparer>());

    /// <summary>Operaciones directas sobre la carpeta de la prueba, con una conexión abierta del mismo recurso.</summary>
    private async Task<T> WithConnectionAsync<T>(Func<IRepositoryStorage, Task<T>> action)
    {
        using var credential = (await _store.GetAsync(_root, Ct))!;
        await using var lease = await _connections.AcquireAsync(credential, Ct);
        return await action(new FileSystemRepositoryStorage(_destination));
    }

    private Task<int> CreateFolderAsync() => WithConnectionAsync(async s =>
    {
        await s.CreateDirectoryAsync(string.Empty, Ct);
        return 0;
    });

    private string[] Entries() => [.. Directory.EnumerateFileSystemEntries(_destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>Criterio 1: la carpeta no existe → se inicializa un repositorio v1.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_missing_folder_on_the_real_share_is_initialized()
    {
        var outcome = await NewPreparer().PrepareAsync(_destination, Ct);

        Assert.Equal(RepositoryPreparationOutcome.Initialized, outcome);
        Assert.True(File.Exists(Path.Combine(_destination, "repo.json")));
        var probe = await WithConnectionAsync(s => BackupRepository.ProbeAsync(s, Ct));
        Assert.Equal(Backbone.Core.Repository.RepositoryState.Compatible, probe.State);
        Assert.DoesNotContain(Password!, _logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>Criterio 1: la carpeta existe y está vacía → se inicializa.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task An_empty_folder_on_the_real_share_is_initialized()
    {
        await CreateFolderAsync();

        var outcome = await NewPreparer().PrepareAsync(_destination, Ct);

        Assert.Equal(RepositoryPreparationOutcome.Initialized, outcome);
    }

    /// <summary>Criterio 2: el repositorio recién creado se adopta sin escribir.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task The_repository_just_created_is_adopted_without_writing()
    {
        var preparer = NewPreparer();
        Assert.Equal(RepositoryPreparationOutcome.Initialized, await preparer.PrepareAsync(_destination, Ct));
        var repoJson = Path.Combine(_destination, "repo.json");
        var idBefore = await WithConnectionAsync(async s => (await BackupRepository.OpenAsync(s, TimeProvider.System, Ct)).Configuration.RepositoryId);
        var entriesBefore = Entries();
        var writtenBefore = File.GetLastWriteTimeUtc(repoJson);
        var bytesBefore = await File.ReadAllBytesAsync(repoJson, Ct);

        var outcome = await preparer.PrepareAsync(_destination, Ct);

        Assert.Equal(RepositoryPreparationOutcome.Adopted, outcome);
        Assert.Equal(entriesBefore, Entries());
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(repoJson));
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(repoJson, Ct));
        Assert.Equal(idBefore, await WithConnectionAsync(async s => (await BackupRepository.OpenAsync(s, TimeProvider.System, Ct)).Configuration.RepositoryId));
    }

    /// <summary>Criterio 3: una carpeta con archivos ajenos se rechaza y no se escribe nada.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_folder_with_other_files_on_the_real_share_is_rejected_and_untouched()
    {
        await CreateFolderAsync();
        var notes = Path.Combine(_destination, "notas.txt");
        await File.WriteAllTextAsync(notes, "no soy un repositorio", Ct);
        var entriesBefore = Entries();

        var ex = await Assert.ThrowsAsync<BackboneException>(() => NewPreparer().PrepareAsync(_destination, Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(entriesBefore, Entries());
        Assert.Equal("no soy un repositorio", await File.ReadAllTextAsync(notes, Ct));
        Assert.False(File.Exists(Path.Combine(_destination, "repo.json")));
        Assert.False(Directory.Exists(Path.Combine(_destination, "tmp")));
    }

    /// <summary>Criterio 4: <c>repo.json</c> de formato 2 → solo lectura y nada escrito.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_newer_format_repository_on_the_real_share_is_read_only_and_untouched()
    {
        await CreateFolderAsync();
        var repoJson = Path.Combine(_destination, "repo.json");
        await File.WriteAllTextAsync(repoJson, """{"formatVersion":2,"repositoryId":"8f6c2e1a-0000-4000-8000-000000000001","hash":"sha256","compression":"none","encryption":"none"}""", Ct);
        var bytesBefore = await File.ReadAllBytesAsync(repoJson, Ct);

        var outcome = await NewPreparer().PrepareAsync(_destination, Ct);

        Assert.Equal(RepositoryPreparationOutcome.ReadOnlyNewerFormat, outcome);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(repoJson, Ct));
        Assert.Equal([repoJson], Entries());
    }

    /// <summary>
    /// ALTA-1 / §8.2 B-1: con la conexión viva Windows tiene el registro de uso de la raíz (<c>WNetOpenEnum</c> con
    /// <c>RESOURCE_CONNECTED</c>); tras liberarla ya no, y una nueva adquisición inmediata lo vuelve a tener (espera a que
    /// termine la desconexión).
    /// </summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task The_use_record_exists_while_connected_and_a_quick_reacquire_keeps_it()
    {
        using var credential = (await _store.GetAsync(_root, Ct))!;
        var first = await _connections.AcquireAsync(credential, Ct);
        Assert.True(await first.Connection.HasUseRecordAsync(Ct));
        Assert.True(WindowsShareConnector.UseRecordExists(_root));

        var release = first.DisposeAsync().AsTask();
        await using var second = await _connections.AcquireAsync(credential, Ct);
        await release;

        Assert.True(await second.Connection.HasUseRecordAsync(Ct));
    }

    /// <summary>
    /// <c>WNetOpenEnum</c> con <c>RESOURCE_CONNECTED</c> lista las conexiones sin letra de unidad: se comprueba contra una
    /// conexión real al propio equipo hecha con <c>net use</c> (sin elevar, sin recurso externo).
    /// </summary>
    [Fact]
    public async Task UseRecordExists_sees_a_connection_without_a_drive_letter()
    {
        const string Ipc = @"\\localhost\IPC$";
        if (await NetUseAsync(Ipc) != 0)
        {
            Assert.Skip(@"No se pudo abrir una conexión de prueba con \\localhost\IPC$.");
        }

        try
        {
            Assert.True(WindowsShareConnector.UseRecordExists(Ipc));
            Assert.True(WindowsShareConnector.UseRecordExists(Ipc.ToUpperInvariant()));
            Assert.False(WindowsShareConnector.UseRecordExists(@"\\localhost\no-existe-backbone"));
        }
        finally
        {
            _ = await NetUseAsync(Ipc, "/delete");
        }

        Assert.False(WindowsShareConnector.UseRecordExists(Ipc));
    }

    private static async Task<int> NetUseAsync(string remote, string? option = null)
    {
        var info = new System.Diagnostics.ProcessStartInfo("net", option is null ? $"use {remote}" : $"use {remote} {option}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("No se pudo ejecutar net use.");
        _ = await process.StandardOutput.ReadToEndAsync(Ct);
        _ = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return process.ExitCode;
    }
}
