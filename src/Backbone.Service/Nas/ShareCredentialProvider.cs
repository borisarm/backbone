using Backbone.Contracts;
using Backbone.Service.Ipc;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// Credencial con la que se conecta a un destino (RF-60, HU-04 criterio 5). La usan la conexión al recurso
/// (<c>IShareConnector</c>, HU-05) y las ejecuciones, también las programadas sin sesión iniciada: solo lee
/// <see cref="ICredentialStore"/> (DPAPI con ámbito equipo), sin depender de ningún cliente IPC ni perfil de usuario.
/// </summary>
/// <remarks>
/// <b>Nunca se conecta sin credencial explícita</b>: si no hay credencial guardada utilizable para el recurso, falla con
/// <see cref="BackboneErrorCode.CredentialRequired"/> (nunca se recurre a la cuenta del equipo).
/// </remarks>
internal sealed partial class ShareCredentialProvider(ICredentialStore store, ILogger<ShareCredentialProvider> logger)
{
    /// <summary>Credencial guardada para el recurso de <paramref name="destination"/>; el llamante la desecha al conectar.</summary>
    /// <param name="destination">Destino UNC (<c>\\servidor\recurso[\ruta]</c>) o raíz del recurso.</param>
    /// <param name="ct">Cancelación.</param>
    /// <exception cref="BackboneException">
    /// <see cref="BackboneErrorCode.CredentialRequired"/> si no hay credencial; <see cref="BackboneErrorCode.ValidationFailed"/>
    /// si el destino no es UNC válido (<see cref="CredentialStore.TryNormalizeShareRoot"/>: también el propio equipo).
    /// </exception>
    public async Task<NasCredential> GetRequiredAsync(string destination, CancellationToken ct)
    {
        // Mismas reglas que IPC: también se rechazan el propio equipo y las suplentes sueltas.
        if (!CredentialStore.TryNormalizeShareRoot(destination, out var shareRoot, out var issue))
        {
            throw BackboneException.ValidationFailed(string.Empty, issue);
        }

        var credential = await store.GetAsync(shareRoot, ct).ConfigureAwait(false);
        if (credential is null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
            {
                LogMissing(logger, UntrustedText.Describe(shareRoot));
            }

            throw new BackboneException(BackboneErrorCode.CredentialRequired, "No credential is saved for the share.");
        }

        return credential;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No hay credencial guardada para el recurso {ShareRoot}: no se conecta (RF-60).")]
    private static partial void LogMissing(ILogger logger, string shareRoot);
}
