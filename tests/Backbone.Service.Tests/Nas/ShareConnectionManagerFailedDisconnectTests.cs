using Backbone.Service.Nas;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// Segunda revisión de seguridad de HU-06. BAJA-N2: una desconexión sin forzar que falla (p. ej. <c>ERROR_OPEN_FILES</c>) no
/// deja la conexión sin dueño: se reintenta a la fuerza al volver a conectar la raíz o al soltar otra, y siempre en la parada.
/// BAJA-N1: la parada deja constancia de las raíces que cierra con concesiones vivas.
/// </summary>
[Trait("Story", "HU-06")]
public sealed class ShareConnectionManagerFailedDisconnectTests : IDisposable
{
    private const string Root = @"\\nas\backups";
    private const string Other = @"\\nas2\fotos";

    private readonly FakeShareConnector _connector = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ShareConnectionManager _manager;

    public ShareConnectionManagerFailedDisconnectTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        _manager = new ShareConnectionManager(_connector, _loggerFactory.CreateLogger<ShareConnectionManager>());
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NasCredential Credential(string root = Root, string user = "ana", string password = "clave-secreta-N2")
    {
        var buffer = GC.AllocateArray<char>(password.Length + 1, pinned: true);
        password.AsSpan().CopyTo(buffer);
        return new NasCredential(root, user, DateTimeOffset.UnixEpoch, buffer, password.Length, isExplicit: false);
    }

    private async Task FailedReleaseAsync(string root = Root)
    {
        using var credential = Credential(root);
        var lease = await _manager.AcquireAsync(credential, Ct);
        _connector.Share.DisconnectFails = true;
        await lease.DisposeAsync(); // no lanza: la desconexión falla sin excepción
        _connector.Share.DisconnectFails = false;
    }

    [Fact]
    public async Task A_failed_disconnect_does_not_forget_the_root_and_the_shutdown_forces_it()
    {
        await FailedReleaseAsync();
        Assert.Empty(_connector.ForcedDisconnects);
        Assert.True(_connector.UseRecords.ContainsKey(Root)); // la conexión sigue viva en el "sistema"

        _manager.Close();
        await _manager.ForceDisconnectAllAsync(Ct);

        Assert.Equal([Root], _connector.ForcedDisconnects);
        Assert.False(_connector.UseRecords.ContainsKey(Root));
    }

    [Fact]
    public async Task Forcing_a_root_clears_it_so_a_second_shutdown_pass_does_not_repeat_it()
    {
        await FailedReleaseAsync();
        _manager.Close();

        await _manager.ForceDisconnectAllAsync(Ct);
        await _manager.ForceDisconnectAllAsync(Ct);

        Assert.Equal([Root], _connector.ForcedDisconnects);
    }

    [Fact]
    public async Task Acquiring_a_root_with_a_failed_disconnect_forces_it_first_and_then_connects()
    {
        await FailedReleaseAsync();

        using var second = Credential();
        await using var lease = await _manager.AcquireAsync(second, Ct);

        Assert.Equal([Root], _connector.ForcedDisconnects);
        Assert.Equal(["connect:" + Root, "disconnect-failed:" + Root, "connect:" + Root], _connector.Events);
        Assert.True(await lease.Connection.HasUseRecordAsync(Ct)); // la fuerza fue antes de conectar: el registro nuevo sigue ahí
    }

    [Fact]
    public async Task Releasing_another_root_retries_the_failed_disconnect_when_it_has_no_live_leases()
    {
        await FailedReleaseAsync();
        using var other = Credential(Other);
        var lease = await _manager.AcquireAsync(other, Ct);
        Assert.Empty(_connector.ForcedDisconnects);

        await lease.DisposeAsync();

        Assert.Equal([Root], _connector.ForcedDisconnects);
        Assert.False(_connector.UseRecords.ContainsKey(Root));
    }

    [Fact]
    public async Task A_root_with_live_leases_is_not_forced_by_the_retries_of_other_roots()
    {
        await FailedReleaseAsync();
        using var again = Credential();
        await using var live = await _manager.AcquireAsync(again, Ct); // fuerza una vez y vuelve a conectar
        _connector.ForcedDisconnects.Clear();
        using var other = Credential(Other);

        await using (await _manager.AcquireAsync(other, Ct))
        {
        }

        Assert.Empty(_connector.ForcedDisconnects);
        Assert.True(await live.Connection.HasUseRecordAsync(Ct));
    }

    [Fact]
    public async Task A_disconnect_that_throws_is_also_remembered_for_the_shutdown()
    {
        using var credential = Credential();
        var lease = await _manager.AcquireAsync(credential, Ct);
        _connector.Share.DisposeError = new IOException("red cortada");
        await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask());
        _connector.Share.DisposeError = null;

        _manager.Close();
        await _manager.ForceDisconnectAllAsync(Ct);

        Assert.Equal([Root], _connector.ForcedDisconnects);
    }

    [Fact]
    public async Task Forcing_with_live_leases_logs_a_warning_without_credentials()
    {
        using var credential = Credential();
        await using var lease = await _manager.AcquireAsync(credential, Ct);
        using var second = Credential();
        await using var lease2 = await _manager.AcquireAsync(second, Ct);
        _manager.Close();

        await _manager.ForceDisconnectAllAsync(Ct);

        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("concesiones vivas", StringComparison.Ordinal));
        Assert.Contains("2 concesiones vivas", warning.Message, StringComparison.Ordinal);
        Assert.Contains(@"\\nas\backups", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("clave-secreta-N2", _logs.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain("ana", _logs.AllText(), StringComparison.Ordinal);
        Assert.Equal([Root], _connector.ForcedDisconnects);
    }

    [Fact]
    public async Task Forcing_with_no_live_leases_does_not_log_the_live_lease_warning()
    {
        using var credential = Credential();
        await (await _manager.AcquireAsync(credential, Ct)).DisposeAsync();
        _manager.Close();

        await _manager.ForceDisconnectAllAsync(Ct);

        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("concesiones vivas", StringComparison.Ordinal));
    }
}
