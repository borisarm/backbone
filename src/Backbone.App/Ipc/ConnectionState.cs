namespace Backbone.App.Ipc;

/// <summary>Estado de la conexión de la app con el servicio (§9.1, §9.3).</summary>
public enum ConnectionState
{
    /// <summary>Primer intento de conexión en curso; aún no se sabe nada del servicio.</summary>
    Connecting = 0,

    /// <summary>Conectado, con contrato compatible y autorizado.</summary>
    Connected = 1,

    /// <summary>Se perdió una conexión que funcionaba y se está reintentando (§9.1).</summary>
    Reconnecting = 2,

    /// <summary>No hay servicio al otro lado del pipe (no instalado, detenido) o no es el servicio auténtico (§7.1 A-1).</summary>
    ServiceStopped = 3,

    /// <summary>El usuario no está autorizado: la ACL del pipe lo rechaza o el servicio responde <c>NotAuthorized</c> (§7.3, §9.3).</summary>
    Unauthorized = 4,

    /// <summary>El servicio habla otra versión mayor del contrato (§7.1).</summary>
    IncompatibleContract = 5,
}
