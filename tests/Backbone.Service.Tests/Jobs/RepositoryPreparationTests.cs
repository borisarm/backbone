using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Backbone.Core.Repository;
using Backbone.Core.Storage;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Outcome = Backbone.Service.Jobs.RepositoryPreparationOutcome;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-06 (RF-63, RNF-14): al guardar un trabajo, el servicio inicializa o adopta el repositorio del destino, sobre un
/// <see cref="IRepositoryStorage"/> en memoria y un recurso SMB simulado. Cada prueba comprueba también que la conexión se
/// suelta y que el destino no se toca cuando no debe.
/// </summary>
[Trait("Story", "HU-06")]
public sealed class RepositoryPreparationTests : IAsyncLifetime
{
    private const string Documents = @"C:\Users\ana\Documentos";
    private const string Destination = @"\\nas\backups\PC-Salon";
    private const string Password = "Contraseña-HU06";

    private readonly IpcTestHarness _server = new();
    private TestConnection _ana = null!; // Asignado en InitializeAsync.

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server.FileSystem.AddFolder(Documents);
        await _server.Jobs.LoadAsync(Ct);
        await _server.AuthorizeAsync(TestSids.Ana);
        _ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        await _ana.Proxy.SaveCredentialAsync(@"\\nas\backups", new CredentialInput { UserName = "ana", Password = Password }, Ct);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private static JobDefinition NewJob(string destination = Destination) =>
        JobDefaults.NewJob() with
        {
            Name = "Documentos",
            Sources = [new JobSource { Alias = "Documentos", Path = Documents }],
            Destination = destination,
        };

    private InMemoryRepositoryStorage Storage => _server.RepositoryStorages.Memory(Destination);

    /// <summary>Instantánea del contenido: carpetas y archivos con su hash, para comprobar que no se ha escrito nada.</summary>
    private static string[] Dump(InMemoryRepositoryStorage storage) =>
    [
        .. storage.DirectoryPaths.Select(d => "D " + d),
        .. storage.FilePaths.Select(f => "F " + f + " " + Convert.ToHexString(SHA256.HashData(storage.GetFileContent(f)))),
    ];

    private static void Seed(InMemoryRepositoryStorage storage, string path, string content) =>
        storage.SetFileContent(path, Encoding.UTF8.GetBytes(content));

