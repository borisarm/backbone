namespace Backbone.Contracts;

/// <summary>
/// Definición de un trabajo de copia (RF-02). Es la configuración que guarda el servicio en <c>jobs.json</c>.
/// </summary>
/// <remarks>
/// Las credenciales no forman parte del trabajo: se guardan por recurso (<see cref="IBackboneService.SaveCredentialAsync"/>).
/// </remarks>
public sealed record JobDefinition
{
    /// <summary>Identificador; <see cref="Guid.Empty"/> en un trabajo nuevo (el servicio lo asigna al guardar).</summary>
    public Guid Id { get; init; }

    /// <summary>Nombre visible del trabajo.</summary>
    public string Name { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Si es <see langword="false"/>, no hay ejecuciones programadas; "Copiar ahora" sigue funcionando (HU-10).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Orígenes: una o más carpetas locales, cada una con su alias.</summary>
    public IReadOnlyList<JobSource> Sources { get; init => field = value ?? []; } = [];

    /// <summary>Destino UNC del repositorio, <c>\\servidor\recurso[\ruta]</c> (RF-03).</summary>
    public string Destination { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Patrones <i>glob</i> de exclusión (RF-04), relativos a la raíz del snapshot (prefijados por el alias o con <c>**</c>).</summary>
    public IReadOnlyList<string> Exclusions { get; init => field = value ?? []; } = [];

    /// <summary>F2 (RF-05). Tamaño máximo de archivo en bytes; <see langword="null"/> = sin límite.</summary>
    public long? MaxFileSizeBytes { get; init; }

    /// <summary>Programación (RF-10, RF-11).</summary>
    public JobSchedule Schedule { get; init => field = value ?? new(); } = new();

    /// <summary>F2 (RF-40). Política de retención.</summary>
    public RetentionPolicy Retention { get; init => field = value ?? new(); } = new();

    /// <summary>F2 (RF-13). Condiciones de ejecución.</summary>
    public JobConditions Conditions { get; init => field = value ?? new(); } = new();

    /// <summary>F3 (RF-28). Límite de ancho de banda en bytes por segundo; <see langword="null"/> = sin límite.</summary>
    public long? BandwidthLimitBytesPerSecond { get; init; }

    /// <summary>F3 (RF-14). Despertar el equipo para las ejecuciones programadas.</summary>
    public bool WakeToRun { get; init; }
}

/// <summary>
/// Carpeta de origen de un trabajo.
/// </summary>
public sealed record JobSource
{
    /// <summary>Alias único dentro del trabajo y válido como nombre de carpeta; prefijo de las rutas del snapshot.</summary>
    public string Alias { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Ruta local absoluta de la carpeta (volumen NTFS local).</summary>
    public string Path { get; init => field = value ?? string.Empty; } = string.Empty;
}

/// <summary>Tipo de programación (RF-10).</summary>
public enum ScheduleKind
{
    /// <summary>Solo bajo petición ("Copiar ahora").</summary>
    Manual = 0,

    /// <summary>A una o más horas del día, los días de la semana seleccionados.</summary>
    Daily = 1,

    /// <summary>Cada N horas (1–24).</summary>
    Interval = 2,
}

/// <summary>Días de la semana (combinables).</summary>
[Flags]
public enum DaysOfWeek
{
    /// <summary>Ningún día.</summary>
    None = 0,

    /// <summary>Lunes.</summary>
    Monday = 1,

    /// <summary>Martes.</summary>
    Tuesday = 2,

    /// <summary>Miércoles.</summary>
    Wednesday = 4,

    /// <summary>Jueves.</summary>
    Thursday = 8,

    /// <summary>Viernes.</summary>
    Friday = 16,

    /// <summary>Sábado.</summary>
    Saturday = 32,

    /// <summary>Domingo.</summary>
    Sunday = 64,

    /// <summary>Todos los días.</summary>
    All = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday,
}

/// <summary>
/// Programación de un trabajo (RF-10, RF-11).
/// </summary>
public sealed record JobSchedule
{
    /// <summary>Tipo de programación.</summary>
    public ScheduleKind Kind { get; init; } = ScheduleKind.Manual;

    /// <summary>
    /// Solo <see cref="ScheduleKind.Daily"/>: horas del día en la <b>hora local del equipo</b> (no son instantes UTC;
    /// el servicio resuelve los cambios de horario de verano).
    /// </summary>
    public IReadOnlyList<TimeOnly> TimesOfDay { get; init => field = value ?? []; } = [];

    /// <summary>Solo <see cref="ScheduleKind.Daily"/>: días de la semana en los que se ejecuta.</summary>
    public DaysOfWeek Days { get; init; } = DaysOfWeek.All;

    /// <summary>Solo <see cref="ScheduleKind.Interval"/>: horas entre ejecuciones (1–24).</summary>
    public int IntervalHours { get; init; } = 24;

    /// <summary>
    /// Recuperar ejecuciones perdidas al arrancar el servicio o al reanudar, tras el periodo de gracia (RF-11).
    /// </summary>
    public bool CatchUpMissedRuns { get; init; } = true;
}

/// <summary>
/// F2 (RF-40, RF-44). Política de retención de snapshots: cuántos se conservan de cada categoría.
/// </summary>
/// <remarks>Valores por defecto 3/7/4/12/0. Un valor 0 desactiva la categoría.</remarks>
public sealed record RetentionPolicy
{
    /// <summary>Últimos snapshots que se conservan (N).</summary>
    public int KeepLast { get; init; } = 3;

    /// <summary>Snapshots diarios que se conservan (D).</summary>
    public int KeepDaily { get; init; } = 7;

    /// <summary>Snapshots semanales que se conservan (S; semanas ISO 8601).</summary>
    public int KeepWeekly { get; init; } = 4;

    /// <summary>Snapshots mensuales que se conservan (M).</summary>
    public int KeepMonthly { get; init; } = 12;

    /// <summary>Snapshots anuales que se conservan (A).</summary>
    public int KeepYearly { get; init; }
}

/// <summary>
/// F2 (RF-13). Condiciones que deben cumplirse para lanzar una ejecución programada.
/// </summary>
/// <remarks>Si no se cumplen, la ejecución queda <see cref="RunState.Skipped"/> con <see cref="RunRecord.SkipReason"/>.</remarks>
public sealed record JobConditions
{
    /// <summary>Porcentaje mínimo de batería sin cargador; <see langword="null"/> = sin condición. Por defecto 30.</summary>
    public int? MinBatteryPercent { get; init; } = JobDefaults.MinBatteryPercent;

    /// <summary>No ejecutar en conexiones de uso medido.</summary>
    public bool SkipOnMeteredConnection { get; init; } = true;

    /// <summary>Solo ejecutar si el NAS es accesible: si no lo es, queda <see cref="RunState.Skipped"/> (<see cref="SkipReason.NasUnreachable"/>) en lugar de fallida.</summary>
    public bool RequireNasReachable { get; init; } = true;
}

/// <summary>
/// Valores por defecto de un trabajo nuevo, compartidos por app y servicio para no duplicarlos.
/// </summary>
public static class JobDefaults
{
    /// <summary>Exclusiones por defecto (RF-04).</summary>
    public static IReadOnlyList<string> Exclusions { get; } =
        ["desktop.ini", "thumbs.db", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information"];

    /// <summary>Umbral de batería por defecto (RF-13).</summary>
    public const int MinBatteryPercent = 30;

    /// <summary>Intervalo mínimo de <see cref="JobSchedule.IntervalHours"/>.</summary>
    public const int MinIntervalHours = 1;

    /// <summary>Intervalo máximo de <see cref="JobSchedule.IntervalHours"/>.</summary>
    public const int MaxIntervalHours = 24;

    /// <summary>Crea un trabajo nuevo (sin identificador) con los valores por defecto.</summary>
    /// <returns>Un <see cref="JobDefinition"/> con exclusiones, programación, retención y condiciones por defecto.</returns>
    public static JobDefinition NewJob() => new() { Exclusions = Exclusions };
}

/// <summary>
/// Resultado de validar un trabajo (<see cref="IBackboneService.ValidateJobAsync"/>, RF-03).
/// </summary>
public sealed record ValidationResult
{
    /// <summary>Errores que impiden guardar el trabajo.</summary>
    public IReadOnlyList<ValidationIssue> Errors { get; init => field = value ?? []; } = [];

    /// <summary>Avisos que no impiden guardar (p. ej. repositorio de formato superior, solo lectura).</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; init => field = value ?? []; } = [];

    /// <summary>Indica si no hay errores. Se calcula; no se serializa.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Un error o aviso de validación asociado a un campo.
/// </summary>
public sealed record ValidationIssue
{
    /// <summary>
    /// Campo afectado, con los nombres de propiedad del DTO validado (<see cref="JobDefinition"/>, <see cref="ServiceSettings"/>…)
    /// e índices desde 0: <c>Name</c>, <c>Sources[1].Alias</c>, <c>Schedule.IntervalHours</c>, <c>OutdatedAfterDays</c>…
    /// Vacío si afecta a la entrada entera o a un parámetro simple (p. ej. la cuenta de un usuario autorizado).
    /// </summary>
    public string Field { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Motivo; la app lo traduce a un mensaje localizado.</summary>
    public ValidationIssueCode Code { get; init; }

    /// <summary>Valor causante (p. ej. el alias repetido o la ruta), para incluirlo en el mensaje; puede faltar.</summary>
    public string? Value { get; init; }
}

/// <summary>Motivos de validación (RF-03, HU-06, HU-07, ajustes, usuarios autorizados y rutas de exportación).</summary>
public enum ValidationIssueCode
{
    /// <summary>Otro motivo.</summary>
    Other = 0,

    /// <summary>Falta el nombre.</summary>
    NameRequired = 1,

    /// <summary>No hay ningún origen.</summary>
    SourcesRequired = 2,

    /// <summary>La carpeta de origen no existe.</summary>
    SourceNotFound = 3,

    /// <summary>El origen no está en un volumen NTFS local.</summary>
    SourceNotNtfs = 4,

    /// <summary>El origen es una unidad de red o extraíble.</summary>
    SourceNotLocal = 5,

    /// <summary>Falta el alias.</summary>
    AliasRequired = 6,

    /// <summary>El alias no es válido como nombre de carpeta.</summary>
    AliasInvalid = 7,

    /// <summary>El alias está repetido en el trabajo.</summary>
    AliasDuplicate = 8,

    /// <summary>Falta el destino.</summary>
    DestinationRequired = 9,

    /// <summary>El destino no es una ruta UNC <c>\\servidor\recurso[\ruta]</c>.</summary>
    DestinationNotUnc = 10,

    /// <summary>El destino está dentro de un origen.</summary>
    DestinationInsideSource = 11,

    /// <summary>El destino lo usa otro trabajo.</summary>
    DestinationUsedByOtherJob = 12,

    /// <summary>El destino contiene archivos y no es un repositorio de Backbone (HU-06.3).</summary>
    DestinationNotRepository = 13,

    /// <summary>Aviso: el repositorio es de un formato superior; solo lectura (RNF-14, HU-06.4).</summary>
    RepositoryNewerFormat = 14,

    /// <summary>Patrón de exclusión no válido.</summary>
    ExclusionPatternInvalid = 15,

    /// <summary>Programación diaria sin horas o sin días.</summary>
    ScheduleIncomplete = 16,

    /// <summary>Valor fuera de rango (intervalo, retención, batería, tamaño, ancho de banda, ajustes).</summary>
    ValueOutOfRange = 17,

    /// <summary>La cuenta indicada no existe o no se puede resolver a un SID (usuarios autorizados).</summary>
    AccountNotFound = 18,

    /// <summary>Ruta de destino no admitida (no absoluta, UNC, extensión incorrecta, ya existe o en una carpeta protegida; §8.7).</summary>
    TargetPathNotAllowed = 19,

    /// <summary>La cuenta no es miembro del grupo local Administradores (solo cuentas administradoras, §7.3).</summary>
    AccountNotAdministrator = 20,

    /// <summary>
    /// La cuenta no es una cuenta de usuario cualificada (<c>EQUIPO\usuario</c>, <c>DOMINIO\usuario</c> o SID de usuario):
    /// grupos, Everyone, SID bien conocidos o nombres sin cualificar se rechazan (§7.3).
    /// </summary>
    AccountNotUser = 21,

    /// <summary>El identificador propuesto por el cliente ya está en uso (p. ej. <see cref="RestoreRequest.RestoreId"/>).</summary>
    IdAlreadyInUse = 22,

    /// <summary>
    /// Destino UNC no admitido: el propio equipo o <i>loopback</i>, rutas <c>\\?\</c> o <c>\\.\</c>, recursos administrativos
    /// (<c>C$</c>, <c>ADMIN$</c>, <c>IPC$</c>) o segmentos no válidos (RF-03, <see cref="UncPaths"/>).
    /// </summary>
    DestinationNotAllowed = 23,

    /// <summary>Texto, lista o número fuera de los límites de <see cref="ContractLimits"/>.</summary>
    LimitExceeded = 24,

    /// <summary>Ruta o identificador de snapshot con formato no válido (<see cref="SnapshotPaths"/>).</summary>
    SnapshotPathInvalid = 25,

    /// <summary>
    /// Contrato 1.2. Error: el origen es la carpeta de datos <c>%ProgramData%\Backbone</c> o está dentro de ella, comparando
    /// rutas finales tras resolver puntos de reanálisis (RF-03, ADR-16, §8.3). <c>Field</c> = <c>Sources[i].Path</c>.
    /// </summary>
    SourceNotAllowed = 26,

    /// <summary>
    /// Contrato 1.2. Aviso: el origen contiene la carpeta de datos <c>%ProgramData%\Backbone</c>, que se excluirá siempre
    /// (exclusión obligatoria, ADR-16, §8.3). <c>Field</c> = <c>Sources[i].Path</c>. No impide guardar.
    /// </summary>
    SourceContainsDataFolder = 27,
}
