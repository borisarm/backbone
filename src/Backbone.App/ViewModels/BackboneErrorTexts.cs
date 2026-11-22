using Backbone.App.Ipc;
using Backbone.Contracts;
using StreamJsonRpc;

namespace Backbone.App.ViewModels;

/// <summary>
/// Traduce cada <see cref="BackboneErrorCode"/> (§7.4) a un mensaje localizado con una acción sugerida. Nunca muestra
/// <see cref="BackboneErrorData.Message"/>. Claves <c>Error_*</c> de <c>Resources.resw</c>.
/// </summary>
internal sealed class BackboneErrorTexts(Func<string, string> getString)
{
    /// <summary>Clave del texto cuando no hay conexión con el servicio.</summary>
    public const string ServiceUnavailableKey = "Error_ServiceUnavailable";

    /// <summary>Clave de recurso del texto de un código.</summary>
    /// <param name="code">Código de negocio.</param>
    /// <returns>La clave del <c>.resw</c>.</returns>
    public static string KeyFor(BackboneErrorCode code) => "Error_" + code;

    /// <summary>Texto de un código de negocio.</summary>
    /// <param name="code">Código de negocio.</param>
    /// <returns>El mensaje con su acción sugerida.</returns>
    public string Translate(BackboneErrorCode code) => getString(KeyFor(code));

    /// <summary>Texto de una excepción de una llamada al servicio.</summary>
    /// <param name="exception">Excepción recibida.</param>
    /// <returns>El mensaje localizado.</returns>
    public string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ServiceUnavailableException => getString(ServiceUnavailableKey),
            RemoteInvocationException rpc when ConnectionFailures.TryGetErrorCode(rpc, out var code, out _) => Translate(code),
            _ => Translate(BackboneErrorCode.Internal),
        };
    }
}
