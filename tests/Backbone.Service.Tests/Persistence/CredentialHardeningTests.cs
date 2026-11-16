using System.Runtime.InteropServices;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// HU-04, correcciones de la revisión de seguridad: raíz del recurso en UTF-8 estricto y sin suplentes sueltas (BAJA-1),
/// <see cref="NasCredential.ToString"/> saneado (BAJA-2), el proveedor de credenciales rechaza el propio equipo como IPC
/// (BAJA-3) y la salida de DPAPI se libera siempre (INFORMATIVA-2).
/// </summary>
[Trait("Story", "HU-04")]
public sealed class CredentialHardeningTests : IDisposable
{
    private const string Password = "Contraseña-endurecida-7c41";

    private readonly TempDirectory _temp = new();

    public CredentialHardeningTests() => Directory.CreateDirectory(_temp.Combine("credentials"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // ---- BAJA-1: UTF-8 estricto y suplentes sueltas ----

    /// <remarks>
    /// Las suplentes sueltas se pasan como códigos: en un atributo (<c>InlineData</c>) el compilador guarda la cadena en
    /// UTF-8 y llegarían como U+FFFD.
    /// </remarks>
    [Theory]
    [InlineData(@"\\nas\a", 0xD800, -1)]
    [InlineData(@"\\nas\a", 0xDC00, -1)]
    [InlineData(@"\\nas\a", 0xDC00, 0xD800)]
    public void A_share_root_with_a_lone_surrogate_has_no_file_name_or_entropy(string prefix, int first, int second)
    {
        var root = WithCodes(prefix, first, second);
        Assert.Contains(root, char.IsSurrogate);

        // Antes, con Encoding.UTF8, \\nas\a<U+D800> y \\nas\a<U+FFFD> compartían archivo y entropía.
        Assert.Throws<EncoderFallbackException>(() => CredentialStore.FileNameFor(root));
        Assert.Throws<EncoderFallbackException>(() => CredentialStore.EntropyFor(root));
    }

    [Theory]
    [InlineData(@"\\nas\a", 0xD800)]
    [InlineData(@"\\nas\a", 0xDC00)]
    [InlineData(@"\\nas\backups\pc", 0xD800)]
    [InlineData(@"\\nas\backups\pc", 0x0000)]
    public void Normalization_rejects_lone_surrogates_and_NUL_anywhere_in_the_destination(string prefix, int code)
    {
        var destination = WithCodes(prefix, code, -1);
        Assert.False(CredentialStore.TryNormalizeShareRoot(destination, out var root, out var issue));
        Assert.Equal(string.Empty, root);
        Assert.NotEqual(ValidationIssueCode.Other, issue);
    }

    [Fact]
    public void Normalization_accepts_a_valid_surrogate_pair_and_U_FFFD_with_distinct_files()
    {
        Assert.True(CredentialStore.TryNormalizeShareRoot(@"\\NAS\Fotos😀", out var emoji, out _));
        Assert.True(CredentialStore.TryNormalizeShareRoot(@"\\nas\a" + "\uFFFD", out var replacement, out _));

        Assert.Equal(@"\\nas\fotos😀", emoji);
        Assert.NotEqual(CredentialStore.FileNameFor(emoji), CredentialStore.FileNameFor(replacement));
    }

    [Fact]
    public void The_store_refuses_a_share_root_with_a_lone_surrogate_as_a_programming_error()
    {
        using var store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.PathFor(@"\\nas\a" + "\uD800"));
    }

    [Fact]
    public async Task IPC_rejects_a_share_with_a_lone_surrogate_as_a_validation_error_without_writing_anything()
    {
        await using var server = new IpcTestHarness();
        var share = @"\\nas\a" + "\uD800";

        foreach (var call in new Func<Task>[]
        {
            () => server.Operations.SaveCredentialAsync(share, new CredentialInput { UserName = "ana", Password = Password }, Ct),
            () => server.Operations.GetCredentialInfoAsync(share, Ct),
            () => server.Operations.DeleteCredentialAsync(share, Ct),
        })
        {
            var ex = await Assert.ThrowsAsync<BackboneException>(call);
            Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
            Assert.Equal(ValidationIssueCode.DestinationNotAllowed, Assert.Single(ex.Validation!.Errors).Code);
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(server.DataRoot, "credentials")));
    }

    [Fact]
    public async Task A_share_with_a_valid_surrogate_pair_round_trips()
    {
        using var store = CreateStore();
        Assert.True(CredentialStore.TryNormalizeShareRoot(@"\\nas\Fotos😀", out var root, out _));

        await store.SaveAsync(root, new CredentialInput { UserName = "ana", Password = Password }, Ct);

        using var credential = await store.GetAsync(root, Ct);
        Assert.Equal(("ana", Password), (credential!.UserName, credential.Password.ToString()));
    }

    // ---- BAJA-2: ToString saneado ----

