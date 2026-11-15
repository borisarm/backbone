using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Service;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Persistence;

/// <summary>
/// §8.2 / ADR-14 con la política de producción (<see cref="FileTrustPolicy.Default"/>): solo elevado se puede asignar
/// Administradores como propietario. Requiere PowerShell elevado.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HT-03")]
public sealed class TrustedFileElevatedTests : IDisposable
{
    private const string NeedsElevation = "Requires an elevated process (Administrators as owner).";

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    private readonly TempDirectory _temp = new();
    private readonly TrustedFile _files = new(FileTrustPolicy.Default);

    public static bool IsElevated => WindowsTestEnvironment.IsElevated;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task A_file_owned_by_Administrators_is_accepted()
    {
        var path = CreateFileOwnedBy(Administrators, "{\"a\":1}");

        var bytes = await _files.ReadAllBytesAsync(path, 1024, Ct);

        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString(bytes!));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task A_file_owned_by_the_user_is_rejected()
    {
        var path = CreateFileOwnedBy(WindowsTestEnvironment.CurrentUserSid, "{}");

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _files.ReadAllBytesAsync(path, 1024, Ct));

        Assert.Equal(UntrustedFileReason.UntrustedOwner, ex.Reason);
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_atomic_write_creates_an_Administrators_owned_file_with_the_protected_RNF07_DACL_and_reads_it_back()
    {
        Directory.CreateDirectory(_temp.Combine("config")); // los almacenes no crean carpetas (§8.2)
        var path = _temp.Combine("config", "settings.json");

        await _files.WriteAllBytesAsync(path, "{\"v\":1}"u8.ToArray(), Ct);
        await _files.WriteAllBytesAsync(path, "{\"v\":2}"u8.ToArray(), Ct);

        var security = new FileInfo(path).GetAccessControl();
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();
        Assert.True(sids.SetEquals([LocalSystem, Administrators]));
        Assert.Equal("{\"v\":2}", System.Text.Encoding.UTF8.GetString((await _files.ReadAllBytesAsync(path, 1024, Ct))!));
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_atomic_write_does_not_replace_a_user_owned_destination()
    {
        var path = CreateFileOwnedBy(WindowsTestEnvironment.CurrentUserSid, "plantado");

        var ex = await Assert.ThrowsAsync<UntrustedFileException>(() => _files.WriteAllBytesAsync(path, "{}"u8.ToArray(), Ct));

        Assert.Equal(UntrustedFileReason.UntrustedOwner, ex.Reason);
        Assert.Equal("plantado", await File.ReadAllTextAsync(path, Ct));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_service_does_not_start_with_a_user_owned_settings_file_and_starts_with_an_Administrators_one()
    {
        var root = _temp.Combine("Backbone");
        var guard = new DataDirectoryGuard();
        Assert.Equal(DataDirectoryCheckResult.Corrected, guard.EnsureSecure(root).Result);
        var paths = new ServicePaths(root);
        Assert.True(Directory.Exists(paths.ConfigDirectory)); // la crea la comprobación de la carpeta (§8.3)

        // Archivo plantado después de la comprobación de la carpeta (ventana de R9): propietario usuario.
        File.WriteAllText(paths.SettingsFile, "{ \"authorizedUsers\": [] }");
        SetOwner(paths.SettingsFile, WindowsTestEnvironment.CurrentUserSid);
        var eventLog = new FakeEventLogWriter();
        using (var settings = new SettingsStore(paths.SettingsFile, _files, NullLogger<SettingsStore>.Instance))
        {
            var startup = new StartupService(paths, new SecureGuard(), settings, eventLog, new ServiceRuntimeInfo(TimeProvider.System), NullLogger<StartupService>.Instance);
            await Assert.ThrowsAsync<ServiceStartupException>(() => startup.StartingAsync(Ct));
        }

        Assert.Equal(BackboneEventIds.ServiceStartFailed, Assert.Single(eventLog.Entries).EventId);

        SetOwner(paths.SettingsFile, Administrators);
        using (var settings = new SettingsStore(paths.SettingsFile, _files, NullLogger<SettingsStore>.Instance))
        {
            var startup = new StartupService(paths, new SecureGuard(), settings, new FakeEventLogWriter(), new ServiceRuntimeInfo(TimeProvider.System), NullLogger<StartupService>.Instance);
            await startup.StartingAsync(Ct);
        }
    }

    private string CreateFileOwnedBy(SecurityIdentifier owner, string content)
    {
        var path = _temp.Combine(Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        SetOwner(path, owner);
        return path;
    }

    private static void SetOwner(string path, SecurityIdentifier owner)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetOwner(owner);
        info.SetAccessControl(security);
        Assert.Equal(owner, new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    private sealed class SecureGuard : IDataDirectoryGuard
    {
        public DataDirectoryCheck EnsureSecure(string root) => DataDirectoryCheck.Secure;
    }
}
