using System.Security.Principal;
using System.Text.Json;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Persistence;

/// <summary>§8.2 <c>SettingsStore</c>: <c>authorizedUsers</c> en <c>settings.json</c> con escritura atómica.</summary>
[Trait("Story", "HT-03")]
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public SettingsStoreTests()
    {
        _path = _temp.Combine("config", "settings.json");
        Directory.CreateDirectory(_temp.Combine("config")); // la crea el servicio al arrancar (§8.3)
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Without_a_file_the_list_is_empty()
    {
        using var store = await LoadAsync();

        Assert.Empty(store.Sids);
    }

    [Fact]
    public async Task The_installer_format_is_read()
    {
        // Lo que escribe el instalador (HU-01): un objeto con authorizedUsers, en camelCase.
        await WriteAsync($$"""{ "authorizedUsers": ["{{TestSids.Ana}}"] }""");

        using var store = await LoadAsync();

        Assert.Equal([TestSids.Ana], store.Sids.ToArray());
        Assert.True(store.Contains(new SecurityIdentifier(TestSids.Ana)));
    }

    [Fact]
    public async Task Entries_that_are_not_user_account_SIDs_are_ignored()
    {
        await WriteAsync($$"""
            { "authorizedUsers": [
                "{{TestSids.Ana}}", "{{TestSids.Everyone}}", "{{TestSids.Users}}", "{{TestSids.Administrators}}",
                "{{TestSids.AuthenticatedUsers}}", "{{TestSids.LocalSystem}}", "{{TestSids.ServiceSid}}",
                "no es un SID", null, "{{TestSids.Ana}}", "{{TestSids.EntraUser}}" ] }
            """);

        using var store = await LoadAsync();

        Assert.Equal([TestSids.Ana, TestSids.EntraUser], store.Sids.ToArray());
    }

    [Fact]
    public async Task Account_SIDs_that_the_system_resolves_as_groups_are_ignored_and_unresolved_ones_are_kept()
    {
        // Un grupo local o de dominio tiene el mismo prefijo S-1-5-21-…: solo el directorio de cuentas lo distingue.
        const string domainGroup = "S-1-5-21-1111111111-2222222222-3333333333-513";
        const string unresolved = "S-1-5-21-1111111111-2222222222-3333333333-4321";
        var accounts = new FakeAccountDirectory()
            .AddUser(TestSids.Ana, @"PC-SALON\ana", administrator: true)
            .AddGroup(domainGroup, @"DOMINIO\Usuarios del dominio");
        await WriteAsync($$"""{ "authorizedUsers": ["{{TestSids.Ana}}", "{{domainGroup}}", "{{unresolved}}"] }""");

        using var store = new SettingsStore(_path, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance, accounts);
        await store.LoadAsync(Ct);

        Assert.Equal([TestSids.Ana, unresolved], store.Sids.ToArray());
    }

    [Fact]
    public async Task If_the_account_lookup_fails_the_entries_are_kept()
    {
        var accounts = new FakeAccountDirectory();
        accounts.FailLookups(FakeAccountDirectory.QueryFailed());
        await WriteAsync($$"""{ "authorizedUsers": ["{{TestSids.Ana}}"] }""");

        using var store = new SettingsStore(_path, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance, accounts);
        await store.LoadAsync(Ct);

        Assert.Equal([TestSids.Ana], store.Sids.ToArray());
    }

    [Theory]
    [InlineData("{ esto no es json")]
    [InlineData("[]")]
    [InlineData("""{ "authorizedUsers": "S-1-5-21-1-2-3-1001" }""")]
    public async Task A_corrupt_file_fails_closed_and_is_kept_as_bad_before_rewriting(string content)
    {
        await WriteAsync(content);

        using var store = await LoadAsync();
        Assert.Empty(store.Sids);

        await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct);
        Assert.Equal(content, await File.ReadAllTextAsync(BadCopies.Single(_path), Ct));
        Assert.Equal([TestSids.Ana], (await LoadAsync()).Sids.ToArray());
    }

    [Fact]
    public async Task An_oversized_file_is_not_read()
    {
        await WriteAsync("{\"authorizedUsers\":[], \"x\":\"" + new string('a', SettingsStore.MaxFileBytes) + "\"}");

        using var store = await LoadAsync();

        Assert.Empty(store.Sids);
    }

    [Fact]
    public async Task Add_and_remove_persist_and_keep_other_properties()
    {
        await WriteAsync($$"""{ "authorizedUsers": [], "missedRunGracePeriod": "00:05:00", "futuro": { "a": 1 } }""");
        using var store = await LoadAsync();

        Assert.True(await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct));
        Assert.True(await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Luis), Ct));
        Assert.True(await store.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_path, Ct));
        Assert.Equal([TestSids.Luis], json.RootElement.GetProperty("authorizedUsers").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("00:05:00", json.RootElement.GetProperty("missedRunGracePeriod").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("futuro").GetProperty("a").GetInt32());
        Assert.Equal([TestSids.Luis], (await LoadAsync()).Sids.ToArray());
    }

    [Fact]
    public async Task Changes_are_idempotent_and_only_real_changes_raise_Changed()
    {
        using var store = await LoadAsync();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.True(await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct));
        Assert.False(await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct));
        Assert.False(await store.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Luis), Ct));
        Assert.True(await store.RemoveAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct));

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Writing_leaves_no_temporary_files()
    {
        using var store = await LoadAsync();

        for (var i = 0; i < 5; i++)
        {
            await store.AddAuthorizedUserAsync(new SecurityIdentifier($"S-1-5-21-1-2-3-{1000 + i}"), Ct);
        }

        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(_path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Concurrent_changes_are_serialized_and_none_is_lost()
    {
        using var store = await LoadAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.AddAuthorizedUserAsync(new SecurityIdentifier($"S-1-5-21-1-2-3-{2000 + i}"), Ct)));

        Assert.Equal(20, (await LoadAsync()).Sids.Length);
    }

    [Fact]
    public async Task A_junction_in_place_of_the_file_is_rejected_on_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        TrustedFileTests.RunCmd("mklink", "/J", _path, Directory.CreateDirectory(_temp.Combine("destino")).FullName);

        using var store = new SettingsStore(_path, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);
        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => store.LoadAsync(Ct));

        Assert.Equal(UntrustedFileReason.ReparsePoint, ex.Reason);
    }

    [Fact]
    public async Task A_junction_in_place_of_the_config_folder_is_rejected_and_nothing_is_written_through_it()
    {
        // M-a: config\ creada de antemano como junction. Ni se lee ni se escribe en su destino.
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        File.WriteAllText(Path.Combine(target, "settings.json"), $$"""{ "authorizedUsers": ["{{TestSids.Ana}}"] }""");
        var config = _temp.Combine("config-enlace");
        TrustedFileTests.RunCmd("mklink", "/J", config, target);
        using var store = new SettingsStore(Path.Combine(config, "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        Assert.Equal(UntrustedFileReason.ReparsePoint, (await Assert.ThrowsAsync<UntrustedFileException>(() => store.LoadAsync(Ct))).Reason);
        await Assert.ThrowsAsync<UntrustedFileException>(() => store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Luis), Ct));

        Assert.Empty(store.Sids);
        Assert.Equal(["settings.json"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
        Assert.DoesNotContain(TestSids.Luis, File.ReadAllText(Path.Combine(target, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_replaced_after_start_by_an_untrusted_one_is_not_overwritten_and_the_list_does_not_change()
    {
        // Decisión de HT-03: un rechazo después del arranque hace fallar la escritura (la operación IPC devuelve error), la
        // lista en memoria no cambia y el servicio sigue; el siguiente arranque lo rechaza (1002).
        using var store = await LoadAsync();
        await store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct);
        var other = _temp.Combine("otro.json");
        await File.WriteAllTextAsync(other, "original", Ct);
        File.Delete(_path);
        TrustedFileTests.RunCmd("mklink", "/H", _path, other);

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => store.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Luis), Ct));

        Assert.Equal(UntrustedFileReason.HardLinked, ex.Reason);
        Assert.Equal([TestSids.Ana], store.Sids.ToArray());
        Assert.Equal("original", await File.ReadAllTextAsync(other, Ct));
    }

    private async Task WriteAsync(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, content, Ct);
    }

    private async Task<SettingsStore> LoadAsync()
    {
        var store = new SettingsStore(_path, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);
        await store.LoadAsync(Ct);
        return store;
    }
}
