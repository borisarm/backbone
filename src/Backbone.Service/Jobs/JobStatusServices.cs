using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Hosting;

namespace Backbone.Service.Jobs;

/// <summary>
/// Actividad de los trabajos (en ejecución o en cola, RF-15). La implementa el scheduler (HU-09); hasta entonces,
/// <see cref="NoJobActivity"/>.
/// </summary>
internal interface IJobActivity
{
    /// <summary>Actividad actual del trabajo.</summary>
    JobActivity GetActivity(Guid jobId);
}

/// <summary>Sin scheduler ni ejecuciones: todos los trabajos están inactivos.</summary>
internal sealed class NoJobActivity : IJobActivity
{
    public JobActivity GetActivity(Guid jobId) => JobActivity.Idle;
}

/// <summary>
/// Construye <see cref="ServiceStatus"/> para <c>GetStatusAsync</c> y <c>StatusChanged</c>. En HU-07 lleva la lista de
/// trabajos (identificador, nombre, activado, actividad); la salud, la última y la próxima ejecución, la cola, la pausa y el
/// estado del repositorio los completan las historias del scheduler, el historial y la pausa (HU-09, HU-11, HU-14, HU-16).
/// </summary>
internal sealed class ServiceStatusProvider(IJobList jobs, IJobActivity activity, TimeProvider timeProvider)
{
    public ServiceStatus GetStatus() =>
        new()
        {
            Health = HealthState.Ok,
            Jobs =
            [
                .. jobs.Jobs.Select(job => new JobStatus
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    Enabled = job.Enabled,
                    Activity = activity.GetActivity(job.Id),
                    Health = HealthState.Ok,
                }),
            ],
            TimestampUtc = timeProvider.GetUtcNow().ToUniversalTime(),
        };
}

/// <summary>
/// Emite <c>StatusChanged</c> a todos los clientes cada vez que cambia la lista de trabajos (§7.5: <c>SaveJobAsync</c> y
/// <c>DeleteJobAsync</c> lo emiten). Escucha <see cref="IJobList.Changed"/>, que se lanza después de persistir y antes de que
/// la llamada responda.
/// </summary>
internal sealed class StatusBroadcaster(IJobList jobs, ServiceStatusProvider status, IIpcEventPublisher events) : IHostedService, IDisposable
{
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() => Unsubscribe();

    /// <summary>Empieza a escuchar (idempotente).</summary>
    public void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            jobs.Changed += OnJobsChanged;
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            jobs.Changed -= OnJobsChanged;
        }
    }

    private void OnJobsChanged(object? sender, EventArgs e) =>
        events.PublishStatusChanged(new StatusChangedEvent { Status = status.GetStatus() });
}
