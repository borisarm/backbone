using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// HT-03 criterio 6 (y 1 a 4 sobre el transporte real): servidor y cliente reales sobre un named pipe de nombre aleatorio,
/// con la ACL de §7.3 y la identidad tomada del token del cliente.
/// </summary>
/// <remarks>
/// Corren en la batería normal: no necesitan elevación (el servidor corre como el usuario de las pruebas y suplanta a un
/// cliente de la misma cuenta). Lo que sí exige otra cuenta real (criterio 2 con un usuario estándar de verdad) es un caso
/// manual; aquí se simula con un token restringido.
/// </remarks>
[Trait("Story", "HT-03")]
public sealed class NamedPipeServerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_authorized_user_gets_service_and_contract_version_over_the_real_pipe()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = await server.ConnectAsync();

        var info = await client.Proxy.GetServiceInfoAsync(Ct);

        Assert.Equal(ContractVersion.Value, info.ContractVersion);
        Assert.False(string.IsNullOrEmpty(info.ServiceVersion));
        Assert.Equal(PipeServerHarness.CurrentUser.Value, info.Caller.UserSid);
        Assert.Equal(@"PRUEBAS\usuario-actual", info.Caller.UserName);
        Assert.Equal(WindowsTestEnvironment.IsElevated, info.Caller.IsElevated);
    }

    [Fact]
    public async Task The_client_can_verify_the_server_process_before_sending_anything()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var pipe = await server.OpenAsync();

        // §7.1 A-1: con los derechos de cliente (sin escribir nada) se obtiene el PID del servidor.
        Assert.Equal(Environment.ProcessId, TestNativeMethods.GetServerProcessId(pipe.SafePipeHandle));
    }

    [Fact]
    public async Task A_second_server_with_the_same_pipe_name_does_not_start_and_is_logged()
    {
        await using var first = await PipeServerHarness.StartAsync();

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => PipeServerHarness.StartAsync(pipeName: first.PipeName));

        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        await using var client = await first.ConnectAsync();
        Assert.Equal(ContractVersion.Value, (await client.Proxy.GetServiceInfoAsync(Ct)).ContractVersion);
    }

    [Fact]
    public async Task A_pipe_squatted_by_another_process_prevents_the_service_from_starting_and_writes_the_event()
    {
        var name = PipeServerHarness.NewPipeName();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var server = await PipeServerHarness.CreateAsync(pipeName: name);

        await Assert.ThrowsAsync<ServiceStartupException>(() => server.Server.StartAsync(Ct));

        var entry = Assert.Single(server.EventLog.Entries);
        Assert.Equal((EventLogLevel.Error, BackboneEventIds.ServiceStartFailed), (entry.Level, entry.EventId));
        Assert.Contains(name, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_without_an_entry_in_the_pipe_acl_is_denied_at_connection()
    {
        await using var server = await PipeServerHarness.StartAsync();
        // Simula a otro usuario: el SID propio y el de Administradores pasan a "solo denegar".
        using var token = TestNativeMethods.CreateTokenWithDenyOnly(
            PipeServerHarness.CurrentUser, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        var ex = Record.Exception(() => WindowsIdentity.RunImpersonated(token, () =>
        {
            using var client = new NamedPipeClientStream(".", server.PipeName, PipeSecurityFactory.ClientRights, PipeOptions.None, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
            client.Connect(5000);
        }));

        Assert.IsType<UnauthorizedAccessException>(ex);
        Assert.Equal(0, server.Manager.Count);
    }

    [Theory]
    [InlineData(TokenImpersonationLevel.Identification)]
    [InlineData(TokenImpersonationLevel.Anonymous)]
    public async Task A_client_that_does_not_allow_impersonation_is_rejected_and_disconnected(TokenImpersonationLevel level)
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var client = await server.ConnectAsync(level);

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));

        await client.Rpc.Completion.WaitAsync(Wait, Ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Assert.True(client.Rpc.IsDisposed || client.Rpc.Completion.IsCompleted);
        await server.WaitForConnectionCountAsync(0);
    }

    [Fact]
    public async Task An_administrator_not_in_the_list_and_not_elevated_is_rejected()
    {
        Assert.SkipWhen(WindowsTestEnvironment.IsElevated, "Elevado, el usuario está autorizado por ser administrador.");
        await using var server = await PipeServerHarness.StartAsync(authorizeCurrentUser: false);
        await using var client = await server.ConnectAsync();

        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => client.Proxy.GetServiceInfoAsync(Ct));
        await server.WaitForConnectionCountAsync(0);
    }

    [Fact]
    public async Task An_abrupt_disconnect_releases_the_connection_and_does_not_affect_the_others()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var abrupt = await server.ConnectAsync();
        await using var other = await server.ConnectAsync();
        await abrupt.Proxy.GetServiceInfoAsync(Ct);
        await other.Proxy.GetServiceInfoAsync(Ct);
        await server.WaitForConnectionCountAsync(2);

        // Se cierra el handle del pipe sin cerrar JsonRpc: como si la app se cerrara de golpe.
        abrupt.Stream.Dispose();

        await server.WaitForConnectionCountAsync(1);
        Assert.Equal(PipeServerHarness.CurrentUser.Value, (await other.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
        await using var replacement = await server.ConnectAsync();
        await replacement.Proxy.GetServiceInfoAsync(Ct);
        await server.WaitForConnectionCountAsync(2);
        await abrupt.DisposeAsync();
    }

    [Fact]
    public async Task Events_reach_every_client_over_the_real_pipe()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var clients = new List<TestRpcClient>();
        var received = new List<TaskCompletionSource<StatusChangedEvent>>();
        for (var i = 0; i < 3; i++)
        {
            var client = await server.ConnectAsync();
            var tcs = new TaskCompletionSource<StatusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Proxy.StatusChanged += (_, e) => tcs.TrySetResult(e);
            await client.Proxy.GetServiceInfoAsync(Ct);
            clients.Add(client);
            received.Add(tcs);
        }

        server.Events.PublishStatusChanged(new StatusChangedEvent { Status = new ServiceStatus { Health = HealthState.Error } });

        foreach (var tcs in received)
        {
            Assert.Equal(HealthState.Error, (await tcs.Task.WaitAsync(Wait, Ct)).Status.Health);
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_oversized_message_closes_the_real_connection()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var pipe = await server.OpenAsync();

        await pipe.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {BackboneIpc.MaxMessageBytes + 1}\r\n\r\n"), Ct);
        await pipe.FlushAsync(Ct);

        Assert.True(await RawFraming.WaitForEndOfStreamAsync(pipe, Wait, Ct));
        await server.WaitForConnectionCountAsync(0);
    }

    [Fact]
    public async Task The_server_accepts_eight_connections_and_closes_the_ninth()
    {
        await using var server = await PipeServerHarness.StartAsync();
        var pipes = new List<NamedPipeClientStream>();
        for (var i = 0; i < BackboneIpc.MaxConnections; i++)
        {
            pipes.Add(await server.OpenAsync());
        }

        await server.WaitForConnectionCountAsync(BackboneIpc.MaxConnections);
        await using var ninth = await server.OpenAsync();

        Assert.True(await RawFraming.WaitForEndOfStreamAsync(ninth, Wait, Ct));
        Assert.Equal(BackboneIpc.MaxConnections, server.Manager.Count);
        foreach (var pipe in pipes)
        {
            await pipe.DisposeAsync();
        }

        await server.WaitForConnectionCountAsync(0);
    }

    [Fact]
    public async Task Adding_an_authorized_user_updates_the_acl_of_the_existing_pipe()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var pipe = await server.OpenAsync();

        await server.Settings.AddAuthorizedUserAsync(new SecurityIdentifier(TestSids.Ana), Ct);

        // El descriptor es del pipe: lo que se aplicó a la instancia que escucha lo ve también esta (ya conectada).
        var rules = pipe.GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();
        var ana = Assert.Single(rules, r => r.IdentityReference.Value == TestSids.Ana);
        Assert.Equal(PipeSecurityFactory.ClientRights, ana.PipeAccessRights);
    }

    [Fact]
    public async Task Client_rights_are_enough_for_JSON_RPC_and_GENERIC_WRITE_is_not_granted_to_authorized_users()
    {
        // Instancia sola con la ACL de producción (sin el SID del servidor de pruebas): el usuario solo tiene ClientRights.
        var name = PipeServerHarness.NewPipeName();
        await using var instance = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0,
            PipeSecurityFactory.Create([PipeServerHarness.CurrentUser.Value]));
        var accept = instance.WaitForConnectionAsync(Ct);

        if (!WindowsTestEnvironment.IsElevated)
        {
            // PipeDirection.InOut pide GENERIC_WRITE, que incluye FILE_CREATE_PIPE_INSTANCE: se deniega.
            using var generic = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
            Assert.Throws<UnauthorizedAccessException>(() => generic.Connect(2000));
        }

        await using var client = new NamedPipeClientStream(".", name, PipeSecurityFactory.ClientRights, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
        await client.ConnectAsync(5000, Ct);
        await accept;

        var payload = "hola"u8.ToArray();
        await client.WriteAsync(payload, Ct);
        var buffer = new byte[payload.Length];
        await instance.ReadExactlyAsync(buffer, Ct);
        await instance.WriteAsync(buffer, Ct);
        await client.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task The_identity_used_is_the_one_of_the_token_of_the_client()
    {
        await using var server = await PipeServerHarness.StartAsync();
        await using var pipe = await server.OpenAsync();

        await using var client = new TestRpcClient(pipe);
        await client.Proxy.GetServiceInfoAsync(Ct);

        var connection = Assert.Single(server.Manager.Connections);
        Assert.NotNull(connection.Identity);
        Assert.Equal(PipeServerHarness.CurrentUser, connection.Identity.Sid);
        Assert.Equal(TokenImpersonationLevel.Impersonation, connection.Identity.ImpersonationLevel);
        Assert.Equal(WindowsTestEnvironment.IsElevated, connection.Identity.IsElevated);
    }
}
