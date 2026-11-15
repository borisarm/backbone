using System.Reflection;
using Backbone.App.Ipc;
using Backbone.Contracts;

namespace Backbone.App.Tests.Ipc;

/// <summary>
/// <see cref="IBackboneService"/> falso: eventos reales (se disparan con <see cref="Raise"/>) y métodos con respuesta
/// configurable por nombre. Un método sin configurar lanza <see cref="NotSupportedException"/>.
/// </summary>
public class FakeBackboneService : DispatchProxy
{
    private readonly Dictionary<string, Delegate?> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?[], object?>> _methods = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public static (IBackboneService Service, FakeBackboneService Fake) Create()
    {
        var proxy = Create<IBackboneService, FakeBackboneService>();
        return (proxy, (FakeBackboneService)(object)proxy);
    }

    /// <summary>Llamadas recibidas, por nombre de método.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Número de manejadores suscritos al evento.</summary>
    public int HandlerCount(string eventName)
    {
        lock (_gate)
        {
            return _events.GetValueOrDefault(eventName)?.GetInvocationList().Length ?? 0;
        }
    }

    public void On(string method, Func<object?[], object?> handler)
    {
        lock (_gate)
        {
            _methods[method] = handler;
        }
    }

    public void Raise(string eventName, object argument)
    {
        Delegate? handler;
        lock (_gate)
        {
            handler = _events.GetValueOrDefault(eventName);
        }

        handler?.DynamicInvoke(this, argument);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var name = targetMethod.Name;
        lock (_gate)
        {
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                var key = name["add_".Length..];
                _events[key] = Delegate.Combine(_events.GetValueOrDefault(key), (Delegate?)args?[0]);
                return null;
            }

            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                var key = name["remove_".Length..];
                _events[key] = Delegate.Remove(_events.GetValueOrDefault(key), (Delegate?)args?[0]);
                return null;
            }

            Calls.Add(name);
            if (!_methods.TryGetValue(name, out var handler))
            {
                throw new NotSupportedException($"{name} is not configured in the fake service.");
            }

            return handler(args ?? []);
        }
    }
}

/// <summary>Sesión falsa: el test decide cuándo "se cae la conexión".</summary>
internal sealed class FakeBackboneSession : IBackboneSession
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeBackboneSession(IBackboneService service, FakeBackboneService fake, ServiceInfo? info = null)
    {
        Service = service;
        Fake = fake;
        Info = info ?? new ServiceInfo { ServiceVersion = "1.0.0", ContractVersion = ContractVersion.Value };
    }

    public IBackboneService Service { get; }

    public FakeBackboneService Fake { get; }

    public ServiceInfo Info { get; }

    public Task Completion => _closed.Task;

    public bool Disposed { get; private set; }

    public static FakeBackboneSession Create(ServiceInfo? info = null)
    {
        var (service, fake) = FakeBackboneService.Create();
        return new FakeBackboneSession(service, fake, info);
    }

    /// <summary>Simula que la conexión se cierra (servicio detenido, pipe roto).</summary>
    public void Drop() => _closed.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _closed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Conector falso con una cola de resultados: sesiones o excepciones. Cuando se agota repite el último.</summary>
internal sealed class FakeConnector : IBackboneConnector
{
    private readonly Queue<object> _script = new();
    private object? _last;
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public FakeConnector Then(IBackboneSession session)
    {
        lock (_script)
        {
            _script.Enqueue(session);
        }

        return this;
    }

    public FakeConnector Then(Exception failure)
    {
        lock (_script)
        {
            _script.Enqueue(failure);
        }

        return this;
    }

    public Task<IBackboneSession> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _attempts);
        object next;
        lock (_script)
        {
            next = _script.Count > 0 ? _script.Dequeue() : _last ?? new TimeoutException("no script");
            _last = next;
        }

        return next switch
        {
            IBackboneSession session => Task.FromResult(session),
            Exception ex => Task.FromException<IBackboneSession>(ex),
            _ => throw new InvalidOperationException(),
        };
    }
}
