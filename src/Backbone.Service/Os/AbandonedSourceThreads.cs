using Backbone.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Os;

/// <summary>
/// Contador único, en todo el servicio, de hilos de lectura de orígenes abandonados que aún no han vuelto: los de
/// <c>SourceFileSystem</c> y los de la resolución controlada (validación, <c>SaveJobAsync</c>, inicio de cada ejecución,
/// cálculo de <c>PD</c>), §8.2 "Tope de hilos abandonados", R12.
/// </summary>
/// <remarks>
/// Con <see cref="Limit"/> o más, las ejecuciones nuevas y las validaciones fallan con <c>Internal</c> sin abrir nada
/// (<see cref="IsExhausted"/>) y se registra el evento 1005 una vez; vuelve a registrarse solo si el número baja de
/// <see cref="Limit"/> y vuelve a llegar.
/// </remarks>
internal sealed partial class AbandonedSourceThreads(IEventLogWriter? eventLog = null, ILogger<AbandonedSourceThreads>? logger = null)
{
    /// <summary>Hilos abandonados a partir de los cuales se detiene la lectura de orígenes.</summary>
    public const int Limit = 4;

    private readonly Lock _lock = new();
    private readonly ILogger _logger = logger ?? NullLogger<AbandonedSourceThreads>.Instance;
    private int _count;
    private bool _reported;

    /// <summary>Hilos abandonados que aún no han vuelto.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Hay <see cref="Limit"/> o más: no se lee ni se valida nada hasta reiniciar el servicio.</summary>
    public bool IsExhausted => Count >= Limit;

    /// <summary>Un hilo se ha abandonado al leer <paramref name="path"/> (texto no confiable: se sanea antes de registrarlo).</summary>
    public void OnAbandoned(string path)
    {
        int count;
        bool report;
        lock (_lock)
        {
            _count++;
            count = _count;
            report = count >= Limit && !_reported;
            if (report)
            {
                _reported = true;
            }
        }

        if (report)
        {
            var described = UntrustedText.Describe(path);
            LogStopped(_logger, count, described);
            eventLog?.Write(
                EventLogLevel.Error,
                BackboneEventIds.SourceReadingStopped,
                $"Backbone stopped reading sources: {count} source reading threads were abandoned because a driver did not honor cancellation (last path: {described}). Backups and job validations fail with an internal error until the Backbone service is restarted.");
        }
    }

    /// <summary>Un hilo abandonado ha vuelto por fin.</summary>
    public void OnReturned()
    {
        lock (_lock)
        {
            if (_count > 0)
            {
                _count--;
            }

            if (_count < Limit)
            {
                _reported = false;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Lectura de orígenes detenida: {Count} hilos abandonados (última ruta {Path}); hay que reiniciar el servicio (R12, evento 1005).")]
    private static partial void LogStopped(ILogger logger, int count, string path);
}
