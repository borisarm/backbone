using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Service.Jobs;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Doble de <see cref="IJobActivity"/>: actividad fijada por la prueba; por defecto, inactivo.</summary>
internal sealed class FakeJobActivity : IJobActivity
{
    private readonly ConcurrentDictionary<Guid, JobActivity> _activity = new();

    public void Set(Guid jobId, JobActivity activity) => _activity[jobId] = activity;

    public JobActivity GetActivity(Guid jobId) => _activity.TryGetValue(jobId, out var activity) ? activity : JobActivity.Idle;
}