    [Fact]
    public void ToString_sanitizes_the_share_root_and_never_includes_the_user_or_the_password()
    {
        var root = "\\\\nas\\a\nFALSA LÍNEA\u202Ex";
        using var credential = new NasCredential(root, "ana", DateTimeOffset.UnixEpoch, Pinned(Password), Password.Length);

        var text = credential.ToString();

        Assert.Contains(UntrustedText.Describe(root), text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
    }

    // ---- BAJA-3: el proveedor rechaza el propio equipo, como IPC ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_provider_rejects_the_own_machine_as_a_destination_without_reading_any_credential(bool byName)
    {
        var store = new RecordingStore();
        var provider = new ShareCredentialProvider(store, NullLogger<ShareCredentialProvider>.Instance);
        var server = byName ? Environment.MachineName : LocalMachine.Addresses().FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString();
        Assert.SkipWhen(server is null, "El equipo no tiene ninguna dirección IPv4 que no sea de bucle invertido.");

        var ex = await Assert.ThrowsAsync<BackboneException>(() => provider.GetRequiredAsync($@"\\{server}\backups\pc", Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(ValidationIssueCode.DestinationNotAllowed, Assert.Single(ex.Validation!.Errors).Code);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task The_provider_rejects_a_destination_with_a_lone_surrogate()
    {
        var store = new RecordingStore();
        var provider = new ShareCredentialProvider(store, NullLogger<ShareCredentialProvider>.Instance);

        var ex = await Assert.ThrowsAsync<BackboneException>(() => provider.GetRequiredAsync(@"\\nas\a" + "\uDC00", Ct));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Equal(0, store.Reads);
    }

    // ---- INFORMATIVA-2: la salida de DPAPI se libera siempre ----

    [Fact]
    public unsafe void The_DPAPI_output_is_freed_even_if_zeroing_fails()
    {
        Released.Reset();

        Assert.Throws<InvalidOperationException>(() => DpapiMachineProtector.Release(0x1234, 16, &ThrowingZero, &RecordingFree));

        Assert.Equal([(nint)0x1234], Released.Freed);
    }

    [Fact]
    public unsafe void A_size_that_does_not_fit_in_an_int_is_passed_whole_and_the_output_is_freed()
    {
        Released.Reset();

        DpapiMachineProtector.Release(0x1234, uint.MaxValue, &RecordingZero, &RecordingFree);

        Assert.Equal([((nint)0x1234, uint.MaxValue)], Released.Zeroed);
        Assert.Equal([(nint)0x1234], Released.Freed);
    }

    [Fact]
    public unsafe void A_null_output_is_neither_zeroed_nor_freed()
    {
        Released.Reset();

        DpapiMachineProtector.Release(0, 16, &RecordingZero, &RecordingFree);

        Assert.Empty(Released.Zeroed);
        Assert.Empty(Released.Freed);
    }

    [Fact]
    public void ZeroUnmanaged_clears_the_whole_buffer()
    {
        var buffer = Marshal.AllocHGlobal(37);
        try
        {
            for (var i = 0; i < 37; i++)
            {
                Marshal.WriteByte(buffer, i, 0xAB);
            }

            DpapiMachineProtector.ZeroUnmanaged(buffer, 37);

            var copy = new byte[37];
            Marshal.Copy(buffer, copy, 0, copy.Length);
            Assert.All(copy, b => Assert.Equal(0, b));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string WithCodes(string prefix, int first, int second) =>
        prefix + (char)first + (second < 0 ? string.Empty : ((char)second).ToString());

    private static void ThrowingZero(nint data, uint size) => throw new InvalidOperationException("fallo simulado");

    private static void RecordingZero(nint data, uint size) => Released.Zeroed.Add((data, size));

    private static void RecordingFree(nint data) => Released.Freed.Add(data);

    private static char[] Pinned(string value)
    {
        var buffer = GC.AllocateArray<char>(value.Length + 1, pinned: true);
        value.CopyTo(buffer);
        return buffer;
    }

    private CredentialStore CreateStore() =>
        new(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), TimeProvider.System, NullLogger<CredentialStore>.Instance);

    /// <summary>Registro de las llamadas de <see cref="DpapiMachineProtector.Release"/> (las pruebas de la clase no son paralelas).</summary>
    private static class Released
    {
        public static List<(nint Data, uint Size)> Zeroed { get; } = [];

        public static List<nint> Freed { get; } = [];

        public static void Reset()
        {
            Zeroed.Clear();
            Freed.Clear();
        }
    }

    private sealed class RecordingStore : ICredentialStore
    {
        public int Reads { get; private set; }

        public Task<CredentialInfo> SaveAsync(string shareRoot, CredentialInput credential, CancellationToken ct) => throw new NotSupportedException();

        public Task<CredentialInfo?> GetInfoAsync(string shareRoot, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<CredentialInfo?>(null);
        }

        public Task<NasCredential?> GetAsync(string shareRoot, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<NasCredential?>(null);
        }

        public Task<bool> DeleteAsync(string shareRoot, CancellationToken ct) => throw new NotSupportedException();
    }
}
