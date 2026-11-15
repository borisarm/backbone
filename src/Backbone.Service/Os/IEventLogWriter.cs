namespace Backbone.Service.Os;

/// <summary>Nivel de una entrada del Visor de eventos.</summary>
internal enum EventLogLevel
{
    Information = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Identificadores del Visor de eventos (§8.5).</summary>
internal static class BackboneEventIds
{
    /// <summary>Servicio iniciado (versión).</summary>
    public const int ServiceStarted = 1000;

    /// <summary>Servicio detenido.</summary>
    public const int ServiceStopped = 1001;

    /// <summary>
    /// Error: el servicio no arranca (pipe IPC ya existente o inaccesible, carpeta de datos insegura que no se puede corregir
    /// ni apartar, <c>settings.json</c> rechazado por propietario no fiable o punto de reanálisis).
    /// </summary>
    public const int ServiceStartFailed = 1002;

    /// <summary>
    /// Advertencia: la carpeta de datos tenía un propietario no fiable y se ha apartado como <c>Backbone.untrusted-&lt;guid&gt;</c>
    /// y sustituido por una nueva (el mensaje lleva la ruta apartada; §8.3).
    /// </summary>
    public const int DataDirectoryReplaced = 1003;
}

/// <summary>
/// Escritura en el registro Aplicación con el origen <c>Backbone</c> (RF-54). Nunca lanza: si el origen no existe (servicio
/// sin instalar, modo consola) lo anota en el log y sigue.
/// </summary>
internal interface IEventLogWriter
{
    void Write(EventLogLevel level, int eventId, string message);
}
