using System.ComponentModel;
using System.Security.Principal;
using Backbone.App.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Backbone.App.Tests.Ipc;

/// <summary>HT-03 criterio 5 / §7.1 A-1: el cliente verifica el proceso servidor antes de enviar nada.</summary>
[Trait("Story", "HT-03")]
public sealed class ServerVerificationTests
{
    private static readonly ServiceProcessInfo Running = new(true, 1234, "LocalSystem");
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SomeUser = new("S-1-5-21-1-2-3-1001");

    [Fact]
    public void The_service_process_running_as_LocalSystem_is_accepted() =>
        ServiceProcessVerifier.Check(1234, Running);

    [Theory]
    [InlineData("localsystem")]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    public void Spellings_of_LocalSystem_are_accepted(string startName) =>
        ServiceProcessVerifier.Check(1234, Running with { StartName = startName });

    [Fact]
    public void A_server_with_another_process_id_is_rejected() =>
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(4321, Running));

    [Fact]
    public void A_missing_or_stopped_service_is_rejected()
    {
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(1234, null));
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(1234, Running with { IsRunning = false }));
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(0, Running with { ProcessId = 0 }));
    }

    [Theory]
    [InlineData(@"NT AUTHORITY\LocalService")]
    [InlineData(@"PC\usuario")]
    [InlineData("")]
    public void A_service_not_running_as_LocalSystem_is_rejected(string startName) =>
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(1234, Running with { StartName = startName }));

    [Fact]
    public void A_service_that_is_not_win32_own_process_is_rejected()
    {
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(1234, Running with { ServiceType = 0x20 }));
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(1234, Running with { ServiceType = 0x01 }));
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    public void Owners_SYSTEM_and_Administrators_are_accepted(string sid) =>
        ServiceProcessVerifier.Check(new SecurityIdentifier(sid), 0, 1234, 1234, Running);

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-5-19")]
    public void Any_other_pipe_owner_is_rejected(string sid) =>
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(new SecurityIdentifier(sid), 0, 1234, 1234, Running));

    [Fact]
    public void A_server_outside_session_0_is_rejected() =>
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(LocalSystem, 1, 1234, 1234, Running));

    [Fact]
    public void A_pipe_pid_that_changes_during_the_check_is_rejected() =>
        Assert.Throws<ServerVerificationException>(() => ServiceProcessVerifier.Check(LocalSystem, 0, 1234, 99, Running));

    [Fact]
    public void The_verifier_combines_owner_session_and_the_pid_before_and_after_the_scm()
    {
        using var pipe = new SafePipeHandle(0, ownsHandle: false);
        new ServiceProcessVerifier(new FixedQuery(Running), new FakePipe { Owner = Admins, Pids = [1234, 1234] }, "BackboneService").Verify(pipe);

        Assert.Throws<ServerVerificationException>(() => Verify(pipe, new FakePipe { Owner = SomeUser, Pids = [1234, 1234] }));
        Assert.Throws<ServerVerificationException>(() => Verify(pipe, new FakePipe { Owner = LocalSystem, Session = 2, Pids = [1234, 1234] }));
        Assert.Throws<ServerVerificationException>(() => Verify(pipe, new FakePipe { Owner = LocalSystem, Pids = [99, 99] }));
        Assert.Throws<ServerVerificationException>(() => Verify(pipe, new FakePipe { Owner = LocalSystem, Pids = [1234, 99] }));
    }

    [Fact]
    public void The_pid_is_read_before_and_after_the_scm_query()
    {
        using var pipe = new SafePipeHandle(0, ownsHandle: false);
        var events = new List<string>();
        var inspector = new FakePipe { Owner = LocalSystem, Pids = [1234, 1234], Events = events };

        new ServiceProcessVerifier(new FixedQuery(Running, events), inspector, "BackboneService").Verify(pipe);

        Assert.Equal(["pid", "scm", "pid"], events);
    }

    [Fact]
    public void A_win32_failure_reading_the_pipe_is_a_verification_failure()
    {
        using var pipe = new SafePipeHandle(0, ownsHandle: false);

        Assert.Throws<ServerVerificationException>(() => Verify(pipe, new FakePipe { Owner = LocalSystem, Pids = [1234, 1234], Fail = true }));
    }

    [Fact]
    public void The_production_owner_list_is_exactly_SYSTEM_and_Administrators() =>
        Assert.Equal(["S-1-5-18", "S-1-5-32-544"], ServiceProcessVerifier.ProductionOwners.Select(s => s.Value));

    [Fact]
    public void The_real_scm_query_reports_a_missing_service_as_null() =>
        Assert.Null(new ScmServiceQuery().Query("Backbone-No-Such-Service-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void The_real_scm_query_reads_pid_account_and_type_of_a_running_service()
    {
        // EventLog corre siempre en Windows, como NT AUTHORITY\LocalService, y un usuario sin privilegios puede consultarlo.
        var info = new ScmServiceQuery().Query("EventLog");

        Assert.NotNull(info);
        Assert.True(info.IsRunning);
        Assert.True(info.ProcessId > 0);
        Assert.Contains("LocalService", info.StartName, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0u, info.ServiceType);
    }

    private static void Verify(SafePipeHandle pipe, FakePipe inspector) =>
        new ServiceProcessVerifier(new FixedQuery(Running), inspector, "BackboneService").Verify(pipe);

    private sealed class FixedQuery(ServiceProcessInfo? info, List<string>? events = null) : IServiceQuery
    {
        public ServiceProcessInfo? Query(string serviceName)
        {
            events?.Add("scm");
            return info;
        }
    }

    private sealed class FakePipe : IPipeInspector
    {
        private int _next;

        public required SecurityIdentifier Owner { get; init; }

        public uint Session { get; init; }

        public required int[] Pids { get; init; }

        public bool Fail { get; init; }

        public List<string>? Events { get; init; }

        public SecurityIdentifier GetOwner(SafePipeHandle pipe) => Fail ? throw new Win32Exception(5) : Owner;

        public uint GetServerSessionId(SafePipeHandle pipe) => Session;

        public int GetServerProcessId(SafePipeHandle pipe)
        {
            Events?.Add("pid");
            return Pids[_next++];
        }
    }
}
