using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// HU-14: las ejecuciones están cableadas en el host sin ciclos de dependencias (la cola no depende del servidor IPC, que
/// depende de las operaciones que la usan) y se paran antes de que el gestor de conexiones cierre a la fuerza lo que quede.
/// </summary>
[Trait("Story", "HU-14")]
public sealed class RunWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private IHost Build() =>
        BackboneHost.CreateBuilder(new BackboneHostOptions { DataRoot = _temp.Path }).Build();

    [Fact]
    public void The_run_queue_is_the_job_activity_and_the_status_source_and_the_whole_service_graph_resolves()
    {
        using var host = Build();
        var services = host.Services;

        var queue = services.GetRequiredService<RunQueue>();

        Assert.Same(queue, services.GetRequiredService<IJobActivity>());
        Assert.Same(queue, services.GetRequiredService<IRunStatusSource>());
        Assert.NotNull(services.GetRequiredService<JobRunner>());
        Assert.NotNull(services.GetRequiredService<BackboneOperations>());
        Assert.NotNull(services.GetRequiredService<ServiceStatusProvider>());
        Assert.NotNull(services.GetRequiredService<StatusBroadcaster>());
        Assert.NotNull(services.GetRequiredService<RunEventBroadcaster>());
    }

    [Fact]
    public void The_run_queue_stops_before_the_connections_are_closed_by_force_and_after_its_events_are_forwarded()
    {
        // Sin desechar el host: IpcServer no se ha arrancado y su DisposeAsync no lo admite (ajeno a esta prueba).
        var host = Build();
        var hosted = host.Services.GetServices<IHostedService>().ToList();

        // La parada es en orden inverso al registro.
        var queue = hosted.FindIndex(s => s is RunQueue);
        Assert.True(queue > hosted.FindIndex(s => s is ShareConnectionShutdown), "La cola tiene que pararse antes que ShareConnectionShutdown.");
        Assert.True(queue > hosted.FindIndex(s => s is RunEventBroadcaster), "RunCompleted de la ejecución cancelada tiene que poder salir.");
        Assert.True(queue < hosted.FindIndex(s => s is IpcServer), "El pipe se cierra primero: no entran peticiones mientras la cola se para.");
    }
}
