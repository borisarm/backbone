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
/// trabajos (identificador, nombre, activado, actividad); en HU-06, el estado del repositorio según la última vez que se abrió
/// (<see cref="RepositoryStateTracker"/>). En HU-14, con las ejecuciones (<see cref="IRunStatusSource"/>): la ejecución en
/// curso y su progreso, la cola y la última ejecución de cada trabajo desde que arrancó el servicio, con la salud que se
/// deduce de ella. La próxima ejecución, la antigüedad y la pausa los completan las historias del scheduler, el historial y la
/// pausa (HU-09, HU-11, HU-16).
/// </summary>
internal sealed class ServiceStatusProvider(
    IJobList jobs,
    IJobActivity activity,
    RepositoryStateTracker repositoryStates,
    TimeProvider timeProvider,
    IRunStatusSource? runs = null)
{
    public ServiceStatus GetStatus()
    {
        var statuses = jobs.Jobs.Select(job =>
        {
            var last = runs?.LastRun(job.Id);
            return new JobStatus
            {
                JobId = job.Id,
                JobName = job.Name,
                Enabled = job.Enabled,
                Activity = activity.GetActivity(job.Id),
                Health = HealthOf(last),
                LastRun = last,
                LastSuccessUtc = runs?.LastSuccessUtc(job.Id),
                RepositoryState = repositoryStates.Get(job.Id),
            };
        }).ToList();
        return new ServiceStatus
        {
            Health = statuses.Where(s => s.Enabled).Select(s => s.Health).DefaultIfEmpty(HealthState.Ok).Max(),
            CurrentRun = runs?.CurrentRun,
            Queue = runs?.Queue ?? [],
            Jobs = statuses,
            TimestampUtc = timeProvider.GetUtcNow().ToUniversalTime(),
        };
    }

    /// <summary>Salud que se deduce de la última ejecución: fallida, error; con avisos, aviso; si no, correcta (HU-11 añade la antigüedad).</summary>
    private static HealthState HealthOf(RunRecord? last) => last?.State switch
    {
        RunState.Failed => HealthState.Error,
        RunState.CompletedWithWarnings => HealthState.Warning,
        _ => HealthState.Ok,
    };
}

/// <summary>
/// Emite <c>StatusChanged</c> a todos los clientes cada vez que cambia la lista de trabajos (§7.5: <c>SaveJobAsync</c> y
/// <c>DeleteJobAsync</c> lo emiten). Escucha <see cref="IJobList.Changed"/>, que se lanza después de persistir y antes de que
/// la llamada responda.
/// </summary>
internal sealed class StatusBroadcaster(IJobList jobs, ServiceStatusProvider status, IIpcEventPublisher events, IRunStatusSource? runs = null) : IHostedService, IDisposable
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
            if (runs is not null)
            {
                runs.Changed += OnJobsChanged;
            }
        }
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            jobs.Changed -= OnJobsChanged;
            if (runs is not null)
            {
                runs.Changed -= OnJobsChanged;
            }
        }
    }

    private void OnJobsChanged(object? sender, EventArgs e) =>
        events.PublishStatusChanged(new StatusChangedEvent { Status = status.GetStatus() });
}
