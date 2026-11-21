using Backbone.Service.Sources;
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

    /// <summary>Una operación que dura más que esto es lenta y suma al presupuesto (§8.2 "Presupuesto", §8.3 paso 4).</summary>
    public TimeSpan SlowOperationThreshold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tiempo acumulado en operaciones lentas que agota el presupuesto: 10 min.</summary>
    public TimeSpan SlowOperationBudget { get; init; } = TimeSpan.FromMinutes(10);
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
/// para no cancelar por error la siguiente (Microsoft Learn, "Canceling Pending I/O Operations"). Lo que devuelva tarde una
/// operación cuyo plazo ha vencido (un handle) se libera (I-1). Las operaciones de más de
/// <see cref="SourceIoOptions.SlowOperationThreshold"/> suman; con <see cref="SourceIoOptions.SlowOperationBudget"/>
/// acumulados, la siguiente lanza <see cref="SourceIoTimeoutException"/> (§8.3 paso 4).
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
    private TimeSpan _slow;
    private int _abandoned;

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

    /// <summary>Se ha abandonado el hilo: lo que quede de trabajo no debe seguir (I-2).</summary>
    public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

    /// <summary>Ruta que se está tratando, para el log y el evento 1005 (texto no confiable).</summary>
    public string CurrentPath { get; set; } = string.Empty;

    /// <summary>
    /// Ejecuta una operación bloqueante con plazo. <paramref name="operation"/> recibe un token que se cancela a la vez que
    /// la E/S del hilo (los dobles lo usan para simular una operación colgada).
    /// </summary>
    /// <exception cref="SourceIoTimeoutException">Ha vencido el plazo, o se ha agotado el presupuesto de operaciones lentas.</exception>
    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public T Run<T>(Func<CancellationToken, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _callToken.ThrowIfCancellationRequested();
        if (IsAbandoned)
        {
            throw new SourceIoTimeoutException(abandoned: true);
        }

        if (_slow >= _options.SlowOperationBudget)
        {
            throw new SourceIoTimeoutException("The source has exhausted its budget of slow operations.");
        }

        Operations++;
        using var operationCts = new CancellationTokenSource();
        lock (_lock)
        {
            _current = operationCts;
            _timedOut = false;
        }

        var start = _time.GetTimestamp();
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

        var elapsed = _time.GetElapsedTime(start);
        if (elapsed > _options.SlowOperationThreshold)
        {
            _slow += elapsed;
        }

        bool timedOut;
        lock (_lock)
        {
            timedOut = _timedOut;
        }

        if (timedOut || IsAbandoned)
        {
            // I-1: lo que vuelve tarde (un handle abierto) se cierra sin usarlo.
            (result as IDisposable)?.Dispose();
            throw new SourceIoTimeoutException(abandoned: IsAbandoned);
        }

        if (_callToken.IsCancellationRequested)
        {
            (result as IDisposable)?.Dispose();
            _callToken.ThrowIfCancellationRequested();
        }

        return result;
    }

    /// <summary>Empieza otra resolución: el presupuesto de operaciones lentas es por resolución (§8.3 paso 4).</summary>
    public void ResetSlowBudget() => _slow = TimeSpan.Zero;

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

        if (Interlocked.Exchange(ref _abandoned, 1) == 0)
        {
            _onAbandon();
        }
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
/// <list type="bullet">
/// <item><description>
/// El hilo es un hilo dedicado de lectura (ADR-21): <c>UnsafeStart</c>, con el token de <see cref="SourceReadToken"/> y
/// <c>RevertToSelf</c> al terminar (<see cref="SourceReaderThread"/>).
/// </description></item>
/// <item><description>
/// La plaza la libera el propio hilo al terminar, así que un hilo abandonado (una operación que no vuelve ni tras
/// cancelarla) la sigue ocupando hasta que vuelva: un filtro colgado bloquea como mucho las validaciones, no el servicio
/// (R12). Cada hilo abandonado cuenta en <see cref="AbandonedSourceThreads"/>, también en el modo sin límite del
/// <c>JobRunner</c>; con 4, toda llamada lanza <see cref="SourceReadingStoppedException"/> sin abrir nada.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class SourceValidationRunner : IDisposable
{
    private readonly TimeProvider _time;
    private readonly SourceIoOptions _options;
    private readonly Func<ISynchronousIoCanceller> _cancellerFactory;
    private readonly SemaphoreSlim _slots;
    private readonly SourceReadToken _token;
    private readonly AbandonedSourceThreads _abandoned;

    public SourceValidationRunner(TimeProvider time, SourceIoOptions options, SourceReadToken? token = null, AbandonedSourceThreads? abandoned = null)
        : this(time, options, ThreadSynchronousIoCanceller.ForCurrentThread, token, abandoned)
    {
    }

    internal SourceValidationRunner(
        TimeProvider time,
        SourceIoOptions options,
        Func<ISynchronousIoCanceller> cancellerFactory,
        SourceReadToken? token = null,
        AbandonedSourceThreads? abandoned = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentValidations, 1);
        _time = time;
        _options = options;
        _cancellerFactory = cancellerFactory;
        _token = token ?? SourceReadToken.Unprivileged;
        _abandoned = abandoned ?? new AbandonedSourceThreads();
        _slots = new SemaphoreSlim(options.MaxConcurrentValidations, options.MaxConcurrentValidations);
    }

    /// <summary>Plazas libres (para pruebas y diagnóstico).</summary>
    public int AvailableSlots => _slots.CurrentCount;

    /// <summary>Contador de hilos abandonados que usa este ejecutor.</summary>
    public AbandonedSourceThreads Abandoned => _abandoned;

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// Ejecuta <paramref name="work"/> en un hilo nuevo con un <see cref="SourceIoGuard"/>. Con <paramref name="limited"/>,
    /// espera antes una plaza con <paramref name="ct"/>.
    /// </summary>
    /// <exception cref="SourceIoTimeoutException">Con <see cref="SourceIoTimeoutException.Abandoned"/>: el hilo se ha abandonado.</exception>
    /// <exception cref="SourceReadingStoppedException">Hay demasiados hilos abandonados en el servicio; no se ha abierto nada.</exception>
    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public async Task<T> RunAsync<T>(Func<SourceIoGuard, T> work, bool limited, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ThrowIfStopped();
        if (limited)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            ThrowIfStopped();
        }
        catch
        {
            if (limited)
            {
                Release();
            }

            throw;
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SourceReaderThread.Start("Backbone source resolution", _token, () => Work(work, limited, completion, ct));
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

    private void ThrowIfStopped()
    {
        if (_abandoned.IsExhausted)
        {
            throw new SourceReadingStoppedException();
        }
    }

    private void Work<T>(Func<SourceIoGuard, T> work, bool limited, TaskCompletionSource<T> completion, CancellationToken ct)
    {
        var abandoned = 0;
        SourceIoGuard? guardRef = null;
        try
        {
            using var guard = new SourceIoGuard(
                _time,
                _options,
                () =>
                {
                    Volatile.Write(ref abandoned, 1);
                    _abandoned.OnAbandoned(Volatile.Read(ref guardRef)?.CurrentPath ?? string.Empty);
                    completion.TrySetException(new SourceIoTimeoutException(abandoned: true));
                },
                _cancellerFactory(),
                ct);
            Volatile.Write(ref guardRef, guard);
            var result = work(guard);
            if (!completion.TrySetResult(result))
            {
                (result as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
#pragma warning disable CA1031 // Cualquier excepción del trabajo se traslada al llamante.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            completion.TrySetException(ex);
        }
        finally
        {
            if (Volatile.Read(ref abandoned) != 0)
            {
                _abandoned.OnReturned();
            }

            if (limited)
            {
                Release();
            }
        }
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
