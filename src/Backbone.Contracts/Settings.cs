namespace Backbone.Contracts;

/// <summary>
/// Ajustes globales del servicio (<c>settings.json</c>, §8.2) que se gestionan desde la app.
/// </summary>
/// <remarks>
/// No incluye la pausa (<see cref="IBackboneService.SetPauseAsync"/>), los usuarios autorizados
/// (<see cref="IBackboneService.GetAuthorizedUsersAsync"/>) ni el inicio con Windows (lo gestiona la app en HKCU).
/// No contiene secretos.
/// </remarks>
public sealed record ServiceSettings
{
    /// <summary>Periodo de gracia antes de recuperar una ejecución perdida (RF-11). Por defecto 5 minutos; entre 0 y 24 horas.</summary>
    public TimeSpan MissedRunGracePeriod { get; init; } = ServiceSettingsDefaults.MissedRunGracePeriod;

    /// <summary>
    /// Días sin copia correcta a partir de los cuales un trabajo está desactualizado (RF-50, RF-52, evento 7000).
    /// Por defecto 3; entre 1 y 365.
    /// </summary>
    public int OutdatedAfterDays { get; init; } = ServiceSettingsDefaults.OutdatedAfterDays;

    /// <summary>Preferencias de notificación (RF-52).</summary>
    public NotificationSettings Notifications { get; init => field = value ?? new(); } = new();
}

/// <summary>
/// Preferencias de notificación (RF-52). Las ejecuciones fallidas se notifican siempre; no es configurable.
/// </summary>
public sealed record NotificationSettings
{
    /// <summary>F2. Notificar las ejecuciones terminadas con avisos. Por defecto activado.</summary>
    public bool OnWarnings { get; init; } = true;

    /// <summary>F2. Notificar las ejecuciones correctas. Por defecto desactivado.</summary>
    public bool OnSuccess { get; init; }

    /// <summary>F2. Notificar cuando un trabajo pasa a desactualizado (<see cref="ServiceSettings.OutdatedAfterDays"/>). Por defecto activado.</summary>
    public bool OnOutdated { get; init; } = true;

    /// <summary>F2. Notificar el espacio bajo en el NAS (RF-43). Por defecto activado.</summary>
    public bool OnLowSpace { get; init; } = true;
}

/// <summary>Valores por defecto y límites de <see cref="ServiceSettings"/>.</summary>
public static class ServiceSettingsDefaults
{
    /// <summary>Periodo de gracia por defecto (RF-11).</summary>
    public static readonly TimeSpan MissedRunGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>Periodo de gracia máximo.</summary>
    public static readonly TimeSpan MaxMissedRunGracePeriod = TimeSpan.FromHours(24);

    /// <summary>Días sin copia correcta por defecto (RF-52).</summary>
    public const int OutdatedAfterDays = 3;

    /// <summary>Máximo de <see cref="ServiceSettings.OutdatedAfterDays"/>.</summary>
    public const int MaxOutdatedAfterDays = 365;
}

/// <summary>
/// Usuario autorizado a gestionar Backbone sin elevar: siempre una cuenta de usuario miembro de Administradores (§7.3).
/// <c>settings.json</c> guarda solo el SID.
/// </summary>
public sealed record AuthorizedUser
{
    /// <summary>SID de la cuenta (<c>S-1-5-21-…</c>); es la identidad.</summary>
    public string Sid { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>
    /// Nombre de la cuenta resuelto por el servicio al responder (<c>EQUIPO\usuario</c>);
    /// <see langword="null"/> si el SID ya no se puede resolver (cuenta eliminada).
    /// </summary>
    public string? AccountName { get; init; }
}

/// <summary>
/// F2 (RF-55). Petición de exportación del diagnóstico (<see cref="IBackboneService.ExportDiagnosticsAsync"/>).
/// </summary>
public sealed record DiagnosticsExportRequest
{
    /// <summary>
    /// Ruta local absoluta del ZIP que se crea (la elige el usuario con un selector de archivo). Debe terminar en <c>.zip</c>,
    /// estar en un volumen local (no UNC) y no existir. El servicio la crea <b>suplantando al llamante</b> (§8.7).
    /// </summary>
    public string TargetPath { get; init => field = value ?? string.Empty; } = string.Empty;
}

/// <summary>
/// F2 (RF-55). Resultado de la exportación del diagnóstico.
/// </summary>
public sealed record DiagnosticsExportResult
{
    /// <summary>Ruta completa del ZIP creado.</summary>
    public string Path { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Tamaño del ZIP en bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Instante de creación (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; }
}
