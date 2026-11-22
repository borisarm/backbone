using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Service.Jobs;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="IJobActivity"/>: actividad fijada por la prueba; por defecto, la de <see cref="Fallback"/> (la cola de
/// ejecuciones real, HU-14) o inactivo.
/// </summary>
internal sealed class FakeJobActivity : IJobActivity
{
    private readonly ConcurrentDictionary<Guid, JobActivity> _activity = new();

    /// <summary>Actividad de los trabajos que la prueba no ha fijado.</summary>
    public IJobActivity? Fallback { get; set; }

    public void Set(Guid jobId, JobActivity activity) => _activity[jobId] = activity;

    public JobActivity GetActivity(Guid jobId) =>
        _activity.TryGetValue(jobId, out var activity) ? activity : Fallback?.GetActivity(jobId) ?? JobActivity.Idle;
}
