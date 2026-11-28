using System.IO.Pipes;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// Arranque fallido (p. ej. carpeta de datos insegura, RNF-07): liberar el host, una o dos veces, no lanza ninguna
/// excepción; el proceso termina con código 1 y no con una excepción no controlada.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class StartupFailureDisposalTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost BuildFailingHost()
    {
        var builder = BackboneHost.CreateBuilder(new BackboneHostOptions
        {
            DataRoot = _temp.Path,
            Ipc = new IpcOptions { PipeName = PipeServerHarness.NewPipeName() },
        });
        builder.Services.AddSingleton<IEventLogWriter, FakeEventLogWriter>();
        builder.Services.AddSingleton<IDataDirectoryGuard>(new ThrowingGuard());
        return builder.Build();
    }

    [Fact]
    public async Task A_failed_start_throws_ServiceStartupException_and_disposing_the_host_twice_does_not_throw()
    {
        var host = BuildFailingHost();

        await Assert.ThrowsAsync<ServiceStartupException>(() => host.StartAsync(Ct));

        var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(host);
        await disposable.DisposeAsync();
        await disposable.DisposeAsync();
        host.Dispose();
    }

    [Fact]
    public async Task RunAsync_surfaces_only_ServiceStartupException_when_the_start_fails()
    {
        // Como Program.cs: RunAsync arranca, falla y libera el host; no debe aparecer ninguna otra excepción.
        var host = BuildFailingHost();

        await Assert.ThrowsAsync<ServiceStartupException>(() => host.RunAsync(Ct));
    }

    [Fact]
    public async Task The_IPC_server_can_be_disposed_twice()
    {
        await using var harness = await PipeServerHarness.CreateAsync();

        await harness.Server.DisposeAsync();
        await harness.Server.DisposeAsync();
    }

    [Fact]
    public async Task The_IPC_server_can_be_stopped_and_disposed_without_having_been_started()
    {
        await using var harness = await PipeServerHarness.CreateAsync();

        await harness.Server.StopAsync(Ct);
        await harness.Server.StopAsync(Ct);
        await harness.Server.DisposeAsync();
    }

    [Fact]
    public async Task The_IPC_server_can_be_disposed_after_a_failed_start_and_after_a_normal_stop()
    {
        var name = PipeServerHarness.NewPipeName();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var failing = await PipeServerHarness.CreateAsync(pipeName: name);
        await Assert.ThrowsAsync<ServiceStartupException>(() => failing.Server.StartAsync(Ct));
        await failing.Server.DisposeAsync();
        await failing.Server.DisposeAsync();

        await using var running = await PipeServerHarness.StartAsync();
        await running.Server.StopAsync(Ct);
        await running.Server.DisposeAsync();
        await running.Server.DisposeAsync();
        await running.Server.StopAsync(Ct);
    }

    private sealed class ThrowingGuard : IDataDirectoryGuard
    {
        public DataDirectoryCheck EnsureSecure(string root) =>
            throw new DataDirectoryInsecureException("The data directory could not be created with the required security.");
    }
}
