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
    /// El servicio no ha podido arrancar (pipe ya existente, carpeta de datos insegura…). <b>Pendiente de añadir a §8.5</b>:
    /// la especificación exige registrarlo (§7.1 A-1, HT-03 I-1) pero no le asigna identificador.
    /// </summary>
    public const int ServiceStartFailed = 1002;
}

/// <summary>
/// Escritura en el registro Aplicación con el origen <c>Backbone</c> (RF-54). Nunca lanza: si el origen no existe (servicio
/// sin instalar, modo consola) lo anota en el log y sigue.
/// </summary>
internal interface IEventLogWriter
{
    void Write(EventLogLevel level, int eventId, string message);
}
