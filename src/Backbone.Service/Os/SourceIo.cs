using Microsoft.Win32.SafeHandles;

namespace Backbone.Service.Os;

/// <summary>Plazos y límites de la resolución de orígenes (§8.2 "Plazo de las lecturas de los orígenes", §8.3).</summary>
internal sealed class SourceIoOptions
{
    /// <summary>Plazo de cada operación que puede llegar a un filtro (apertura, <c>FSCTL_GET_REPARSE_POINT</c>, enumeración).</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Espera adicional a que vuelva una operación cancelada; después, el hilo se abandona.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validaciones de orígenes simultáneas en todo el servicio (§8.3 "Sin bloquear el hilo RPC"; el JobRunner no cuenta).</summary>
    public int MaxConcurrentValidations { get; init; } = 4;
}

/// <summary>
/// Una operación sobre un origen no ha terminado en su plazo (§8.2): el origen da <c>SourceUnreadable</c>. Con
/// <see cref="Abandoned"/>, ni siquiera ha vuelto tras cancelarla y el hilo se ha abandonado.
/// </summary>
internal sealed class SourceIoTimeoutException : IOException
{
    public SourceIoTimeoutException()
        : base("A source operation did not complete within its deadline.")
    {
    }

    public SourceIoTimeoutException(string message)
        : base(message)
    {
    }

    public SourceIoTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SourceIoTimeoutException(bool abandoned)
        : base(abandoned
            ? "A source operation did not return after being cancelled; the worker thread was abandoned."
            : "A source operation did not complete within its deadline.")
    {
        Abandoned = abandoned;
    }

    public bool Abandoned { get; }
}

/// <summary>Cancela la E/S síncrona pendiente de un hilo (<c>CancelSynchronousIo</c>); simulable en pruebas.</summary>
internal interface ISynchronousIoCanceller : IDisposable
{
    /// <summary>Pide cancelar la operación síncrona en curso del hilo. No lanza.</summary>
    void Cancel();
}

/// <summary><see cref="ISynchronousIoCanceller"/> real: handle del hilo actual con <c>THREAD_TERMINATE</c>.</summary>
internal sealed class ThreadSynchronousIoCanceller : ISynchronousIoCanceller
{
    private readonly SafeWaitHandle _thread;

    private ThreadSynchronousIoCanceller(SafeWaitHandle thread) => _thread = thread;

    /// <summary>Para el hilo que lo llama. Si no se puede abrir el hilo, la cancelación no hace nada (queda el abandono).</summary>
    public static ISynchronousIoCanceller ForCurrentThread()
    {
        var handle = NativeMethods.Resolution.OpenThread(NativeMethods.Resolution.ThreadTerminate, false, NativeMethods.Resolution.GetCurrentThreadId());
        return new ThreadSynchronousIoCanceller(handle);
    }

    public void Cancel()
    {
        if (!_thread.IsInvalid && !_thread.IsClosed)
        {
            _ = NativeMethods.Resolution.CancelSynchronousIo(_thread);
        }
    }

    public void Dispose() => _thread.Dispose();
}

/// <summary>
/// Plazo de las operaciones bloqueantes de un hilo de trabajo (§8.2, §8.3 "Sin bloquear el hilo RPC"). Se crea en el hilo
/// que hace las operaciones y solo se usa desde él.
/// </summary>
/// <remarks>
/// Cada <see cref="Run{T}"/> arma un temporizador de <see cref="SourceIoOptions.OperationTimeout"/>. Al vencer, o al
/// cancelarse la llamada, se cancela la operación (<c>CancelSynchronousIo</c> sobre el hilo y el token que recibe la
/// operación, para los dobles). Si tras vencer tampoco vuelve en <see cref="SourceIoOptions.CancellationGrace"/>, se avisa
/// al que espera (<c>onAbandon</c>) y el hilo se abandona. La cancelación solo se emite mientras la operación está armada,
/// para no cancelar por error la siguiente (Microsoft Learn, "Canceling Pending I/O Operations").
/// </remarks>
internal sealed class SourceIoGuard : IDisposable
{
    private readonly TimeProvider _time;
    private readonly SourceIoOptions _options;
    private readonly CancellationToken _callToken;
    private readonly Action _onAbandon;
    private readonly ISynchronousIoCanceller _canceller;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _current;
    private bool _timedOut;

    public SourceIoGuard(TimeProvider time, SourceIoOptions options, Action onAbandon, ISynchronousIoCanceller canceller, CancellationToken callToken)
    {
        _time = time;
        _options = options;
        _callToken = callToken;
        _onAbandon = onAbandon;
        _canceller = canceller;
    }

    /// <summary>Token de la llamada (cancelada por el cliente o al cerrar la conexión).</summary>
    public CancellationToken CallToken => _callToken;

    /// <summary>Número de operaciones hechas (para pruebas).</summary>
    public int Operations { get; private set; }

