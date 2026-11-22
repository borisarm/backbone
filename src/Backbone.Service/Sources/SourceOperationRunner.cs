using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Core.Engine;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using R = Backbone.Service.Os.NativeMethods.Resolution;

namespace Backbone.Service.Sources;

/// <summary>Plazos de la lectura de los orígenes (§8.2 "Plazo de las lecturas de los orígenes"). Las pruebas los acortan.</summary>
internal sealed record SourceReadOptions
{
    public static SourceReadOptions Default { get; } = new();

    /// <summary>Plazo de cada apertura, llamada de enumeración, consulta o bloque leído: 60 s.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Lo que se espera a que vuelva una operación cancelada antes de abandonarla: 30 s.</summary>
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cada cuánto se repite la cancelación mientras no vuelve (la E/S puede empezar después del primer intento).</summary>
    public TimeSpan RecancelInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tamaño máximo de un bloque leído (el búfer propio del flujo).</summary>
    public int ReadBlockBytes { get; init; } = 1024 * 1024;

    /// <summary>Una operación que dura más es lenta y suma al presupuesto del origen (M-1): 10 s.</summary>
    public TimeSpan SlowOperationThreshold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tiempo acumulado en operaciones lentas que agota el presupuesto del origen: 10 min.</summary>
    public TimeSpan SlowOperationBudget { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Plazos vencidos que agotan el presupuesto del origen: 5.</summary>
    public int MaxTimedOutOperationsPerSource { get; init; } = 5;

    /// <summary>Handles de carpeta en la caché de una ejecución, aparte de las raíces: 128.</summary>
    public int DirectoryCacheCapacity { get; init; } = 128;
}

/// <summary>
/// Ejecuta las operaciones síncronas sobre los orígenes (aperturas, enumeraciones, consultas sobre el handle) en un
/// <b>hilo dedicado de lectura</b> con plazo, y las lecturas asíncronas con plazo (§8.2 "Plazo de las lecturas de los
/// orígenes", "Token de lectura de los orígenes", R12).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// El hilo se crea con <c>new Thread</c> y <c>UnsafeStart</c> (sin el <c>ExecutionContext</c> de quien lo pide), lleva la
/// copia del token del proceso con <c>SeBackupPrivilege</c> (<see cref="SourceReadToken"/>, ADR-21), solo ejecuta los
/// trabajos síncronos de su cola y llama a <c>RevertToSelf</c> al terminar (<see cref="SourceReaderThread"/>).
/// </description></item>
/// <item><description>
/// Al vencer el plazo (o cancelarse el <see cref="CancellationToken"/> de la ejecución) se cancela la operación: en el hilo,
/// con <c>CancelSynchronousIo</c> (y <c>CancelIoEx</c> sobre el handle si la operación tiene uno); en las lecturas, con
/// <c>CancelIoEx</c>. La cancelación se repite cada <see cref="SourceReadOptions.RecancelInterval"/> mientras la operación
/// no vuelve, porque <c>CancelSynchronousIo</c> solo cancela la E/S que está pendiente en ese momento.
/// </description></item>
/// <item><description>
/// Si vuelve: con el plazo vencido, <see cref="SourceAccessException"/> con <see cref="RunIssueCode.ReadError"/> (el motor
/// omite el elemento y conserva la versión del padre, RF-25); con la ejecución cancelada,
/// <see cref="OperationCanceledException"/>. Lo que devuelva tarde (un handle) se libera. Cada operación lenta suma al
/// <see cref="SourceBudget"/> de su origen; con el presupuesto agotado, la operación lanza sin E/S.
/// </description></item>
/// <item><description>
/// Si tampoco vuelve en <see cref="SourceReadOptions.CancellationGrace"/>: se registra, el hilo (o la lectura) se abandona,
/// cuenta en <see cref="AbandonedSourceThreads"/> hasta que vuelva, y esta operación y todas las siguientes lanzan
/// <see cref="SourceOperationAbandonedException"/>, que no es un error de un elemento (no deriva de
/// <see cref="IOException"/>): la ejecución termina <c>Failed</c>/<c>Internal</c>.
/// </description></item>
/// </list>
/// El hilo se crea con el ejecutor (uno por <see cref="SourceFileSystem"/>, es decir, por ejecución) y ejecuta las
/// operaciones de una en una; el plazo cuenta desde que la operación empieza en el hilo.
/// </remarks>
internal sealed partial class SourceOperationRunner : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly Lock _threadHandleLock = new();
    private readonly TimeProvider _time;
    private readonly SourceReadOptions _options;
    private readonly ILogger _logger;
    private readonly AbandonedSourceThreads? _abandonedThreads;
    private SafeWaitHandle? _threadHandle;
    private string? _abandoned;
    private int _disposed;

