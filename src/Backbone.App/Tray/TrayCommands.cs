using Backbone.App.Ipc;
using Backbone.Contracts;
using StreamJsonRpc;

namespace Backbone.App.Tray;

/// <summary>Resultado de "Copiar todo ahora".</summary>
/// <param name="Started">Trabajos lanzados o encolados.</param>
/// <param name="AlreadyRunning">Trabajos que ya estaban en marcha (no es un error).</param>
/// <param name="Failed">Trabajos para los que el servicio devolvió otro error.</param>
/// <param name="Unavailable">No había conexión con el servicio.</param>
internal sealed record RunAllResult(int Started, int AlreadyRunning, int Failed, bool Unavailable);

/// <summary>Acciones del menú de la bandeja que hablan con el servicio. Solo usa el contrato: no hay lógica de copia aquí.</summary>
internal static class TrayCommands
{
    /// <summary>
    /// "Copiar todo ahora": <c>RunNowAsync</c> por cada trabajo activado sin ejecución en curso ni en cola
    /// (el contrato no tiene una operación "todos"; la cola la gestiona el servicio, RF-15).
    /// </summary>
    public static async Task<RunAllResult> RunAllNowAsync(IBackboneService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        int started = 0, running = 0, failed = 0;
        try
        {
            var status = await service.GetStatusAsync(ct).ConfigureAwait(false);
            foreach (var job in status.Jobs.Where(j => j.Enabled && j.Activity == JobActivity.Idle))
            {
                try
                {
                    await service.RunNowAsync(job.JobId, ct).ConfigureAwait(false);
                    started++;
                }
                catch (RemoteInvocationException ex)
                {
                    if (ConnectionFailures.TryGetErrorCode(ex, out var code, out _) && code == BackboneErrorCode.JobAlreadyRunning)
                    {
                        running++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
        }
        catch (ServiceUnavailableException)
        {
            return new(started, running, failed, Unavailable: true);
        }
        catch (RemoteInvocationException)
        {
            failed++;
        }

        return new(started, running, failed, Unavailable: false);
    }
}
