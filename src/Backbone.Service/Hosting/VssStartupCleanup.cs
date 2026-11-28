using Backbone.Service.Os;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Hosting;

/// <summary>
/// Al arrancar, elimina las instantáneas VSS que Backbone creó en una ejecución anterior y no llegó a eliminar (HU-15
/// criterio 3, §8.2 <c>VssSnapshotter</c>): solo las registradas en <c>data\vss-snapshots.json</c>, nunca las de otro programa.
/// </summary>
/// <remarks>
/// Corre <b>en segundo plano</b>: si VSS tarda o no responde, no retrasa el arranque del servicio (el SCM espera 30 s) ni la
/// creación del pipe. No interfiere con una ejecución que empiece mientras tanto: la limpieza solo toca el registro de otras
/// instancias del servicio, y la ejecución crea y registra lo suyo con la instancia actual. Nunca lanza.
/// </remarks>
internal sealed partial class VssStartupCleanup(IVolumeSnapshotter snapshotter, ILogger<VssStartupCleanup> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task _task = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _task = Task.Run(CleanAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // La parada se agotó esperando: lo que quede lo reintenta el siguiente arranque.
        }
    }

    /// <summary>La limpieza. Visible para las pruebas.</summary>
    internal async Task CleanAsync()
    {
        try
        {
            var deleted = await snapshotter.DeleteOrphansAsync(_stopping.Token).ConfigureAwait(false);
            if (deleted > 0)
            {
                LogDeleted(logger, deleted);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Servicio parándose.
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex.GetType().Name);
        }
    }

    public void Dispose() => _stopping.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Se han eliminado {Count} instantáneas VSS huérfanas de Backbone.")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se han podido limpiar las instantáneas VSS huérfanas ({ErrorType}); se reintentará al arrancar.")]
    private static partial void LogFailed(ILogger logger, string errorType);
}