    public SourceOperationRunner(
        TimeProvider time,
        SourceReadOptions options,
        ILogger logger,
        SourceReadToken? token = null,
        AbandonedSourceThreads? abandonedThreads = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _options = options;
        _logger = logger;
        _abandonedThreads = abandonedThreads;
        SourceReaderThread.Start("Backbone source reader", token ?? SourceReadToken.Unprivileged, Work, logger);
    }

    /// <summary>Se ha abandonado una operación: la lectura de los orígenes de esta ejecución no puede seguir.</summary>
    public bool IsAbandoned => Volatile.Read(ref _abandoned) is not null;

    public SourceReadOptions Options => _options;

    public TimeProvider Time => _time;

    /// <summary>Lanza <see cref="SourceOperationAbandonedException"/> si se ha abandonado alguna operación.</summary>
    public void ThrowIfAbandoned()
    {
        var abandoned = Volatile.Read(ref _abandoned);
        if (abandoned is not null)
        {
            throw new SourceOperationAbandonedException(abandoned);
        }
    }

    /// <summary>
    /// Ejecuta <paramref name="operation"/> en el hilo dedicado con plazo. <paramref name="target"/> es el handle sobre el que
    /// trabaja, si lo hay, para cancelarlo también con <c>CancelIoEx</c>. El token que recibe la operación se cancela al
    /// cancelarla (lo usan los simuladores; la operación real la desbloquea <c>CancelSynchronousIo</c>).
    /// </summary>
    /// <param name="description">Qué se hace, para los registros.</param>
    /// <param name="path">Ruta afectada, para los registros (texto no confiable).</param>
    /// <param name="budget">Presupuesto de plazos del origen; agotado, lanza sin hacer E/S.</param>
    /// <param name="target">Handle sobre el que trabaja la operación, si lo hay.</param>
    /// <param name="operation">Operación síncrona.</param>
    /// <param name="cancellationToken">Cancelación de la ejecución.</param>
    public async Task<T> RunAsync<T>(
        string description,
        string path,
        SourceBudget? budget,
        ISourceHandle? target,
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfAbandoned();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        budget?.ThrowIfExhausted();

        var item = new WorkItem<T>(operation, target, _time);
        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            ThrowIfAbandoned();
            throw new ObjectDisposedException(nameof(SourceOperationRunner));
        }

        var abandoned = Volatile.Read(ref _abandoned);
        if (abandoned is not null)
        {
            item.FailIfNotStarted(new SourceOperationAbandonedException(abandoned));
        }

