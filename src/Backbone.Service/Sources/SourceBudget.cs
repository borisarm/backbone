using Backbone.Contracts;
using Backbone.Core.Engine;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Sources;

/// <summary>
/// Presupuesto de plazos de un origen en una ejecución (§8.2 "Presupuesto de plazos por origen y ejecución", M-1). Cuentan
/// las operaciones <b>lentas</b>: las que vencen el plazo y las que terminan después de
/// <see cref="SourceReadOptions.SlowOperationThreshold"/> (10 s). Se agota con
/// <see cref="SourceReadOptions.MaxTimedOutOperationsPerSource"/> (5) plazos vencidos o con
/// <see cref="SourceReadOptions.SlowOperationBudget"/> (10 min) acumulados en operaciones lentas (la duración completa de
/// cada una).
/// </summary>
/// <remarks>
/// Al agotarse se registra <b>un</b> aviso en el log (origen, plazos vencidos y tiempo acumulado) y, desde entonces, toda
/// operación sobre una ruta de ese origen lanza enseguida, sin E/S, <see cref="SourceAccessException"/> con
/// <see cref="RunIssueCode.ReadError"/> (el motor conserva la versión del padre, RF-25). La operación en curso sigue con su
/// propio plazo. No hay código de aviso nuevo: el contrato no cambia.
/// </remarks>
internal sealed partial class SourceBudget(string source, SourceReadOptions options, ILogger logger)
{
    /// <summary>Mensaje del aviso de cada elemento no leído por el presupuesto agotado.</summary>
    internal const string ExhaustedMessage = "No se ha leído: el origen ha agotado su presupuesto de plazos.";

    private readonly Lock _lock = new();
    private int _timedOut;
    private TimeSpan _slow;
    private int _exhausted;

    /// <summary>Plazos vencidos.</summary>
    public int TimedOut
    {
        get
        {
            lock (_lock)
            {
                return _timedOut;
            }
        }
    }

    /// <summary>Tiempo acumulado en operaciones lentas.</summary>
    public TimeSpan Slow
    {
        get
        {
            lock (_lock)
            {
                return _slow;
            }
        }
    }

    public bool IsExhausted => Volatile.Read(ref _exhausted) != 0;

    /// <summary>Lanza, sin E/S, si el presupuesto está agotado.</summary>
    /// <exception cref="SourceAccessException">Con <see cref="RunIssueCode.ReadError"/>.</exception>
    public void ThrowIfExhausted()
    {
        if (IsExhausted)
        {
            throw new SourceAccessException(RunIssueCode.ReadError, ExhaustedMessage);
        }
    }

    /// <summary>Anota una operación terminada (también una cuyo plazo venció y que volvió tras cancelarla).</summary>
    public void Record(TimeSpan duration, bool timedOut)
    {
        if (!timedOut && duration <= options.SlowOperationThreshold)
        {
            return;
        }

        int count;
        TimeSpan slow;
        lock (_lock)
        {
            if (timedOut)
            {
                _timedOut++;
            }

            _slow += duration;
            count = _timedOut;
            slow = _slow;
            if (_exhausted != 0 || (count < options.MaxTimedOutOperationsPerSource && slow < options.SlowOperationBudget))
            {
                return;
            }

            Volatile.Write(ref _exhausted, 1);
        }

        LogExhausted(logger, source, count, slow.TotalSeconds);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "El origen {Source} ha agotado su presupuesto de plazos ({TimedOut} plazos vencidos, {SlowSeconds:0} s en operaciones lentas): el resto del origen no se lee en esta copia y conserva la versión anterior (§8.2, M-1).")]
    private static partial void LogExhausted(ILogger logger, string source, int timedOut, double slowSeconds);
}
