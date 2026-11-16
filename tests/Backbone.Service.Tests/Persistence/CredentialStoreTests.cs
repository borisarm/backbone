using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Ipc;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-04 (RF-60, RF-62, ADR-07, RNF-06): <see cref="CredentialStore"/> sobre una carpeta temporal con DPAPI real (ámbito
/// equipo) o con un doble del protector. La carpeta <c>credentials\</c> la crea la prueba, como el servicio al arrancar.
/// </summary>
[Trait("Story", "HU-04")]
public sealed class CredentialStoreTests : IDisposable
{
    private const string Share = @"\\nas\backups";
    private const string Password = "Contraseña-secreta-7c1e";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 10, 30, 0, TimeSpan.Zero));

    public CredentialStoreTests()
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

    // ---- Criterio 1: archivo en credentials\ cifrado con DPAPI LocalMachine y entropía propia ----

    [Fact]
    public async Task Saving_creates_one_file_in_credentials_encrypted_with_DPAPI_and_the_own_entropy()
    {
        using var store = CreateStore();

        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        var file = Assert.Single(Directory.GetFiles(CredentialsDirectory));
        var expectedName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Share))) + ".bin";
        Assert.Equal(expectedName, Path.GetFileName(file));
        Assert.Equal(store.PathFor(Share), file);

        var content = await File.ReadAllBytesAsync(file, Ct);
        Assert.True(content.AsSpan().StartsWith("BBCRED\u0001"u8));
        Assert.DoesNotContain("ana", Encoding.UTF8.GetString(content), StringComparison.Ordinal); // ni el usuario en claro
        SecretSearch.AssertNotInFolder(Password, _temp.Path);

        // Es un blob de DPAPI que solo se descifra con la entropía propia de Backbone y de ese recurso.
        var blob = content.AsSpan(7).ToArray();
        var plaintext = new DpapiMachineProtector().Unprotect(blob, CredentialStore.EntropyFor(Share));
        Assert.Contains(Password, Encoding.UTF8.GetString(plaintext), StringComparison.Ordinal);
        CryptographicOperations.ZeroMemory(plaintext);
        Assert.Throws<CryptographicException>(() => new DpapiMachineProtector().Unprotect(blob, "otra entropía"u8));
        Assert.Throws<CryptographicException>(() => new DpapiMachineProtector().Unprotect(blob, CredentialStore.EntropyFor(@"\\nas\otro")));
        Assert.True(CredentialStore.EntropyFor(Share).AsSpan().StartsWith(CredentialStore.EntropyPrefix));

        // Escrito por TrustedFile: DACL protegida explícita (no hereda nada de la carpeta).
        Assert.True(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void The_protector_uses_the_machine_scope_and_never_shows_UI()
    {
        Assert.Equal(NativeMethods.CryptProtectLocalMachine | NativeMethods.CryptProtectUiForbidden, DpapiMachineProtector.ProtectFlags);
        Assert.Equal(0x4u, NativeMethods.CryptProtectLocalMachine);
        Assert.Equal(0x1u, NativeMethods.CryptProtectUiForbidden);
    }

    [Fact]
    public void The_machine_scope_blob_uses_a_machine_master_key_and_not_one_of_the_user()
    {
        // Un blob de DPAPI lleva el GUID de la clave maestra con que se cifró (DPAPI_BLOB: dwVersion, guidProvider,
        // dwMasterKeyVersion, guidMasterKey…). Con ámbito usuario, esa clave está en el perfil del usuario
        // (%APPDATA%\Microsoft\Protect\<SID>\<GUID>); con ámbito equipo, en la del sistema. Control: el mismo cálculo con
        // ámbito usuario sí encuentra la clave en el perfil, así que la comprobación es significativa.
        var entropy = CredentialStore.EntropyFor(Share);
        var machineBlob = DpapiMachineProtector.Call("x"u8, entropy, protect: true, DpapiMachineProtector.ProtectFlags);
        var userBlob = DpapiMachineProtector.Call("x"u8, entropy, protect: true, NativeMethods.CryptProtectUiForbidden);
        var userKeys = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Protect", WindowsTestEnvironment.CurrentUserSid.Value);
        Assert.SkipUnless(Directory.Exists(userKeys), "The user has no DPAPI master keys folder.");

        Assert.True(File.Exists(Path.Combine(userKeys, MasterKeyGuid(userBlob))), "Control: the user-scope blob should use a user master key.");
        Assert.False(File.Exists(Path.Combine(userKeys, MasterKeyGuid(machineBlob))));
    }

    private static string MasterKeyGuid(byte[] blob) => new Guid(blob.AsSpan(4 + 16 + 4, 16)).ToString("D");

    [Fact]
    public async Task Each_share_has_its_own_file_and_saving_again_replaces_it()
    {
        using var store = CreateStore();

        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = "1" }, Ct);
        await store.SaveAsync(@"\\nas\fotos", new CredentialInput { UserName = "luis", Password = "2" }, Ct);
        _time.Advance(TimeSpan.FromHours(1));
        var replaced = await store.SaveAsync(Share, new CredentialInput { UserName = "eva", Password = "3" }, Ct);

        Assert.Equal(2, Directory.GetFiles(CredentialsDirectory).Length); // sin temporales
        Assert.Equal("eva", replaced.UserName);
        using var credential = await store.GetAsync(Share, Ct);
        Assert.Equal(("eva", "3"), (credential!.UserName, credential.Password.ToString()));
        Assert.Equal("luis", (await store.GetInfoAsync(@"\\nas\fotos", Ct))!.UserName);
    }

    // ---- Criterio 2: usuario y fecha, nunca la contraseña ----

    [Fact]
    public async Task GetInfo_returns_the_user_and_the_update_date_but_never_the_password()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        var info = await store.GetInfoAsync(Share, Ct);

        Assert.Equal(new CredentialInfo { ShareRoot = Share, UserName = "ana", UpdatedUtc = _time.GetUtcNow() }, info);
        Assert.Equal(TimeSpan.Zero, info!.UpdatedUtc.Offset);
        Assert.DoesNotContain(Password, System.Text.Json.JsonSerializer.Serialize(info), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(CredentialInfo).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await store.GetInfoAsync(@"\\nas\otro", Ct));
    }

    [Fact]
    public async Task The_full_credential_keeps_the_password_in_a_NUL_terminated_buffer_zeroed_on_dispose()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        var credential = await store.GetAsync(Share, Ct);

        Assert.NotNull(credential);
        Assert.Equal(Password, credential.Password.ToString());
        Assert.Equal('\0', credential.PasswordWithNullTerminator[^1]);
        Assert.Equal(Password.Length + 1, credential.PasswordWithNullTerminator.Length);
        Assert.DoesNotContain(Password, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ana", credential.ToString(), StringComparison.Ordinal);
        var buffer = (char[])typeof(NasCredential).GetField("_password", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(credential)!;

        credential.Dispose();

        Assert.All(buffer, c => Assert.Equal('\0', c));
        Assert.Throws<ObjectDisposedException>(() => credential.Password.Length);
    }

    [Fact]
    public async Task The_decrypted_plaintext_is_zeroed_after_reading()
    {
        var protector = new RecordingProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        await store.GetInfoAsync(Share, Ct);
        using (await store.GetAsync(Share, Ct))
        {
        }

        Assert.Equal(2, protector.Unprotected.Count);
        Assert.All(protector.Unprotected, plaintext => Assert.All(plaintext, b => Assert.Equal(0, b)));
    }

    // ---- Borrado ----

    [Fact]
    public async Task Delete_removes_the_file_and_is_idempotent()
    {
        using var store = CreateStore();
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        Assert.True(await store.DeleteAsync(Share, Ct));
        Assert.False(await store.DeleteAsync(Share, Ct));

        Assert.Empty(Directory.GetFiles(CredentialsDirectory));
        Assert.Null(await store.GetInfoAsync(Share, Ct));
    }

    // ---- Archivos que no se pueden usar (propietario fiable): como si no hubiera credencial ----

    public static TheoryData<string, string> UnusableFiles => new()
    {
        { "garbage", nameof(CredentialFileProblem.UnknownFormat) },
        { "header-only", nameof(CredentialFileProblem.UnknownFormat) },
        { "other-version", nameof(CredentialFileProblem.UnknownFormat) },
        { "altered-blob", nameof(CredentialFileProblem.NotDecryptable) },
        { "other-share", nameof(CredentialFileProblem.NotDecryptable) },
        { "too-large", nameof(CredentialFileProblem.TooLarge) },
    };

    [Theory]
    [MemberData(nameof(UnusableFiles))]
    public async Task An_unusable_file_counts_as_no_credential_is_logged_and_is_replaced_on_the_next_save(string kind, string problem)
    {
        using var store = CreateStore();
        var path = store.PathFor(Share);
        await PlantAsync(store, path, kind);

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Null(await store.GetAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(problem, StringComparison.Ordinal));

        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);
        Assert.Equal("ana", (await store.GetInfoAsync(Share, Ct))!.UserName);
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    [Fact]
    public async Task A_file_DPAPI_cannot_decrypt_counts_as_no_credential()
    {
        // P. ej. carpeta de datos copiada de otro equipo: DPAPI con ámbito equipo no la descifra.
        var protector = new RecordingProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);
        protector.FailUnprotect = true;

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.NotDecryptable), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Decrypted_content_with_invalid_structure_counts_as_no_credential()
    {
        var protector = new RecordingProtector();
        using var store = CreateStore(protector);
        await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);
        protector.TamperPlaintext = p => p[..^1]; // falta un byte de la contraseña

        Assert.Null(await store.GetInfoAsync(Share, Ct));
        Assert.Contains(_logs.Entries, e => e.Message.Contains(nameof(CredentialFileProblem.InvalidContent), StringComparison.Ordinal));
    }

    // ---- Archivos rechazados por TrustedFile: no se leen, ni se sustituyen, ni se borran ----

    [Fact]
    public async Task A_hard_linked_credential_file_is_rejected_and_never_read_replaced_or_deleted()
    {
        using var store = CreateStore();
        var path = store.PathFor(Share);
        var other = _temp.Combine("otro.bin");
        await File.WriteAllTextAsync(other, "contenido ajeno", Ct);
        TrustedFileTests.RunCmd("mklink", "/H", path, other);

        Assert.Equal(UntrustedFileReason.HardLinked, (await Assert.ThrowsAsync<UntrustedFileException>(() => store.GetInfoAsync(Share, Ct))).Reason);
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.GetAsync(Share, Ct));
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct));
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.DeleteAsync(Share, Ct));

        Assert.True(File.Exists(path));
        Assert.Equal("contenido ajeno", await File.ReadAllTextAsync(other, Ct));
        Assert.Single(Directory.GetFiles(CredentialsDirectory)); // ni temporales
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(nameof(UntrustedFileReason.HardLinked), StringComparison.Ordinal));
        SecretSearch.AssertNotInLogs(Password, _logs);
    }

    [Fact]
    public async Task A_junction_in_place_of_the_credential_file_is_rejected_and_not_followed()
    {
        using var store = CreateStore();
        var path = store.PathFor(Share);
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        TrustedFileTests.RunCmd("mklink", "/J", path, target);

        await Assert.ThrowsAsync<UntrustedFileException>(() => store.GetInfoAsync(Share, Ct));
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct));
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.DeleteAsync(Share, Ct));

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task The_store_does_not_create_the_credentials_folder()
    {
        Directory.Delete(CredentialsDirectory);
        using var store = CreateStore();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct));

        Assert.False(Directory.Exists(CredentialsDirectory));
    }

    // ---- Entradas ----

    [Theory]
    [InlineData(@"\\NAS\Backups")]
    [InlineData(@"\\nas\backups\pc")]
    [InlineData(@"\\nas\backups\")]
    [InlineData("nas")]
    public async Task Only_normalized_share_roots_are_accepted(string shareRoot)
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetInfoAsync(shareRoot, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(shareRoot, new CredentialInput { UserName = "ana", Password = "x" }, Ct));
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("  ", "x")]
    [InlineData("an\ta", "x")]
    [InlineData("ana", "con\0nul")]
    public async Task Credentials_that_cannot_be_stored_intact_are_refused_without_echoing_them(string userName, string password)
    {
        using var store = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Share, new CredentialInput { UserName = userName, Password = password }, Ct));

        Assert.DoesNotContain(password, ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(CredentialsDirectory));
    }

    [Fact]
    public async Task A_password_with_a_lone_surrogate_is_refused_without_echoing_it()
    {
        using var store = CreateStore();
        var password = "suelta" + (char)0xD800;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = password }, Ct));

        Assert.DoesNotContain("suelta", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(CredentialsDirectory));
    }

    [Fact]
    public async Task Passwords_with_any_valid_Unicode_round_trip()
    {
        using var store = CreateStore();
        var password = "ñ€😀\u200Bx " + new string('a', 240);

        await store.SaveAsync(Share, new CredentialInput { UserName = @"PC\ána", Password = password }, Ct);

        using var credential = await store.GetAsync(Share, Ct);
        Assert.Equal(password, credential!.Password.ToString());
        Assert.Equal(@"PC\ána", credential.UserName);
    }

    private CredentialStore CreateStore(IDataProtector? protector = null) =>
        new(CredentialsDirectory, TestFileTrust.CurrentUser, protector ?? new DpapiMachineProtector(), _time, _loggerFactory.CreateLogger<CredentialStore>());

    private static async Task PlantAsync(CredentialStore store, string path, string kind)
    {
        byte[] content;
        switch (kind)
        {
            case "garbage":
                content = Encoding.UTF8.GetBytes("no es un archivo de credenciales");
                break;
            case "header-only":
                content = "BBCRED\u0001"u8.ToArray();
                break;
            case "too-large":
                content = new byte[CredentialStore.MaxFileBytes + 1];
                break;
            case "other-share":
                // El archivo válido de otro recurso con el nombre de este: la entropía (que incluye el recurso) no coincide.
                await store.SaveAsync(@"\\nas\otro", new CredentialInput { UserName = "luis", Password = Password }, Ct);
                File.Move(store.PathFor(@"\\nas\otro"), path);
                return;
            default:
                await store.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = Password }, Ct);
                content = await File.ReadAllBytesAsync(path, Ct);
                if (kind == "other-version")
                {
                    content[6] = 2;
                }
                else
                {
                    content[^1] ^= 0xFF;
                }

                File.Delete(path);
                break;
        }

        await File.WriteAllBytesAsync(path, content, Ct);
    }

    /// <summary>Doble del protector: cifrado reversible trivial que exige la misma entropía y guarda los textos en claro devueltos.</summary>
    private sealed class RecordingProtector : IDataProtector
    {
        public List<byte[]> Unprotected { get; } = [];

        public bool FailUnprotect { get; set; }

        public Func<byte[], byte[]>? TamperPlaintext { get; set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var hash = SHA256.HashData(entropy);
            var result = new byte[hash.Length + plaintext.Length];
            hash.CopyTo(result, 0);
            for (var i = 0; i < plaintext.Length; i++)
            {
                result[hash.Length + i] = (byte)(plaintext[i] ^ 0x5A);
            }

            return result;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
        {
            if (FailUnprotect || !ciphertext[..32].SequenceEqual(SHA256.HashData(entropy)))
            {
                throw new CryptographicException("Fake failure.");
            }

            var plaintext = new byte[ciphertext.Length - 32];
            for (var i = 0; i < plaintext.Length; i++)
            {
                plaintext[i] = (byte)(ciphertext[32 + i] ^ 0x5A);
            }

            if (TamperPlaintext is { } tamper)
            {
                plaintext = tamper(plaintext);
            }

            Unprotected.Add(plaintext);
            return plaintext;
        }
    }
}
