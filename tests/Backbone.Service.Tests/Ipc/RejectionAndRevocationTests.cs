using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using StreamJsonRpc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>
/// §7.1 (B-2, B-4) y §7.3: cierre ordenado de una conexión rechazada frente a cierre inmediato de una conexión revocada, y
/// cómputo por SID de las conexiones rechazadas mientras esperan a cerrarse (servidor completo sobre flujos en memoria).
/// </summary>
[Trait("Story", "HT-03")]
public sealed class RejectionAndRevocationTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly IpcTestHarness _server;

    /// <summary>Retiene la llamada en curso (conexión al NAS que no responde) hasta que la prueba, o su limpieza, la libere.</summary>
    private readonly TaskCompletionSource _inProgress = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RejectionAndRevocationTests() => _server = new IpcTestHarness();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(_server.AuthorizeAsync(TestSids.Ana, TestSids.Luis));

    public async ValueTask DisposeAsync()
    {
        _inProgress.TrySetResult();
        await _server.DisposeAsync();
    }

    // ---- Revocación de una conexión ya autorizada (B-1 de la revisión) ----

    [Fact]
    public async Task Revocation_detected_in_a_call_closes_at_once_and_cancels_the_calls_in_progress()
    {
        // La llamada en curso espera de forma asíncrona (el conector del NAS no responde): así la conexión sigue leyendo
        // mensajes. Una llamada que se bloquease de forma síncrona antes de su primer await (como lo hacía esta prueba con DPAPI,
        // con un tiempo máximo de 30 s) detiene el bucle de lectura de StreamJsonRpc y la llamada que detecta la revocación ni
        // siquiera se procesa hasta que la otra termina: la prueba tardaba siempre 30 s y fallaba de forma intermitente en el
        // límite de esos 30 s, cuando la respuesta de la llamada en curso llegaba antes que el cierre.
        _server.ShareConnector.ConnectGate = _inProgress;
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var test = ana.Proxy.TestConnectionAsync(@"\\nas\copias", new CredentialInput { UserName = "u", Password = "p" }, Ct);
        await WaitUntilAsync(() => !_server.ShareConnector.Connections.IsEmpty, "La llamada en curso no ha llegado a conectar.");

        _server.Accounts.SetAdministrator(TestSids.Ana, false);
        var revoking = await Record.ExceptionAsync(() => ana.Proxy.GetServiceInfoAsync(Ct).WaitAsync(Wait, Ct));

        // La llamada que lo detecta no recibe nada útil (NotAuthorized si llega a salir, o la conexión cerrada) y la conexión
        // deja de estar autorizada en el acto, sin esperar a RejectionLinger (el reloj no avanza en toda la prueba).
        AssertNotAuthorizedOrConnectionLost(revoking);
        Assert.False(ana.RequiredServer.IsAuthorized);

        // La llamada en curso se queda sin respuesta: el cliente ve la conexión cerrada antes de que la operación termine.
        var inProgress = await Record.ExceptionAsync(() => test.WaitAsync(Wait, Ct));
        Assert.IsType<ConnectionLostException>(inProgress);
        Assert.False(_inProgress.Task.IsCompleted, "La llamada en curso no debía haber terminado aún.");

        _inProgress.TrySetResult();
        await ana.WaitClosedAsync();
        Assert.True(ana.Channel.IsDisposed);
        Assert.Equal(0, _server.Manager.Count);
        Assert.Contains(nameof(CloseReason.Revoked), _server.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_the_SID_from_the_list_while_a_call_is_detected_closes_without_waiting_for_the_linger()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var other = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        await _server.Settings.RemoveAuthorizedUserAsync(new System.Security.Principal.SecurityIdentifier(TestSids.Ana), Ct);

        AssertNotAuthorizedOrConnectionLost(await Record.ExceptionAsync(() => ana.Proxy.GetServiceInfoAsync(Ct)));

        await ana.WaitClosedAsync();
        Assert.Equal(TestSids.Luis, (await other.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
    }

    [Fact]
    public async Task A_revoked_connection_receives_no_events()
    {
        var ana = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var received = 0;
        ana.Proxy.StatusChanged += (_, _) => Interlocked.Increment(ref received);
        _server.Accounts.SetAdministrator(TestSids.Ana, false);
        _ = await Record.ExceptionAsync(() => ana.Proxy.GetServiceInfoAsync(Ct));

        _server.Manager.Broadcast(nameof(IBackboneService.StatusChanged), new StatusChangedEvent { Status = new ServiceStatus() });

        await ana.WaitClosedAsync();
        Assert.Equal(0, Volatile.Read(ref received));
    }

    // ---- Conexión rechazada durante la espera (I-4 a y b) ----

    [Fact]
    public async Task A_rejected_connection_receives_neither_Broadcast_nor_SendToSid_while_it_lingers()
    {
        // Rechazada con el SID de Ana (sin suplantación): SendToSid(Ana) la encontraría por SID si no comprobase el estado.
        var rejected = _server.Connect(TestSids.Identity(TestSids.Ana, level: System.Security.Principal.TokenImpersonationLevel.Identification));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => rejected.Proxy.GetServiceInfoAsync(Ct));
        var control = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        var leaked = 0;
        rejected.Proxy.StatusChanged += (_, _) => Interlocked.Increment(ref leaked);
        rejected.Proxy.RestoreProgress += (_, _) => Interlocked.Increment(ref leaked);
        var status = Subscribe<StatusChangedEvent>(h => control.Proxy.StatusChanged += h);
        var restore = Subscribe<RestoreProgressEvent>(h => control.Proxy.RestoreProgress += h);

        _server.Events.PublishStatusChanged(new StatusChangedEvent { Status = new ServiceStatus { Health = HealthState.Warning } });
        _server.Events.PublishRestoreProgress(TestSids.Ana, new RestoreProgressEvent { RestoreId = Guid.NewGuid(), State = RestoreState.Running });

        await status.WaitAsync(Wait, Ct);
        await restore.WaitAsync(Wait, Ct);
        // Los mensajes de una conexión llegan en orden: si se le hubiera enviado un evento, llegaría antes que esta respuesta.
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => rejected.Proxy.GetServiceInfoAsync(Ct));
        Assert.Equal(0, Volatile.Read(ref leaked));
        await _server.WaitRejectedClosedAsync(rejected);
    }

    [Fact]
    public async Task A_rejected_connection_is_not_closed_one_tick_before_the_linger_and_is_closed_at_the_linger()
    {
        var rejected = _server.Connect(TestSids.Identity(TestSids.Eva)); // no está en la lista ni es administradora
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => rejected.Proxy.GetServiceInfoAsync(Ct));

        _server.Time.Advance(_server.Options.RejectionLinger - TimeSpan.FromTicks(1));

        // Sigue abierta: el cliente aún puede leer respuestas (y recibe NotAuthorized, no la conexión cerrada).
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => rejected.Proxy.GetServiceInfoAsync(Ct));
        Assert.False(rejected.RequiredServer.Completion.IsCompleted);
        Assert.False(rejected.Channel.IsDisposed);
        Assert.Equal(1, _server.Manager.Count);

        _server.Time.Advance(TimeSpan.FromTicks(1));

        await rejected.WaitClosedAsync();
        Assert.True(rejected.Channel.IsDisposed);
        Assert.Equal(0, _server.Manager.Count);
    }

    [Fact]
    public async Task Every_rejection_reason_gets_the_same_NotAuthorized_text()
    {
        for (var i = 0; i < IpcOptions.DefaultMaxConnectionsPerSid; i++)
        {
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        }

        var connections = new[]
        {
            _server.Connect(TestSids.Identity(TestSids.Eva)),
            _server.Connect(TestSids.Identity(TestSids.Luis, level: System.Security.Principal.TokenImpersonationLevel.Identification)),
            _server.Connect(() => throw new IOException("ImpersonateNamedPipeClient failed")),
            _server.Connect(TestSids.Identity(TestSids.Ana)), // quinta de Ana
        };

        foreach (var connection in connections)
        {
            var data = await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => connection.Proxy.GetServiceInfoAsync(Ct));
            Assert.Equal(IpcConnection.NotAuthorizedMessage, data.Message);
            data = await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => connection.Proxy.GetServiceInfoAsync(Ct));
            Assert.Equal(IpcConnection.NotAuthorizedMessage, data.Message);
        }
    }

    // ---- Límite por SID con conexiones rechazadas (B-2 de la revisión) ----

    [Fact]
    public async Task A_connection_rejected_for_exceeding_the_per_account_limit_counts_for_its_SID_until_it_closes()
    {
        var ana = new List<TestConnection>();
        for (var i = 0; i < IpcOptions.DefaultMaxConnectionsPerSid; i++)
        {
            ana.Add(await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana)));
        }

        var fifth = _server.Connect(TestSids.Identity(TestSids.Ana));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => fifth.Proxy.GetServiceInfoAsync(Ct));
        Assert.Equal(IpcOptions.DefaultMaxConnectionsPerSid + 1, _server.Manager.CountForSid(TestSids.Ana));

        // Se libera una de las autorizadas mientras la quinta espera: la cuenta sigue en el límite (la rechazada ocupa una
        // instancia), así que reconectar no le da otra.
        await ana[0].Client.DisposeAsync();
        await ana[0].WaitClosedAsync();
        var retry = _server.Connect(TestSids.Identity(TestSids.Ana));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => retry.Proxy.GetServiceInfoAsync(Ct));

        // Otra cuenta no se ve afectada.
        var luis = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Luis));
        Assert.Equal(TestSids.Luis, (await luis.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);

        // Al cerrarse las rechazadas, la cuenta recupera su hueco.
        _server.Time.Advance(_server.Options.RejectionLinger);
        await fifth.WaitClosedAsync();
        await retry.WaitClosedAsync();
        Assert.Equal(IpcOptions.DefaultMaxConnectionsPerSid - 1, _server.Manager.CountForSid(TestSids.Ana));
        var replacement = await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        Assert.Equal(TestSids.Ana, (await replacement.Proxy.GetServiceInfoAsync(Ct)).Caller.UserSid);
    }

    [Fact]
    public async Task A_rejected_connection_that_the_client_closes_frees_its_SID_count()
    {
        for (var i = 0; i < IpcOptions.DefaultMaxConnectionsPerSid; i++)
        {
            await _server.ConnectAuthorizedAsync(TestSids.Identity(TestSids.Ana));
        }

        var fifth = _server.Connect(TestSids.Identity(TestSids.Ana));
        await RpcAssert.FailsWithAsync(BackboneErrorCode.NotAuthorized, () => fifth.Proxy.GetServiceInfoAsync(Ct));

        await fifth.Client.DisposeAsync();
        await fifth.WaitClosedAsync();

        Assert.Equal(IpcOptions.DefaultMaxConnectionsPerSid, _server.Manager.CountForSid(TestSids.Ana));
    }

    private static void AssertNotAuthorizedOrConnectionLost(Exception? ex)
    {
        switch (ex)
        {
            case RemoteInvocationException rpc:
                Assert.Equal((int)BackboneErrorCode.NotAuthorized, rpc.ErrorCode);
                break;
            case ConnectionLostException:
                break;
            default:
                Assert.Fail($"Se esperaba NotAuthorized o la conexión cerrada y se ha obtenido {ex?.GetType().Name ?? "éxito"}.");
                break;
        }
    }

    private static Task<T> Subscribe<T>(Action<EventHandler<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe((_, e) => tcs.TrySetResult(e));
        return tcs.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Delay(10, Ct);
        }
    }
}