        bool timedOut;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Espera a que empiece (de una en una). Si la ejecución se cancela antes, no llega a empezar.
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, _time, wait.Token);
            if (await Task.WhenAny(item.Started, cancelled).ConfigureAwait(false) != item.Started && item.TryCancelBeforeStart())
            {
                await wait.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }

            await item.Started.ConfigureAwait(false); // propaga el abandono si se vació la cola sin ejecutarla

            var deadline = Task.Delay(_options.OperationTimeout, _time, wait.Token);
            var winner = await Task.WhenAny(item.Completion, deadline).ConfigureAwait(false);
            if (winner == item.Completion || item.Completion.IsCompleted)
            {
                await wait.CancelAsync().ConfigureAwait(false);
                budget?.Record(item.Elapsed, timedOut: false);
                return await item.Completion.ConfigureAwait(false);
            }

            timedOut = deadline.Status == TaskStatus.RanToCompletion;
            await wait.CancelAsync().ConfigureAwait(false);
        }

        await CancelAndWaitAsync(item, description, path).ConfigureAwait(false);
        budget?.Record(item.Elapsed, timedOut);
        if (!timedOut)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw TimedOut(description);
    }

    /// <summary>Lee un bloque de <paramref name="handle"/> con plazo (E/S asíncrona, cancelada con <c>CancelIoEx</c>).</summary>
    public async Task<int> ReadAsync(ISourceHandle handle, Memory<byte> buffer, long fileOffset, string path, SourceBudget? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ThrowIfAbandoned();
        cancellationToken.ThrowIfCancellationRequested();
        budget?.ThrowIfExhausted();

        var start = _time.GetTimestamp();
        using var timeout = new CancellationTokenSource(_options.OperationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        Task<int> read;
        try
        {
            read = handle.ReadAsync(buffer, fileOffset, linked.Token).AsTask();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            read = Task.FromCanceled<int>(linked.Token);
        }

        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, _time, linked.Token);
        var winner = await Task.WhenAny(read, cancelled).ConfigureAwait(false);
        if (winner == read && !(read.IsCanceled && linked.IsCancellationRequested))
        {
            budget?.Record(_time.GetElapsedTime(start), timedOut: false);
            return await read.ConfigureAwait(false);
        }

        var timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

        // Plazo vencido o ejecución cancelada: el token ya ha pedido CancelIoEx; se pide otra vez por si acaso.
        if (!read.IsCompleted)
        {
            handle.CancelIo();
            var grace = Task.Delay(_options.CancellationGrace, _time, CancellationToken.None);
            if (await Task.WhenAny(read, grace).ConfigureAwait(false) != read)
            {
                // La lectura abandonada cuenta como un hilo abandonado hasta que el controlador responda (R12).
                _abandonedThreads?.OnAbandoned(path);
                _ = read.ContinueWith(
                    static (t, state) =>
                    {
                        _ = t.Exception;
                        ((AbandonedSourceThreads?)state)?.OnReturned();
                    },
                    _abandonedThreads,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Abandon("lectura de un bloque", path);
                ThrowIfAbandoned();
            }
        }

        ObserveLater(read);
        budget?.Record(_time.GetElapsedTime(start), timedOut);
        cancellationToken.ThrowIfCancellationRequested();
        throw TimedOut("lectura de un bloque");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // No se espera al hilo (puede estar abandonado): termina solo al vaciarse la cola.
        _queue.CompleteAdding();
    }

    private static void ObserveLater(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private SourceAccessException TimedOut(string description) => new(
        RunIssueCode.ReadError,
        $"Se superó el plazo de {_options.OperationTimeout.TotalSeconds:0} s ({description}); la operación se canceló y se conserva la versión anterior si la había.");

    private async Task CancelAndWaitAsync(WorkItem item, string description, string path)
    {
        item.RequestCancel(this);
        using var recancel = _time.CreateTimer(_ => item.RequestCancel(this), null, _options.RecancelInterval, _options.RecancelInterval);
        var grace = Task.Delay(_options.CancellationGrace, _time, CancellationToken.None);
        var returned = await Task.WhenAny(item.CompletionUntyped, grace).ConfigureAwait(false) == item.CompletionUntyped;

        // Lo que devuelva cuando vuelva (un handle) se libera y su error, esperado tras cancelar, se descarta.
        item.DisposeResultWhenDone();
        if (!returned && item.TryMarkAbandoned())
        {
            _abandonedThreads?.OnAbandoned(path);
            Abandon(description, path);
            ThrowIfAbandoned();
        }
    }

    private void Abandon(string description, string path)
    {
        var message = $"Una operación sobre un origen ({description}) no ha vuelto en {(_options.OperationTimeout + _options.CancellationGrace).TotalSeconds:0} s, ni tras cancelarla; se abandona su hilo y la copia no puede seguir.";
        if (Interlocked.CompareExchange(ref _abandoned, message, null) is not null)
        {
            return;
        }

        LogAbandoned(_logger, description, UntrustedText.Describe(path), _options.OperationTimeout.TotalSeconds, _options.CancellationGrace.TotalSeconds);
        while (_queue.TryTake(out var pending))
        {
            pending.FailIfNotStarted(new SourceOperationAbandonedException(message));
        }

        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Work()
    {
        // CancelSynchronousIo exige un handle real del hilo con THREAD_TERMINATE.
        var handle = R.OpenThread(R.ThreadTerminate, inheritHandle: false, R.GetCurrentThreadId());
        lock (_threadHandleLock)
        {
            _threadHandle = handle;
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (item.TryStart())
                {
                    item.Execute();
                    if (item.MarkReturned())
                    {
                        // El hilo abandonado ha vuelto por fin: deja de contar (R12). Lo que haya abierto ya se ha liberado.
                        _abandonedThreads?.OnReturned();
                    }
                }
            }
        }
        finally
        {
            lock (_threadHandleLock)
            {
                _threadHandle = null;
                handle.Dispose();
            }
        }
    }

    private void CancelSynchronousIoOfWorker()
    {
        lock (_threadHandleLock)
        {
            if (_threadHandle is { IsInvalid: false, IsClosed: false } thread)
            {
                _ = R.CancelSynchronousIo(thread); // ERROR_NOT_FOUND si no había E/S pendiente: se reintentará
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "La operación sobre el origen ({Operation}, {Path}) no ha vuelto tras {Timeout} s de plazo y {Grace} s más tras cancelarla: se abandona su hilo y la ejecución terminará con error interno (R12).")]
    private static partial void LogAbandoned(ILogger logger, string operation, string path, double timeout, double grace);

    /// <summary>Una operación en la cola. Su estado se cambia bajo <see cref="Gate"/>.</summary>
    private abstract class WorkItem(ISourceHandle? target, TimeProvider time) : IDisposable
    {
        private const int Running = 0;
        private const int Returned = 1;
        private const int AbandonedState = 2;

        private readonly CancellationTokenSource _signal = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _running;
        private bool _done;
        private int _returnState;
        private long _start;
        private long _end;

        public Task Started => _started.Task;

        public abstract Task CompletionUntyped { get; }

        /// <summary>Duración de la operación en el hilo (desde que empezó hasta que volvió).</summary>
        public TimeSpan Elapsed
        {
            get
            {
                var start = Volatile.Read(ref _start);
                var end = Volatile.Read(ref _end);
                return start == 0 ? TimeSpan.Zero : time.GetElapsedTime(start, end == 0 ? time.GetTimestamp() : end);
            }
        }

        protected Lock Gate { get; } = new();

        protected CancellationToken Signal => _signal.Token;

        public bool TryStart()
        {
            lock (Gate)
            {
                if (_done)
                {
                    return false;
                }

                _running = true;
            }

            Volatile.Write(ref _start, time.GetTimestamp());
            _started.TrySetResult();
            return true;
        }

        public bool TryCancelBeforeStart()
        {
            lock (Gate)
            {
                if (_running || _done)
                {
                    return false;
                }

                _done = true;
            }

            SetCanceled();
            _started.TrySetCanceled();
            return true;
        }

        public void FailIfNotStarted(Exception exception)
        {
            lock (Gate)
            {
                if (_running || _done)
                {
                    return;
                }

                _done = true;
            }

            SetException(exception);
            _started.TrySetException(exception);
        }

        /// <summary>El que espera la da por abandonada; <see langword="false"/> si ya había vuelto (no se abandona).</summary>
        public bool TryMarkAbandoned() => Interlocked.CompareExchange(ref _returnState, AbandonedState, Running) == Running;

        /// <summary>El hilo ha vuelto de la operación; <see langword="true"/> si se había dado por abandonada.</summary>
        public bool MarkReturned() => Interlocked.CompareExchange(ref _returnState, Returned, Running) == AbandonedState;

        public void RequestCancel(SourceOperationRunner runner)
        {
            lock (Gate)
            {
                if (!_running || _done)
                {
                    return;
                }

                // Bajo el cerrojo: el hilo no puede marcar esta operación como terminada (y pasar a la siguiente) mientras
                // se cancela, así que CancelSynchronousIo nunca alcanza a otra operación.
                if (!_signal.IsCancellationRequested)
                {
                    _signal.Cancel();
                }

                runner.CancelSynchronousIoOfWorker();
                target?.CancelIo();
            }
        }

        public abstract void Execute();

        /// <summary>Libera la señal de cancelación; solo se llama al terminar la operación (bajo <see cref="Gate"/>).</summary>
        public void Dispose() => _signal.Dispose();

        public abstract void DisposeResultWhenDone();

        protected void MarkDone()
        {
            Volatile.Write(ref _end, time.GetTimestamp());
            lock (Gate)
            {
                _running = false;
                _done = true;
                Dispose(); // RequestCancel ya no toca la señal: comprueba _done bajo el mismo cerrojo
            }
        }

        protected abstract void SetException(Exception exception);

        protected abstract void SetCanceled();
    }

    private sealed class WorkItem<T>(Func<CancellationToken, T> operation, ISourceHandle? target, TimeProvider time) : WorkItem(target, time)
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public override Task CompletionUntyped => _completion.Task;

        public override void Execute()
        {
            T result;
            try
            {
                result = operation(Signal);
            }
#pragma warning disable CA1031 // Cualquier excepción de la operación se traslada al llamante.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                MarkDone();
                _completion.TrySetException(ex);
                return;
            }

            MarkDone();
            _completion.TrySetResult(result);
        }

        public override void DisposeResultWhenDone() => _ = DisposeLateResultAsync();

        private async Task DisposeLateResultAsync()
        {
            T result;
            try
            {
                result = await _completion.Task.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // El error de una operación ya cancelada se descarta.
            catch (Exception)
#pragma warning restore CA1031
            {
                return;
            }

            (result as IDisposable)?.Dispose();
        }

        protected override void SetException(Exception exception) => _completion.TrySetException(exception);

        protected override void SetCanceled() => _completion.TrySetCanceled();
    }
}
