using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// HU-09: el scheduler está cableado en el host, encola en la cola de ejecuciones real, publica la próxima ejecución en el
/// estado y se arranca después de leer <c>jobs.json</c> y de la cola, y se para antes que ella.
/// </summary>
[Trait("Story", "HU-09")]
public sealed class SchedulerWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private IHost Build() =>
        BackboneHost.CreateBuilder(new BackboneHostOptions { DataRoot = _temp.Path }).Build();

    [Fact]
    public void The_scheduler_feeds_the_run_queue_and_the_status_with_the_next_run()
    {
        using var host = Build();
        var services = host.Services;

        Assert.Same(services.GetRequiredService<Scheduler>(), services.GetRequiredService<INextRunSource>());
        Assert.Same(services.GetRequiredService<RunQueue>(), services.GetRequiredService<IRunEnqueuer>());
        Assert.Same(services.GetRequiredService<ScheduleSettings>(), services.GetRequiredService<IScheduleSettings>());
        Assert.Equal(_temp.Combine("data", "schedule.json"), services.GetRequiredService<ServicePaths>().ScheduleFile);
        Assert.NotNull(services.GetRequiredService<ServiceStatusProvider>());
    }

    [Fact]
    public void The_scheduler_starts_after_the_run_queue_and_stops_before_it()
    {
        var host = Build();
        var hosted = host.Services.GetServices<IHostedService>().ToList();

        // StartingAsync (lee jobs.json) de StartupService va antes que cualquier StartAsync; el resto, en orden de registro.
        var scheduler = hosted.FindIndex(s => s is Scheduler);
        Assert.True(scheduler > hosted.FindIndex(s => s is RunQueue), "El scheduler arranca después de la cola y, al pararse en orden inverso, antes que ella.");
        Assert.True(scheduler < hosted.FindIndex(s => s is StatusBroadcaster), "El estado ya puede enviarse cuando el scheduler publica.");
    }
}
