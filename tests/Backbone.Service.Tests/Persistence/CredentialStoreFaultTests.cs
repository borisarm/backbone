using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-04 (RF-60, ADR-07, RNF-06): inyección de fallos sobre <see cref="CredentialStore"/> con DPAPI real (ámbito equipo):
/// DPAPI que falla, archivo vacío, truncado o con la cabecera alterada, entropía de otro recurso, borrado y guardado
/// concurrentes y cancelación. En todos los casos la contraseña centinela no sale en logs, excepciones ni archivos.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class CredentialStoreFaultTests : IDisposable
{
    private const string Share = @"\\nas\backups";
    private const string Other = @"\\nas\otro";
    private const string Password = "CENTINELA-hu04-Ñ€-9a2f";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));

    public CredentialStoreFaultTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(CredentialsDirectory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CredentialsDirectory => _temp.Combine("credentials");

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    private static CredentialInput Input(string user = "ana", string password = Password) => new() { UserName = user, Password = password };

    // ---- DPAPI que falla ----

    [Fact]
    public async Task When_DPAPI_fails_to_protect_nothing_is_written_and_the_previous_credential_survives()
    {
        var protector = new FaultyProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, Input("ana", "antigua"), Ct);
        var before = await File.ReadAllBytesAsync(store.PathFor(Share), Ct);
        protector.FailProtect = true;

        var ex = await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync(Share, Input("eva", Password), Ct));

        Assert.DoesNotContain(Password, ex.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PathFor(Share), Ct));
        Assert.Single(Directory.GetFiles(CredentialsDirectory));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(CredentialsDirectory), p => Path.GetFileName(p).EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal("ana", (await store.GetInfoAsync(Share, Ct))!.UserName);
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    [Fact]
    public async Task When_DPAPI_fails_to_protect_on_a_first_save_no_file_is_left()
    {
        using var store = CreateStore(new FaultyProtector { FailProtect = true });

        await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync(Share, Input(), Ct));

        Assert.Empty(Directory.GetFileSystemEntries(CredentialsDirectory));
        Assert.Null(await CreateStore().GetInfoAsync(Share, Ct));
    }

    [Fact]
    public async Task A_non_cryptographic_failure_of_DPAPI_while_reading_propagates_instead_of_being_taken_for_a_missing_credential()
    {
        // Un fallo inesperado del protector no es "no descifrable": no se debe disfrazar de CredentialRequired.
        var protector = new FaultyProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, Input(), Ct);
        protector.UnprotectException = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(Share, Ct));
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    // ---- Archivo vacío, truncado o con la cabecera alterada (DPAPI real) ----

    [Fact]
    public async Task A_zero_byte_file_counts_as_no_credential_and_is_replaced_on_save()
    {
        using var store = CreateStore();
        await File.WriteAllBytesAsync(store.PathFor(Share), [], Ct);

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(CredentialFileProblem.UnknownFormat), StringComparison.Ordinal));

        await store.SaveAsync(Share, Input(), Ct);
        Assert.Equal("ana", (await store.GetInfoAsync(Share, Ct))!.UserName);
    }

    [Fact]
    public async Task A_truncated_file_never_throws_and_never_yields_a_credential_at_any_length()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        var path = store.PathFor(Share);
        var full = await File.ReadAllBytesAsync(path, Ct);

        // Todos los prefijos propios: vacío, parte de la cabecera, solo la cabecera, blob cortado.
        for (var length = 0; length < full.Length; length += length < 16 ? 1 : 7)
        {
            File.Delete(path);
            await File.WriteAllBytesAsync(path, full.AsMemory(0, length), Ct);

            Assert.Null(await store.GetInfoAsync(Share, Ct));
            Assert.Null(await store.GetAsync(Share, Ct));
        }

        // Sin el último byte.
        File.Delete(path);
        await File.WriteAllBytesAsync(path, full.AsMemory(0, full.Length - 1), Ct);
        Assert.Null(await store.GetAsync(Share, Ct));
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    [Fact]
    public async Task A_file_with_trailing_bytes_after_the_blob_never_yields_altered_data()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        var path = store.PathFor(Share);
        var content = (await File.ReadAllBytesAsync(path, Ct)).Concat(new byte[] { 1, 2, 3 }).ToArray();
        File.Delete(path);
        await File.WriteAllBytesAsync(path, content, Ct);

        // DPAPI ignora los bytes tras el blob (que está autenticado): no puede colarse un contenido distinto.
        using var credential = await store.GetAsync(Share, Ct);
        Assert.True(credential is null || (credential.UserName, credential.Password.ToString()) == ("ana", Password));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Altering_any_byte_of_the_header_makes_the_file_unusable(int index)
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        var path = store.PathFor(Share);
        var content = await File.ReadAllBytesAsync(path, Ct);
        content[index] ^= 0x01;
        File.Delete(path);
        await File.WriteAllBytesAsync(path, content, Ct);

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(CredentialFileProblem.UnknownFormat), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flipping_a_bit_in_the_middle_of_the_blob_makes_it_undecryptable()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        var path = store.PathFor(Share);
        var content = await File.ReadAllBytesAsync(path, Ct);
        content[content.Length / 2] ^= 0x10;
        File.Delete(path);
        await File.WriteAllBytesAsync(path, content, Ct);

        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.NotDecryptable), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_of_exactly_the_maximum_size_is_read_and_rejected_as_an_unknown_format()
    {
        using var store = CreateStore();
        await File.WriteAllBytesAsync(store.PathFor(Share), new byte[CredentialStore.MaxFileBytes], Ct);
        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.UnknownFormat), StringComparison.Ordinal));
    }

    // ---- Entropía de otro recurso ----

    [Fact]
    public async Task A_blob_protected_with_the_entropy_of_another_share_is_not_decrypted()
    {
        using var store = CreateStore();
        await store.SaveAsync(Other, Input("luis"), Ct);
        // El archivo de OTRO recurso copiado con el nombre de este (el atacante con acceso a la carpeta).
        File.Copy(store.PathFor(Other), store.PathFor(Share));

        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.NotDecryptable), StringComparison.Ordinal));
        Assert.Equal("luis", (await store.GetInfoAsync(Other, Ct))!.UserName);
    }

    [Fact]
    public async Task A_blob_with_the_right_entropy_but_the_content_of_another_share_is_rejected_as_invalid_content()
    {
        // Doble defensa: aun descifrando, la raíz guardada dentro debe ser la del recurso consultado.
        using var store = CreateStore();
        await store.SaveAsync(Other, Input("luis"), Ct);
        var content = await File.ReadAllBytesAsync(store.PathFor(Other), Ct);
        var dpapi = new DpapiMachineProtector();
        var plaintext = dpapi.Unprotect(content.AsSpan(7), CredentialStore.EntropyFor(Other));
        var rewrapped = dpapi.Protect(plaintext, CredentialStore.EntropyFor(Share));
        CryptographicOperations.ZeroMemory(plaintext);
        var forged = new byte[7 + rewrapped.Length];
        content.AsSpan(0, 7).CopyTo(forged);
        rewrapped.CopyTo(forged, 7);
        await File.WriteAllBytesAsync(store.PathFor(Share), forged, Ct);

        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.InvalidContent), StringComparison.Ordinal));
    }

    [Fact]
    public void The_entropy_is_not_empty_and_differs_per_share()
    {
        var a = CredentialStore.EntropyFor(Share);
        var b = CredentialStore.EntropyFor(Other);

        Assert.NotEmpty(a);
        Assert.NotEqual(a, b);
        Assert.Throws<ArgumentException>(() => new DpapiMachineProtector().Protect("x"u8, ReadOnlySpan<byte>.Empty));
    }

    // ---- Concurrencia ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_or_replacing_while_reading_never_throws_and_readers_see_a_complete_credential_or_none(bool delete)
    {
        using var store = CreateStore();
        var errors = new List<Exception>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        async Task Reader()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var credential = await store.GetAsync(Share, CancellationToken.None);
                    if (credential is not null)
                    {
                        Assert.Equal(Password, credential.Password.ToString());
                        Assert.Equal("ana", credential.UserName);
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            }
        }

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(Reader, Ct)).ToArray();
        for (var i = 0; i < 40; i++)
        {
            await store.SaveAsync(Share, Input(), Ct);
            if (delete)
            {
                await store.DeleteAsync(Share, Ct);
            }
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => e.GetType().Name + ": " + e.Message).Distinct()));
        Assert.Equal(delete ? 0 : 1, Directory.GetFileSystemEntries(CredentialsDirectory).Length);
    }

    [Fact]
    public async Task Concurrent_saves_of_the_same_share_leave_exactly_one_complete_credential_and_no_temporaries()
    {
        using var store = CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => store.SaveAsync(Share, Input($"user{i}", Password + i), CancellationToken.None), Ct)));

        var file = Assert.Single(Directory.GetFileSystemEntries(CredentialsDirectory));
        Assert.Equal(store.PathFor(Share), file);
        using var credential = await store.GetAsync(Share, Ct);
        Assert.NotNull(credential);
        Assert.Equal("user" + credential.Password.ToString()[Password.Length..], credential.UserName); // usuario y contraseña de la misma escritura
    }

    [Fact]
    public async Task Concurrent_deletes_report_the_file_as_existing_exactly_once()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.DeleteAsync(Share, CancellationToken.None), Ct)));

        Assert.Equal(1, results.Count(deleted => deleted));
        Assert.Empty(Directory.GetFileSystemEntries(CredentialsDirectory));
    }

    [Fact]
    public async Task A_file_deleted_externally_between_save_and_read_is_just_no_credential()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        File.Delete(store.PathFor(Share));

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.False(await store.DeleteAsync(Share, Ct));
    }

    // ---- Cancelación ----

    [Fact]
    public async Task A_cancelled_save_writes_nothing_and_keeps_the_previous_credential()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input("ana", "antigua"), Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Share, Input("eva", Password), cts.Token));

        Assert.Single(Directory.GetFileSystemEntries(CredentialsDirectory));
        using var credential = await store.GetAsync(Share, Ct);
        Assert.Equal(("ana", "antigua"), (credential!.UserName, credential.Password.ToString()));
        SecretSearch.AssertNotInFolder(Password, _temp.Path);
    }

    [Fact]
    public async Task A_cancelled_read_does_not_corrupt_the_file()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, Input(), Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(Share, cts.Token));

        Assert.Equal("ana", (await store.GetInfoAsync(Share, Ct))!.UserName);
    }

    // ---- Criterio 3: la contraseña no aparece en ninguna parte tras un ciclo completo con fallos ----

    [Fact]
    public async Task After_saves_failures_and_deletes_the_sentinel_password_is_nowhere_in_the_data_folder_or_the_logs()
    {
        var protector = new FaultyProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, Input(), Ct);
        await store.SaveAsync(Other, Input("luis"), Ct);
        protector.FailProtect = true;
        await Assert.ThrowsAsync<CryptographicException>(() => store.SaveAsync(Share, Input(), Ct));
        protector.FailProtect = false;
        protector.FailUnprotect = true;
        Assert.Null(await store.GetAsync(Share, Ct));
        protector.FailUnprotect = false;
        using (await store.GetAsync(Share, Ct))
        {
        }

        File.WriteAllText(Path.Combine(CredentialsDirectory, "ajeno.bin"), "x");
        SecretSearch.AssertNotInFolder(Password, _temp.Path);
        SecretSearch.AssertNotInLogs(Password, _logs);
        SecretSearch.AssertNotInLogs("ana", _logs);
        SecretSearch.AssertNotInLogs("luis", _logs);
        await store.DeleteAsync(Share, Ct);
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    private CredentialStore CreateStore(IDataProtector? protector = null) =>
        new(CredentialsDirectory, TestFileTrust.CurrentUser, protector ?? new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());

    /// <summary>DPAPI real con interruptores para simular fallos de cifrado o descifrado.</summary>
    private sealed class FaultyProtector : IDataProtector
    {
        private readonly DpapiMachineProtector _inner = new();

        public bool FailProtect { get; set; }

        public bool FailUnprotect { get; set; }

        public Exception? UnprotectException { get; set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            FailProtect ? throw new CryptographicException("Fake protect failure.") : _inner.Protect(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            if (UnprotectException is { } exception)
            {
                throw exception;
            }

            return FailUnprotect ? throw new CryptographicException("Fake unprotect failure.") : _inner.Unprotect(ciphertext, entropy);
        }
    }
}
