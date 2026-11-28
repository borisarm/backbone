using System.Collections.Concurrent;

namespace Backbone.Service.Os;

/// <summary>
/// Hilo dedicado de VSS: <c>IVssBackupComponents</c> se crea, se usa y se suelta siempre desde el mismo hilo, que además tiene
/// COM inicializado (multihilo) todo el tiempo que viva alguna instantánea. Ejecuta los trabajos en cola, de uno en uno, y los
/// entrega con un <see cref="TaskCompletionSource{TResult}"/>; cada trabajo es síncrono.
/// </summary>
/// <remarks>
/// Se arranca con <see cref="Thread.UnsafeStart()"/> para que no le llegue por el <see cref="ExecutionContext"/> ninguna
/// suplantación del hilo que lo pide (p. ej. la de una llamada RPC): VSS se llama siempre con el token del propio proceso
/// (SYSTEM con los privilegios de §10), nunca con el de un cliente ni con el token de lectura de los orígenes (ADR-21).
/// </remarks>
internal sealed class VssWorker(bool initializeCom = true) : IDisposable
{
    /// <summary>Lo que se espera a que el hilo termine su cola al desechar el trabajador.</summary>
    internal static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

    private readonly BlockingCollection<Action> _queue = [];
    private readonly Lock _gate = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Ejecuta <paramref name="work"/> en el hilo de VSS.</summary>
    /// <exception cref="ObjectDisposedException">El trabajador está desechado.</exception>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Backbone VSS" };
                _thread.UnsafeStart();
            }

            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        }

        return completion.Task;
    }

    private void Run()
    {
        var initialized = false;
        if (initializeCom)
        {
            // S_OK (0) o S_FALSE (1, el hilo ya estaba inicializado): en los dos hay que llamar a CoUninitialize.
            initialized = NativeMethods.Vss.CoInitializeEx(0, NativeMethods.Vss.CoInitMultithreaded) >= 0;
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                item();
            }
        }
        finally
        {
            if (initialized)
            {
                NativeMethods.Vss.CoUninitialize();
            }
        }
    }

    /// <summary>Termina lo que haya en cola (hasta <see cref="JoinTimeout"/>) y para el hilo.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            _queue.CompleteAdding();
        }

        _ = thread?.Join(JoinTimeout);
        if (thread is null || !thread.IsAlive)
        {
            _queue.Dispose();
        }
    }
}