    /// <summary>
    /// Ejecuta una operación bloqueante con plazo. <paramref name="operation"/> recibe un token que se cancela a la vez que
    /// la E/S del hilo (los dobles lo usan para simular una operación colgada).
    /// </summary>
    /// <exception cref="SourceIoTimeoutException">Ha vencido el plazo (el resultado de la operación se descarta).</exception>
    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public T Run<T>(Func<CancellationToken, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _callToken.ThrowIfCancellationRequested();
        Operations++;
        using var operationCts = new CancellationTokenSource();
        lock (_lock)
        {
            _current = operationCts;
            _timedOut = false;
        }

        T result;
        using (var abandon = _time.CreateTimer(_ => Abandon(operationCts), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
        using (_time.CreateTimer(_ => Expire(operationCts, abandon), null, _options.OperationTimeout, Timeout.InfiniteTimeSpan))
        using (_callToken.Register(() => CancelIfCurrent(operationCts)))
        {
            try
            {
                result = operation(operationCts.Token);
            }
            finally
            {
                lock (_lock)
                {
                    _current = null;
                }
            }
        }

        bool timedOut;
        lock (_lock)
        {
            timedOut = _timedOut;
        }

        if (timedOut)
        {
            throw new SourceIoTimeoutException(abandoned: false);
        }

        _callToken.ThrowIfCancellationRequested();
        return result;
    }

    public void Dispose() => _canceller.Dispose();

    private void Expire(CancellationTokenSource operation, ITimer abandon)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_current, operation))
            {
                return;
            }

            _timedOut = true;
            CancelLocked(operation);
        }

        _ = abandon.Change(_options.CancellationGrace, Timeout.InfiniteTimeSpan);
    }

    private void Abandon(CancellationTokenSource operation)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_current, operation))
            {
                return;
            }
        }

        _onAbandon();
    }

    private void CancelIfCurrent(CancellationTokenSource operation)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, operation))
            {
                CancelLocked(operation);
            }
        }
    }

    private void CancelLocked(CancellationTokenSource operation)
    {
        _canceller.Cancel();
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La operación ya ha terminado.
        }
    }
}

/// <summary>
/// Ejecuta la resolución de orígenes en un hilo de trabajo propio, fuera del hilo RPC (§8.3 "Sin bloquear el hilo RPC",
/// MEDIA-1): como mucho <see cref="SourceIoOptions.MaxConcurrentValidations"/> a la vez en todo el servicio para la
/// validación (<c>ValidateJobAsync</c>, <c>SaveJobAsync</c>); el <c>JobRunner</c> no pasa por ese límite.
/// </summary>
/// <remarks>
/// La plaza la libera el propio hilo al terminar, así que un hilo abandonado (una operación que no vuelve ni tras
/// cancelarla) la sigue ocupando hasta que vuelva: un filtro colgado bloquea como mucho las validaciones, no el servicio
/// (R12). El que espera vuelve al cancelarse su llamada o al abandonarse el hilo.
/// </remarks>
internal sealed class SourceValidationRunner : IDisposable
{
    private readonly TimeProvider _time;
    private readonly SourceIoOptions _options;
    private readonly Func<ISynchronousIoCanceller> _cancellerFactory;
    private readonly SemaphoreSlim _slots;

    public SourceValidationRunner(TimeProvider time, SourceIoOptions options)
        : this(time, options, ThreadSynchronousIoCanceller.ForCurrentThread)
    {
    }

    internal SourceValidationRunner(TimeProvider time, SourceIoOptions options, Func<ISynchronousIoCanceller> cancellerFactory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentValidations, 1);
        _time = time;
        _options = options;
        _cancellerFactory = cancellerFactory;
        _slots = new SemaphoreSlim(options.MaxConcurrentValidations, options.MaxConcurrentValidations);
    }

    /// <summary>Plazas libres (para pruebas y diagnóstico).</summary>
    public int AvailableSlots => _slots.CurrentCount;

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Ejecuta <paramref name="work"/> en un hilo nuevo con un <see cref="SourceIoGuard"/>. Con <paramref name="limited"/>,
    /// espera antes una plaza con <paramref name="ct"/>.
    /// </summary>
    /// <exception cref="SourceIoTimeoutException">Con <see cref="SourceIoTimeoutException.Abandoned"/>: el hilo se ha abandonado.</exception>
    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public async Task<T> RunAsync<T>(Func<SourceIoGuard, T> work, bool limited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (limited)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var guard = new SourceIoGuard(
                    _time, _options, () => completion.TrySetException(new SourceIoTimeoutException(abandoned: true)), _cancellerFactory(), ct);
                completion.TrySetResult(work(guard));
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                if (limited)
                {
                    Release();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Backbone source resolution",
        };

        try
        {
            thread.Start();
        }
        catch
        {
            if (limited)
            {
                Release();
            }

            throw;
        }

        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void Release()
    {
        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // Un hilo abandonado que vuelve después de detener el servicio.
        }
    }
}
