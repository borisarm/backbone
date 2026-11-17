using System.Diagnostics;
using Backbone.App.Ipc;
using Backbone.Contracts;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Win32.SafeHandles;

namespace Backbone.IntegrationTests.Ipc;

/// <summary>
/// HT-03 criterios 5 y 6: el cliente real de la app (<see cref="BackboneClient"/> + <see cref="NamedPipeBackboneConnector"/>) contra el
/// servidor real (<c>IpcServer</c>) sobre un named pipe de nombre aleatorio.
/// </summary>
/// <remarks>
/// No necesitan elevación (el servidor corre en el proceso de pruebas con la identidad real del cliente), así que no llevan
/// <c>Category=Integration</c> y corren en la batería normal. La comprobación del proceso servidor se sustituye por una que
/// espera el PID de este proceso: el servidor de pruebas no es el servicio <c>BackboneService</c> del SCM.
/// </remarks>
[Trait("Story", "HT-03")]
public sealed class AppClientAgainstRealServerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackboneClient NewClient(string pipeName, IServerVerifier? verifier = null)
    {
        var connector = new NamedPipeBackboneConnector(
            verifier ?? new ExpectedProcessVerifier(Environment.ProcessId),
            TimeProvider.System,
            new NamedPipeConnectorOptions { PipeName = pipeName, ConnectTimeout = TimeSpan.FromMilliseconds(300) });
        return new BackboneClient(connector, TimeProvider.System);
    }

    [Fact]
    public async Task The_client_connects_and_gets_service_and_contract_version()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = NewClient(server.PipeName);

        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);
        var info = await client.GetServiceInfoAsync(Ct);

        Assert.Equal(ContractVersion.Value, info.ContractVersion);
        Assert.Equal(info.ContractVersion, client.ServiceInfo?.ContractVersion);
        Assert.Equal(PipeServerHarness.CurrentUser.Value, info.Caller.UserSid);
    }

    [Fact]
    public async Task StatusChanged_and_RunProgress_reach_the_client_through_its_events()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = NewClient(server.PipeName);
        var status = new TaskCompletionSource<StatusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<RunProgressEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, e) => status.TrySetResult(e);
        client.RunProgress += (_, e) => progress.TrySetResult(e);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        server.Events.PublishStatusChanged(new StatusChangedEvent { Status = new ServiceStatus { Health = HealthState.Warning } });
        server.Events.PublishRunProgress(new RunProgressEvent { RunId = 7, FilesProcessed = 11 });

        Assert.Equal(HealthState.Warning, (await status.Task.WaitAsync(Wait, Ct)).Status.Health);
        Assert.Equal(11, (await progress.Task.WaitAsync(Wait, Ct)).FilesProcessed);
    }

    [Fact]
    public async Task A_business_error_from_the_server_reaches_the_caller_with_its_code()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = NewClient(server.PipeName);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        // Hoy el servidor responde NotSupported a las operaciones aún sin implementar (§7.4).
        var ex = await Assert.ThrowsAsync<StreamJsonRpc.RemoteInvocationException>(() => client.GetSettingsAsync(Ct));

        Assert.True(ConnectionFailures.TryGetErrorCode(ex, out var code, out var data));
        Assert.Equal(BackboneErrorCode.NotSupported, code);
        Assert.Equal(BackboneErrorCode.NotSupported, code);
        Assert.Equal(BackboneErrorCode.NotSupported, data?.Code);
        // Un error de negocio no tumba la conexión.
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task When_the_service_restarts_the_client_reconnects_by_itself()
    {
        var pipeName = PipeServerHarness.NewPipeName();
        var first = await PipeServerHarness.StartAsync(pipeName: pipeName);
        await using var client = NewClient(pipeName);
        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        await first.DisposeAsync();
        await Eventually(() => client.State != ConnectionState.Connected);
        Assert.Equal(ConnectionState.Reconnecting, client.State);

        await using var second = await PipeServerHarness.StartAsync(pipeName: pipeName);
        await Eventually(() => client.State == ConnectionState.Connected);
        Assert.Equal(ContractVersion.Value, (await client.GetServiceInfoAsync(Ct)).ContractVersion);
    }

    [Fact]
    public async Task Without_a_service_the_client_reports_ServiceStopped_and_connects_when_it_appears()
    {
        var pipeName = PipeServerHarness.NewPipeName();
        await using var client = NewClient(pipeName);
        client.Start();
        await Eventually(() => client.State == ConnectionState.ServiceStopped);

        await using var server = await PipeServerHarness.StartAsync(pipeName: pipeName);
        client.RetryNow();

        await Eventually(() => client.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task A_user_not_in_the_authorized_list_is_reported_as_Unauthorized()
    {
        Assert.SkipWhen(WindowsTestEnvironment.IsElevated, "Elevado, el usuario está autorizado por ser administrador.");
        await using var server = await PipeServerHarness.StartAsync(authorizeCurrentUser: false);
        await using var client = NewClient(server.PipeName);

        client.Start();

        // En este arnés el usuario de pruebas tiene control total en la ACL (AdditionalServerSid), así que no hay acceso denegado
        // al abrir el pipe: el rechazo llega como NotAuthorized del servicio en la primera llamada. El acceso denegado de la ACL
        // (UnauthorizedAccessException) se clasifica en ConnectionFailuresTests y se prueba a mano (docs/PRUEBAS-MANUALES.md).
        await Eventually(() => client.State == ConnectionState.Unauthorized, () => client.State + server.Logs.AllText());
    }

    [Fact]
    public async Task A_server_that_fails_verification_is_closed_before_the_client_sends_anything()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = NewClient(server.PipeName, new ExpectedProcessVerifier(Environment.ProcessId + 1));

        client.Start();
        await Eventually(() => client.State == ConnectionState.ServiceStopped);
        await server.WaitForConnectionCountAsync(0);

        // El servidor nunca llegó a identificar al cliente: no recibió ningún mensaje (§7.1 A-1).
        Assert.DoesNotContain(server.Logs.Entries, e => e.Message.Contains("identificada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dropping_the_client_abruptly_does_not_affect_another_client()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var first = NewClient(server.PipeName);
        await using var second = NewClient(server.PipeName);
        first.Start();
        second.Start();
        await Eventually(() => first.State == ConnectionState.Connected && second.State == ConnectionState.Connected);
        await server.WaitForConnectionCountAsync(2);

        await first.DisposeAsync();

        await server.WaitForConnectionCountAsync(1);
        Assert.Equal(ContractVersion.Value, (await second.GetServiceInfoAsync(Ct)).ContractVersion);
    }

    private static async Task Eventually(Func<bool> condition, Func<string>? describe = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Wait, "The expected condition was not reached in time. " + describe?.Invoke());
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task The_real_pipe_inspector_reads_owner_session_and_pid_from_the_client_handle()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var probe = new ProbeVerifier(pipe => (new NativePipeInspector().GetOwner(pipe), new NativePipeInspector().GetServerSessionId(pipe)));
        await using var client = NewClient(server.PipeName, probe);

        client.Start();
        await Eventually(() => client.State == ConnectionState.Connected);

        var (owner, session) = Assert.IsType<(System.Security.Principal.SecurityIdentifier, uint)>(probe.Result);
        // El arnés crea el pipe con el usuario de pruebas como propietario (o Administradores si el proceso está elevado).
        Assert.True(
            owner.Value == PipeServerHarness.CurrentUser.Value || owner.Value == "S-1-5-32-544",
            "Unexpected pipe owner " + owner.Value);
        Assert.Equal((uint)Process.GetCurrentProcess().SessionId, session);
    }

    [Fact]
    public async Task The_production_verifier_rejects_a_pipe_not_owned_by_SYSTEM_or_Administrators_and_not_in_session_0()
    {
        Assert.SkipWhen(WindowsTestEnvironment.IsElevated, "Elevado, el propietario del pipe es Administradores.");
        Assert.SkipWhen(Process.GetCurrentProcess().SessionId == 0, "El proceso de pruebas corre en la sesión 0.");
        await using var server = await PipeServerHarness.StartAsync();
        var running = new FixedServiceQuery(new ServiceProcessInfo(true, Environment.ProcessId, "LocalSystem"));
        var production = new ServiceProcessVerifier(running, new NativePipeInspector(), "BackboneService");
        var probe = new ProbeVerifier(pipe => { production.Verify(pipe); return null; });
        await using var client = NewClient(server.PipeName, probe);

        client.Start();
        await Eventually(() => probe.Exception is not null);

        Assert.IsType<ServerVerificationException>(probe.Exception);
        Assert.NotEqual(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task The_verifier_with_real_owner_and_pid_accepts_the_harness_when_the_owner_is_allowed_and_session_is_injected()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var running = new FixedServiceQuery(new ServiceProcessInfo(true, Environment.ProcessId, "LocalSystem"));
        var owners = new[]
        {
            PipeServerHarness.CurrentUser,
            new System.Security.Principal.SecurityIdentifier("S-1-5-32-544"),
        };
        var verifier = new ServiceProcessVerifier(running, new Session0Inspector(), "BackboneService", owners);
        await using var client = NewClient(server.PipeName, verifier);

        client.Start();

        await Eventually(() => client.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task The_verifier_with_real_owner_rejects_the_harness_when_the_owner_is_not_allowed()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var running = new FixedServiceQuery(new ServiceProcessInfo(true, Environment.ProcessId, "LocalSystem"));
        var owners = new[] { new System.Security.Principal.SecurityIdentifier("S-1-5-18") };
        var verifier = new ServiceProcessVerifier(running, new Session0Inspector(), "BackboneService", owners);
        var probe = new ProbeVerifier(pipe => { verifier.Verify(pipe); return null; });
        await using var client = NewClient(server.PipeName, probe);

        client.Start();
        await Eventually(() => probe.Exception is not null);

        Assert.IsType<ServerVerificationException>(probe.Exception);
        Assert.NotEqual(ConnectionState.Connected, client.State);
    }

    private sealed class FixedServiceQuery(ServiceProcessInfo info) : IServiceQuery
    {
        public ServiceProcessInfo? Query(string serviceName) => info;
    }

    /// <summary>Lecturas reales de propietario y PID; solo la sesión se inyecta (el proceso de pruebas no está en la sesión 0).</summary>
    private sealed class Session0Inspector : IPipeInspector
    {
        private readonly NativePipeInspector _real = new();

        public int GetServerProcessId(SafePipeHandle pipe) => _real.GetServerProcessId(pipe);

        public uint GetServerSessionId(SafePipeHandle pipe) => 0;

        public System.Security.Principal.SecurityIdentifier GetOwner(SafePipeHandle pipe) => _real.GetOwner(pipe);
    }

    /// <summary>Ejecuta una comprobación sobre el handle real, guarda el resultado o la excepción y deja pasar (o relanza).</summary>
    private sealed class ProbeVerifier(Func<SafePipeHandle, object?> probe) : IServerVerifier
    {
        public object? Result { get; private set; }

        public Exception? Exception { get; private set; }

        public void Verify(SafePipeHandle pipe)
        {
            try
            {
                Result = probe(pipe);
            }
            catch (Exception ex)
            {
                Exception = ex;
                throw;
            }
        }
    }

    /// <summary>Acepta solo si el servidor del pipe es el proceso indicado (el de pruebas, que aloja el servidor real).</summary>
    private sealed class ExpectedProcessVerifier(int expectedProcessId) : IServerVerifier
    {
        public void Verify(SafePipeHandle pipe)
        {
            if (ServiceProcessVerifier.GetServerProcessId(pipe) != expectedProcessId)
            {
                throw new ServerVerificationException("Unexpected server process.");
            }
        }
    }
}
