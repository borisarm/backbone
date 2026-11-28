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
    /// ni apartar, <c>settings.json</c> o <c>jobs.json</c> rechazados por propietario no fiable, punto de reanálisis, ser una
    /// carpeta o enlace duro).
    /// </summary>
    public const int ServiceStartFailed = 1002;

    /// <summary>
    /// Advertencia: la carpeta de datos tenía un propietario no fiable y se ha apartado como <c>Backbone.untrusted-&lt;guid&gt;</c>
    /// y sustituido por una nueva (el mensaje lleva la ruta apartada; §8.3).
    /// </summary>
    public const int DataDirectoryReplaced = 1003;

    /// <summary>
    /// Advertencia: <c>jobs.json</c> dañado (JSON no válido o más de 64 MiB); el servicio arranca sin trabajos y lo apartará
    /// como <c>jobs.json.bad-&lt;marca UTC&gt;</c> en la siguiente escritura (§8.2, §8.5).
    /// </summary>
    public const int JobsFileCorrupt = 1004;

    /// <summary>
    /// Error: lectura de orígenes detenida; hay 4 o más hilos de lectura abandonados porque un controlador no atendió la
    /// cancelación. Las copias y validaciones fallan con <c>Internal</c> hasta reiniciar el servicio (§8.2, R12).
    /// </summary>
    public const int SourceReadingStopped = 1005;

    /// <summary>
    /// Advertencia: VSS no está disponible (no ha podido crear las instantáneas); la copia continúa en vivo y termina con
    /// avisos (RF-22, §8.4 paso 5). El mensaje lleva el trabajo y el código de error de VSS.
    /// </summary>
    public const int VssUnavailable = 4000;
}

/// <summary>
/// Escritura en el registro Aplicación con el origen <c>Backbone</c> (RF-54). Nunca lanza: si el origen no existe (servicio
/// sin instalar, modo consola) lo anota en el log y sigue.
/// </summary>
internal interface IEventLogWriter
{
    void Write(EventLogLevel level, int eventId, string message);
}
