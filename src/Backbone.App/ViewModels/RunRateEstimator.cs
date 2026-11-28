namespace Backbone.App.ViewModels;

/// <summary>
/// Velocidad media móvil de una ejecución (HU-19): se usa cuando el evento <c>RunProgress</c> no trae <c>BytesPerSecond</c>.
/// El tiempo sale de <see cref="TimeProvider"/>, así que es determinista en pruebas.
/// </summary>
internal sealed class RunRateEstimator
{
    /// <summary>Ventana de la media móvil.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private long _runId = -1;

    public RunRateEstimator(TimeProvider time) => _time = time;

    /// <summary>Bytes por segundo en la ventana; <see langword="null"/> si aún no hay dos muestras separadas en el tiempo.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = (last.At - first.At).TotalSeconds;
            return seconds > 0 ? Math.Max(0, last.Bytes - first.Bytes) / seconds : null;
        }
    }

    /// <summary>Registra una muestra. Una ejecución distinta o un contador que retrocede reinician la ventana.</summary>
    /// <param name="runId">Ejecución.</param>
    /// <param name="bytesProcessed">Bytes procesados hasta ahora.</param>
    public void Add(long runId, long bytesProcessed)
    {
        if (runId != _runId || (_samples.Count > 0 && bytesProcessed < _samples.Last().Bytes))
        {
            _samples.Clear();
            _runId = runId;
        }

        var now = _time.GetUtcNow();
        _samples.Enqueue((now, bytesProcessed));
        while (_samples.Count > 2 && now - _samples.Peek().At > Window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Olvida las muestras (fin de la ejecución).</summary>
    public void Reset()
    {
        _samples.Clear();
        _runId = -1;
    }
}
