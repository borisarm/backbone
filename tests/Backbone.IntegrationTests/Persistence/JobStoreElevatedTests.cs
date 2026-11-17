using System.Security.AccessControl;
using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.IntegrationTests.Persistence;

/// <summary>
/// HU-07 criterio 5 y §8.2 con la política de producción (<see cref="FileTrustPolicy.Default"/>): <c>jobs.json</c> se escribe
/// con propietario Administradores y la DACL protegida de RNF-07, y un <c>jobs.json</c> de propietario usuario impide arrancar
/// (1002). Requiere PowerShell elevado.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Story", "HU-07")]
public sealed class JobStoreElevatedTests : IDisposable
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
    public async Task Jobs_json_is_written_with_the_RNF07_descriptor_and_read_back_by_another_instance()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        var path = _temp.Combine("config", "jobs.json");
        var job = JobDefaults.NewJob() with { Id = Guid.NewGuid(), Name = "Docs", Sources = [new JobSource { Alias = "Docs", Path = @"C:\Docs" }], Destination = @"\\nas\b" };
        using (var store = new JobStore(path, _files, NullLogger<JobStore>.Instance))
        {
            await store.LoadAsync(Ct);
            await store.UpdateAsync(list => (list.Add(job), 0), Ct);
            await store.UpdateAsync(list => (list.SetItem(0, job with { Name = "Docs 2" }), 0), Ct);
        }

        var security = new FileInfo(path).GetAccessControl();
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var sids = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).ToHashSet();
        Assert.True(sids.SetEquals([LocalSystem, Administrators]));
        Assert.Equal(["jobs.json"], Directory.GetFiles(_temp.Combine("config")).Select(Path.GetFileName));

        using var reader = new JobStore(path, _files, NullLogger<JobStore>.Instance);
        await reader.LoadAsync(Ct);
        Assert.Equal("Docs 2", Assert.Single(reader.Jobs).Name);
    }

    [Fact(Skip = NeedsElevation, SkipUnless = nameof(IsElevated))]
    public async Task The_service_does_not_start_with_a_user_owned_jobs_file()
    {
        var root = _temp.Combine("Backbone");
        var guard = new DataDirectoryGuard();
        guard.EnsureSecure(root);
        var paths = new ServicePaths(root);
        File.WriteAllText(paths.JobsFile, "{ \"jobs\": [] }");
        var info = new FileInfo(paths.JobsFile);
        var fileSecurity = info.GetAccessControl();
        fileSecurity.SetOwner(WindowsTestEnvironment.CurrentUserSid);
        info.SetAccessControl(fileSecurity);
        var eventLog = new FakeEventLogWriter();

        using var settings = new SettingsStore(paths.SettingsFile, _files, NullLogger<SettingsStore>.Instance);
        using var jobs = new JobStore(paths.JobsFile, _files, NullLogger<JobStore>.Instance);
        var startup = new StartupService(paths, new SecureGuard(), settings, jobs, eventLog, new ServiceRuntimeInfo(TimeProvider.System), NullLogger<StartupService>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => startup.StartingAsync(Ct));

        Assert.IsType<UntrustedFileException>(ex.InnerException);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, Assert.Single(eventLog.Entries).EventId);
    }

    private sealed class SecureGuard : IDataDirectoryGuard
    {
        public DataDirectoryCheck EnsureSecure(string root) => DataDirectoryCheck.Secure;
    }
}