    private static async Task<BackupRepository> SeedRepositoryAsync(InMemoryRepositoryStorage storage, string createdBy = "Backbone 0.9 en PC-VIEJO")
    {
        await storage.CreateDirectoryAsync(string.Empty, Ct);
        return await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = createdBy }, TimeProvider.System, Ct);
    }

    private static string RepoJson(int formatVersion, string hash = "sha256") => $$"""
        {
          "formatVersion": {{formatVersion}},
          "repositoryId": "8f6c2e1a-0000-4000-8000-000000000001",
          "createdUtc": "2026-11-15T02:00:00Z",
          "createdBy": "Backbone 2.0.0 en PC-FUTURO",
          "hash": "{{hash}}",
          "compression": "none",
          "encryption": "none"
        }
        """;

    private void AssertConnectionReleased()
    {
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
        Assert.NotEmpty(_server.ShareConnector.Created);
        Assert.All(_server.ShareConnector.Created, c => Assert.True(c.IsDisposed));
        Assert.All(_server.ShareConnector.Created, c => Assert.False(c.DisposedWithOperationsInFlight));
        Assert.True(FakeShareConnector.IsDisposed(_server.ShareConnector.LastCredential!));
    }

    // ---- Criterio 1: destino vacío ----

    [Fact]
    public async Task A_missing_destination_is_initialized_when_the_job_is_saved()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        var repository = await BackupRepository.OpenAsync(Storage, TimeProvider.System, Ct);
        Assert.Equal(1, repository.Configuration.FormatVersion);
        Assert.Equal($"Backbone {_server.Runtime.Version} en {_server.Runtime.MachineName}", repository.Configuration.CreatedBy);
        Assert.Empty(await repository.ListSnapshotIdsAsync(Ct));
        Assert.Equal(saved.Id, Assert.Single(await _ana.Proxy.GetJobsAsync(Ct)).Id);
        Assert.Equal(Contracts.RepositoryState.ReadWrite, Assert.Single((await _ana.Proxy.GetStatusAsync(Ct)).Jobs).RepositoryState);
        AssertConnectionReleased();
    }

    [Fact]
    public async Task An_existing_empty_folder_is_initialized()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(Backbone.Core.Repository.RepositoryState.Compatible, (await BackupRepository.ProbeAsync(Storage, Ct)).State);
        AssertConnectionReleased();
    }

    [Fact]
    public async Task The_repository_is_created_with_the_credential_saved_for_the_share_and_the_password_is_never_logged()
    {
        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        var connection = Assert.Single(_server.ShareConnector.Connections);
        Assert.Equal((@"\\nas\backups", "ana"), (connection.ShareRoot, connection.UserName));
        Assert.True(_server.ShareConnector.PasswordReadableAtEndOfConnect);
        Assert.DoesNotContain(Password, _server.Logs.AllText(), StringComparison.Ordinal);
        Assert.Contains(_server.Logs.Entries, e => e.Message.Contains("Repositorio inicializado", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saving_the_same_job_again_adopts_the_repository_it_created_without_writing()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        var before = Dump(Storage);
        var id = (await BackupRepository.OpenAsync(Storage, TimeProvider.System, Ct)).Configuration.RepositoryId;

        await _ana.Proxy.SaveJobAsync(saved with { Name = "Documentos de Ana" }, Ct);

        Assert.Equal(before, Dump(Storage));
        Assert.Equal(id, (await BackupRepository.OpenAsync(Storage, TimeProvider.System, Ct)).Configuration.RepositoryId);
    }

    // ---- Criterio 2: repositorio v1 válido ----

    [Fact]
    public async Task A_valid_v1_repository_is_adopted_without_writing_and_keeps_its_snapshots()
    {
        var existing = await SeedRepositoryAsync(Storage);
        Seed(Storage, "snapshots/20261115T020000Z-a1b2.json", "{}");
        var before = Dump(Storage);

        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(before, Dump(Storage));
        var adopted = await BackupRepository.OpenAsync(Storage, TimeProvider.System, Ct);
        Assert.Equal(existing.Configuration.RepositoryId, adopted.Configuration.RepositoryId);
        Assert.Equal(["20261115T020000Z-a1b2"], await adopted.ListSnapshotIdsAsync(Ct));
        Assert.Equal(Contracts.RepositoryState.ReadWrite, Assert.Single((await _ana.Proxy.GetStatusAsync(Ct)).Jobs).RepositoryState);
        Assert.Equal(saved.Id, Assert.Single(await _ana.Proxy.GetJobsAsync(Ct)).Id);
        Assert.Contains(_server.Logs.Entries, e => e.Message.Contains("adoptado", StringComparison.Ordinal) && e.Message.Contains("PC-VIEJO", StringComparison.Ordinal));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task Another_installation_publishing_the_repository_first_is_adopted_not_failed()
    {
        // Entre el examen y la publicación de repo.json otro proceso inicializa el destino (§6.2: "ya inicializado").
        var other = new InMemoryRepositoryStorage();
        await BackupRepository.InitializeAsync(other, new RepositoryInitializationOptions { CreatedBy = "otro" }, TimeProvider.System, Ct);
        var otherConfig = other.GetFileContent("repo.json");
        var inner = Storage;
        _server.RepositoryStorages.Set(
            Destination,
            new HookedStorage(inner)
            {
                AfterCreateDirectory = (path, inner) =>
                {
                    if (path == "tmp")
                    {
                        inner.SetFileContent("repo.json", otherConfig);
                    }
                },
            });

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(otherConfig, inner.GetFileContent("repo.json"));
        Assert.Single(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    // ---- Criterio 3: archivos que no son un repositorio ----

    [Fact]
    public async Task A_destination_with_other_files_fails_explaining_it_and_nothing_is_written()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        await Storage.CreateDirectoryAsync("fotos", Ct);
        Seed(Storage, "fotos/vacaciones.jpg", "no es un repositorio");
        Seed(Storage, "notas.txt", "hola");
        var before = Dump(Storage);

        await RpcAssert.ValidationFailsAsync("Destination", ValidationIssueCode.DestinationNotRepository, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Equal(before, Dump(Storage));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        Assert.False(File.Exists(Path.Combine(_server.DataRoot, "config", "jobs.json")));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task Changing_the_destination_of_a_saved_job_to_a_folder_with_files_leaves_the_job_untouched()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        const string OtherDestination = @"\\nas\backups\otro";
        var other = _server.RepositoryStorages.Memory(OtherDestination);
        await other.CreateDirectoryAsync(string.Empty, Ct);
        Seed(other, "algo.bin", "x");

        await RpcAssert.ValidationFailsAsync(
            "Destination", ValidationIssueCode.DestinationNotRepository, () => _ana.Proxy.SaveJobAsync(saved with { Destination = OtherDestination }, Ct));

        Assert.Equal(Destination, Assert.Single(await _ana.Proxy.GetJobsAsync(Ct)).Destination);
        Assert.Equal(["F algo.bin " + Convert.ToHexString(SHA256.HashData("x"u8))], Dump(other));
    }

    [Fact]
    public async Task A_damaged_repository_is_RepositoryCorrupt_and_is_not_recreated()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", "esto no es json {");
        var before = Dump(Storage);

        await RpcAssert.FailsWithAsync(BackboneErrorCode.RepositoryCorrupt, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Equal(before, Dump(Storage));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    // ---- Criterio 4: formato superior (RNF-14) ----

    [Fact]
    public async Task A_newer_format_repository_is_saved_read_only_and_never_written()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 2));
        var before = Dump(Storage);

        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(before, Dump(Storage));
        var job = Assert.Single((await _ana.Proxy.GetStatusAsync(Ct)).Jobs);
        Assert.Equal((saved.Id, Contracts.RepositoryState.ReadOnlyNewerFormat), (job.JobId, job.RepositoryState));
        Assert.Contains(_server.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("formato superior", StringComparison.Ordinal));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task A_newer_format_is_decided_by_formatVersion_even_if_the_rest_of_repo_json_is_not_understood()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", """{ "formatVersion": 7, "algo": [1, 2, { "nuevo": true }], "repositoryId": 5 }""");
        var before = Dump(Storage);

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(before, Dump(Storage));
        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, Assert.Single((await _ana.Proxy.GetStatusAsync(Ct)).Jobs).RepositoryState);
    }

    [Fact]
    public async Task A_repository_with_features_of_a_later_format_is_read_only()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 1, hash: "blake3"));

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, Assert.Single((await _ana.Proxy.GetStatusAsync(Ct)).Jobs).RepositoryState);
    }

    [Fact]
    public async Task StatusChanged_carries_the_read_only_state_already_in_the_event_of_the_save()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 2));
        var events = new ConcurrentQueue<StatusChangedEvent>();
        var received = new SemaphoreSlim(0);
        _ana.Proxy.StatusChanged += (_, e) =>
        {
            events.Enqueue(e);
            received.Release();
        };

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, Assert.Single(Assert.Single(events).Status.Jobs).RepositoryState);
    }

    [Fact]
    public async Task Saving_again_refreshes_the_state_and_deleting_the_job_forgets_it()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 2));
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        Assert.Equal(Contracts.RepositoryState.ReadOnlyNewerFormat, _server.RepositoryStates.Get(saved.Id));

        // Se vuelve a apuntar el destino a una carpeta vacía: se inicializa.
        _server.RepositoryStorages.Set(Destination, new InMemoryRepositoryStorage(createRoot: false));
        await _ana.Proxy.SaveJobAsync(saved, Ct);
        Assert.Equal(Contracts.RepositoryState.ReadWrite, _server.RepositoryStates.Get(saved.Id));

        await _ana.Proxy.DeleteJobAsync(saved.Id, Ct);
        Assert.Equal(Contracts.RepositoryState.Unknown, _server.RepositoryStates.Get(saved.Id));
    }

    [Fact]
    public async Task A_failed_save_does_not_change_the_state_of_the_job()
    {
        var saved = await _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        Assert.Equal(Contracts.RepositoryState.ReadWrite, _server.RepositoryStates.Get(saved.Id));
        var damaged = _server.RepositoryStorages.Memory(@"\\nas\backups\roto");
        await damaged.CreateDirectoryAsync(string.Empty, Ct);
        Seed(damaged, "repo.json", "{");

        await RpcAssert.FailsWithAsync(BackboneErrorCode.RepositoryCorrupt, () => _ana.Proxy.SaveJobAsync(saved with { Destination = @"\\nas\backups\roto" }, Ct));

        Assert.Equal(Contracts.RepositoryState.ReadWrite, _server.RepositoryStates.Get(saved.Id));
        Assert.Equal(Destination, Assert.Single(await _ana.Proxy.GetJobsAsync(Ct)).Destination);
    }

    // ---- Fallos del destino y liberación de la conexión ----

    [Fact]
    public async Task Without_a_credential_it_does_not_connect_or_touch_the_destination()
    {
        await RpcAssert.FailsWithAsync(BackboneErrorCode.CredentialRequired, () => _ana.Proxy.SaveJobAsync(NewJob(@"\\otro-nas\copias"), Ct));

        Assert.Empty(_server.ShareConnector.Connections);
        Assert.Empty(_server.RepositoryStorages.Requested);
    }

    [Theory]
    [InlineData(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorLogonFailure)]
    [InlineData(BackboneErrorCode.NasUnreachable, 53)]
    [InlineData(BackboneErrorCode.ShareNotFound, 67)]
    public async Task Connection_failures_keep_their_code_and_nothing_is_saved(BackboneErrorCode code, int win32)
    {
        _server.ShareConnector.ConnectError = new ShareException(code, win32);

        await RpcAssert.FailsWithAsync(code, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        Assert.Empty(_server.RepositoryStorages.Requested);
        Assert.Equal(0, _server.ShareConnector.OpenConnections);
        Assert.True(FakeShareConnector.IsDisposed(_server.ShareConnector.LastCredential!));
        Assert.DoesNotContain(Password, _server.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_IO_error_in_the_destination_is_translated_and_the_connection_is_released()
    {
        _server.RepositoryStorages.Set(Destination, new HookedStorage(Storage) { FailCreateDirectory = new UnauthorizedAccessException("sin permiso") });

        await RpcAssert.FailsWithAsync(BackboneErrorCode.AccessDenied, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task An_unexpected_error_releases_the_connection_too()
    {
        _server.RepositoryStorages.Set(Destination, new HookedStorage(Storage) { FailCreateDirectory = new InvalidOperationException("error de programación") });

        await Assert.ThrowsAnyAsync<Exception>(() => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task A_NAS_that_does_not_answer_is_NasUnreachable_after_the_timeout_and_the_connection_is_released_when_it_finishes()
    {
        var gate = _server.ShareConnector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = _ana.Proxy.SaveJobAsync(NewJob(), Ct);
        await WaitUntilAsync(() => !_server.ShareConnector.Connections.IsEmpty);

        _server.Time.Advance(_server.PreparationOptions.Timeout + TimeSpan.FromSeconds(1));

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NasUnreachable, () => call);
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));

        gate.SetResult();
        await _server.Repositories.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        AssertConnectionReleased();
    }

    [Fact]
    public async Task Cancelling_the_call_releases_the_connection_and_writes_nothing()
    {
        var gate = _server.ShareConnector.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = _server.Repositories.PrepareAsync(Destination, cts.Token);
        await WaitUntilAsync(() => !_server.ShareConnector.Connections.IsEmpty);

        // La llamada al sistema no se interrumpe: se responde cuando vuelve (ver RepositoryPreparationLimitsTests).
        await cts.CancelAsync();
        gate.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await _server.Repositories.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        AssertConnectionReleased();
        Assert.Empty(Dump(Storage));
    }

    // ---- Errores de E/S del almacenamiento: nunca "vacío" (Core, HU-06) ----

    // 0x80070040 = HRESULT_FROM_WIN32(ERROR_NETNAME_DELETED), 0x80070035 = ERROR_BAD_NETPATH: la sesión SMB se reinició.
    [Theory]
    [InlineData("DirectoryExists", 0x80070040, BackboneErrorCode.NasUnreachable)]
    [InlineData("OpenRead", 0x80070035, BackboneErrorCode.NasUnreachable)]
    [InlineData("Enumerate", 0x80070040, BackboneErrorCode.NasUnreachable)]
    public async Task A_network_error_reading_the_destination_is_NasUnreachable_and_never_initializes(string operation, uint hresult, BackboneErrorCode expected)
    {
        var inner = Storage;
        await inner.CreateDirectoryAsync(string.Empty, Ct);
        Seed(inner, "algo-que-parece-vacio.bin", "x");
        var before = Dump(inner);
        _server.RepositoryStorages.Set(
            Destination,
            new HookedStorage(inner) { FailRead = op => op == operation ? new IOException("red", unchecked((int)hresult)) : null });

        await RpcAssert.FailsWithAsync(expected, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Equal(before, Dump(inner));
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    [Theory]
    [InlineData("DirectoryExists")]
    [InlineData("OpenRead")]
    [InlineData("Enumerate")]
    public async Task Access_denied_reading_the_destination_is_AccessDenied_and_never_initializes(string operation)
    {
        var inner = Storage;
        await inner.CreateDirectoryAsync(string.Empty, Ct);
        var before = Dump(inner);
        _server.RepositoryStorages.Set(
            Destination,
            new HookedStorage(inner) { FailRead = op => op == operation ? new UnauthorizedAccessException("sin permiso") : null });

        await RpcAssert.FailsWithAsync(BackboneErrorCode.AccessDenied, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Equal(before, Dump(inner));
        Assert.Empty(inner.FilePaths);
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    [Fact]
    public async Task Foreign_files_appearing_while_initializing_are_DestinationNotRepository_and_repo_json_is_not_published()
    {
        // El examen dio "vacío"; antes de publicar repo.json aparece un archivo ajeno (Core vuelve a examinar y lanza NotEmpty).
        var inner = Storage;
        _server.RepositoryStorages.Set(
            Destination,
            new HookedStorage(inner)
            {
                AfterCreateDirectory = (path, memory) =>
                {
                    if (path == "tmp")
                    {
                        memory.SetFileContent("ajeno.txt", "no es de Backbone"u8);
                    }
                },
            });

        await RpcAssert.ValidationFailsAsync("Destination", ValidationIssueCode.DestinationNotRepository, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.DoesNotContain("repo.json", inner.FilePaths);
        Assert.Contains("ajeno.txt", inner.FilePaths);
        Assert.Empty(await _ana.Proxy.GetJobsAsync(Ct));
        AssertConnectionReleased();
    }

    // ---- Directo contra el preparador ----

    [Fact]
    public async Task The_preparer_reports_what_it_did()
    {
        Assert.Equal(Outcome.Initialized, await _server.Repositories.PrepareAsync(Destination, Ct));
        Assert.Equal(Outcome.Adopted, await _server.Repositories.PrepareAsync(Destination, Ct));
        Seed(Storage, "repo.json", RepoJson(formatVersion: 9));
        Assert.Equal(Outcome.ReadOnlyNewerFormat, await _server.Repositories.PrepareAsync(Destination, Ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(10, Ct);
        }
    }

    // ---- Auditoría QA: "sin escribir" verificado sobre las operaciones del almacenamiento ----

    private RecordingStorage Record()
    {
        var recording = new RecordingStorage(Storage);
        _server.RepositoryStorages.Set(Destination, recording);
        return recording;
    }

    [Fact]
    public async Task Adopting_a_v1_repository_performs_no_write_operation_on_the_storage()
    {
        await SeedRepositoryAsync(Storage);
        Seed(Storage, "snapshots/20261115T020000Z-a1b2.json", "{}");
        var recording = Record();

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Empty(recording.Writes);
        Assert.NotEmpty(recording.Reads);
    }

    [Fact]
    public async Task Rejecting_a_folder_with_files_performs_no_write_operation_on_the_storage()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "notas.txt", "hola");
        var recording = Record();

        await RpcAssert.ValidationFailsAsync("Destination", ValidationIssueCode.DestinationNotRepository, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(recording.Writes);
    }

    [Fact]
    public async Task A_newer_format_repository_performs_no_write_operation_on_the_storage()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 2));
        var recording = Record();

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Empty(recording.Writes);
    }

    [Fact]
    public async Task A_damaged_repository_performs_no_write_operation_on_the_storage()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", "esto no es json {");
        var recording = Record();

        await RpcAssert.FailsWithAsync(BackboneErrorCode.RepositoryCorrupt, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(recording.Writes);
    }

    [Fact]
    public async Task A_folder_with_only_hidden_or_unrelated_subfolders_is_not_a_repository_and_is_not_written()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        await Storage.CreateDirectoryAsync("carpeta vacía", Ct);
        Seed(Storage, "carpeta vacía/ñ.txt", "x");
        var recording = Record();

        await RpcAssert.ValidationFailsAsync("Destination", ValidationIssueCode.DestinationNotRepository, () => _ana.Proxy.SaveJobAsync(NewJob(), Ct));

        Assert.Empty(recording.Writes);
    }

    [Fact]
    public async Task The_newer_format_repository_keeps_its_snapshots_untouched_for_later_reading()
    {
        await Storage.CreateDirectoryAsync(string.Empty, Ct);
        Seed(Storage, "repo.json", RepoJson(formatVersion: 3));
        await Storage.CreateDirectoryAsync("snapshots", Ct);
        Seed(Storage, "snapshots/20261115T020000Z-a1b2.json", "{}");
        var before = Dump(Storage);

        await _ana.Proxy.SaveJobAsync(NewJob(), Ct);

        Assert.Equal(before, Dump(Storage));
    }

    /// <summary>Delega en un almacenamiento en memoria y anota las operaciones de lectura y de escritura.</summary>
    private sealed class RecordingStorage(InMemoryRepositoryStorage inner) : IRepositoryStorage
    {
        private readonly ConcurrentQueue<string> _writes = new();
        private readonly ConcurrentQueue<string> _reads = new();

        public IReadOnlyCollection<string> Writes => [.. _writes];

        public IReadOnlyCollection<string> Reads => [.. _reads];

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            _reads.Enqueue("OpenRead " + path);
            return inner.OpenReadAsync(path, cancellationToken);
        }

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            _writes.Enqueue("CreateNew " + path);
            return inner.CreateNewAsync(path, cancellationToken);
        }

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            _writes.Enqueue($"Move {sourcePath} -> {destinationPath}");
            return inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            _writes.Enqueue("Delete " + path);
            return inner.DeleteFileAsync(path, cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            _writes.Enqueue("CreateDirectory " + path);
            return inner.CreateDirectoryAsync(path, cancellationToken);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
        {
            _reads.Enqueue("DirectoryExists " + path);
            return inner.DirectoryExistsAsync(path, cancellationToken);
        }

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
        {
            _reads.Enqueue("GetFileInfo " + path);
            return inner.GetFileInfoAsync(path, cancellationToken);
        }

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken)
        {
            _reads.Enqueue("Enumerate " + directoryPath);
            return inner.EnumerateAsync(directoryPath, cancellationToken);
        }
    }

    /// <summary>Almacenamiento que delega en uno en memoria y permite fallar o reaccionar al crear carpetas.</summary>
    private sealed class HookedStorage(InMemoryRepositoryStorage inner) : IRepositoryStorage
    {
        public Exception? FailCreateDirectory { get; init; }

        public Action<string, InMemoryRepositoryStorage>? AfterCreateDirectory { get; init; }

        /// <summary>Error de las lecturas (<c>DirectoryExists</c>, <c>GetFileInfo</c>, <c>OpenRead</c>, <c>Enumerate</c>) según la operación.</summary>
        public Func<string, Exception?>? FailRead { get; init; }

        private void MaybeFail(string operation)
        {
            if (FailRead?.Invoke(operation) is { } failure)
            {
                throw failure;
            }
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            MaybeFail("OpenRead");
            return inner.OpenReadAsync(path, cancellationToken);
        }

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            if (FailCreateDirectory is { } failure)
            {
                throw failure;
            }

            await inner.CreateDirectoryAsync(path, cancellationToken);
            AfterCreateDirectory?.Invoke(path, inner);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
        {
            MaybeFail("DirectoryExists");
            return inner.DirectoryExistsAsync(path, cancellationToken);
        }

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
        {
            MaybeFail("GetFileInfo");
            return inner.GetFileInfoAsync(path, cancellationToken);
        }

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken)
        {
            MaybeFail("Enumerate");
            return inner.EnumerateAsync(directoryPath, cancellationToken);
        }
    }
}
