using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Os;

/// <summary>Mitigaciones del proceso del servicio que se activan al arrancar; simulable (no se aplican al proceso de pruebas).</summary>
internal interface IProcessMitigations
{
    /// <summary>
    /// Activa Redirection Guard (<c>SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy)</c> con
    /// <c>EnforceRedirectionTrust</c>, §8.3, ADR-20): el proceso deja de seguir <i>junctions</i> creados por usuarios no
    /// administradores.
    /// </summary>
    /// <exception cref="Win32Exception">El sistema no lo admite o la llamada falla.</exception>
    void EnableRedirectionGuard();
}

/// <summary><see cref="IProcessMitigations"/> real.</summary>
internal sealed class WindowsProcessMitigations : IProcessMitigations
{
    public void EnableRedirectionGuard()
    {
        var flags = NativeMethods.Resolution.EnforceRedirectionTrust;
        if (!NativeMethods.Resolution.SetProcessMitigationPolicy(NativeMethods.Resolution.ProcessRedirectionTrustPolicy, in flags, sizeof(uint)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

/// <summary>
/// Primer servicio alojado: activa Redirection Guard antes de leer ningún origen (§8.3, decisión de la revisión de seguridad
/// de HU-07). Es una defensa complementaria, no un sustituto de la resolución controlada: si falla, se registra un aviso y el
/// servicio sigue.
/// </summary>
internal sealed partial class ProcessMitigationStartup(IProcessMitigations mitigations, ILogger<ProcessMitigationStartup> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            mitigations.EnableRedirectionGuard();
            LogEnabled(logger);
        }
        catch (Exception ex) when (ex is Win32Exception or EntryPointNotFoundException)
        {
            LogNotEnabled(logger, ex.Message);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Redirection Guard activado: el servicio no sigue junctions creados por usuarios no administradores.")]
    private static partial void LogEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido activar Redirection Guard ({Reason}); el servicio sigue (la protección principal es la resolución controlada, ADR-20).")]
    private static partial void LogNotEnabled(ILogger logger, string reason);
}
