using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;

namespace Backbone.Service.Jobs;

/// <summary>
/// Reglas de negocio de un trabajo (RF-03, HU-07 criterios 2, 3 y 6, §7.5, §8.3). Es la única validación de trabajos: la
/// app no duplica reglas (criterio 4). Se aplica después de la validación genérica de la entrada
/// (<see cref="Ipc.IpcInputValidator.Job"/>: nulos, límites y enumerados).
/// </summary>
/// <remarks>
/// Cada problema lleva <see cref="ValidationIssue.Field"/> con el nombre de la propiedad e índices desde 0
/// (<c>Sources[1].Path</c>). <see cref="ValidationIssue.Value"/> solo se rellena cuando ayuda al mensaje y no es un texto
/// arbitrario del cliente: el alias repetido y la carpeta protegida que causa <c>SourceNotAllowed</c> o
/// <c>SourceContainsDataFolder</c>.
/// </remarks>
internal sealed class JobValidator(ILocalFileSystemInspector fileSystem, ProtectedFolderResolver protectedFolders)
{
    private const string Sources = nameof(JobDefinition.Sources);
    private const string Schedule = nameof(JobDefinition.Schedule);
    private const string Retention = nameof(JobDefinition.Retention);
    private const string Conditions = nameof(JobDefinition.Conditions);

    /// <summary>Valida <paramref name="job"/> frente a los trabajos guardados (el propio, por <c>Id</c>, no cuenta).</summary>
    public ValidationResult Validate(JobDefinition job, IReadOnlyList<JobDefinition> savedJobs)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(savedJobs);
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        ValidateName(job, errors);
        var sourceFinalPaths = ValidateSources(job, errors, warnings);
        ValidateDestination(job, savedJobs, sourceFinalPaths, errors);
        ValidateExclusions(job, errors);
        ValidateSchedule(job.Schedule, errors);
        ValidateNumbers(job, errors);

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    /// <summary>
    /// Destino normalizado (<see cref="UncPaths.TryParse"/>: servidor y recurso en minúsculas, sin barra final) o el motivo del
    /// rechazo. También rechaza el propio equipo y los caracteres que no se pueden guardar (<see cref="CredentialStore.IsStorable"/>).
    /// </summary>
    public static bool TryNormalizeDestination(string? destination, out string normalized, out ValidationIssueCode issue)
    {
        if (!UncPaths.TryParse(destination, out _, out normalized, out issue, LocalMachine.Names(), LocalMachine.Addresses()))
        {
            return false;
        }

        if (destination is null || !CredentialStore.IsStorable(destination, allowControl: false))
        {
            normalized = string.Empty;
            issue = ValidationIssueCode.DestinationNotAllowed;
            return false;
        }

        return true;
    }

    private static void ValidateName(JobDefinition job, List<ValidationIssue> errors)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            errors.Add(Issue(nameof(JobDefinition.Name), ValidationIssueCode.NameRequired));
        }
        else if (HasInvalidText(job.Name))
        {
            errors.Add(Issue(nameof(JobDefinition.Name), ValidationIssueCode.ValueOutOfRange));
        }
    }

    /// <summary>Alias (criterio 2) y rutas (criterios 3 y 6). Devuelve la ruta final de cada origen que se ha podido abrir.</summary>
    private List<string> ValidateSources(JobDefinition job, List<ValidationIssue> errors, List<ValidationIssue> warnings)
    {
        var finalPaths = new List<string>();
        if (job.Sources.Count == 0)
        {
            errors.Add(Issue(Sources, ValidationIssueCode.SourcesRequired));
            return finalPaths;
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProtectedFolderSet? protectedSet = null;
        for (var i = 0; i < job.Sources.Count; i++)
        {
            var source = job.Sources[i];
            var aliasField = $"{Sources}[{i}].{nameof(JobSource.Alias)}";
            if (string.IsNullOrWhiteSpace(source.Alias))
            {
                errors.Add(Issue(aliasField, ValidationIssueCode.AliasRequired));
            }
            else if (source.Alias.Length > ContractLimits.MaxAliasLength || !SnapshotPaths.IsValidSegment(source.Alias))
            {
                errors.Add(Issue(aliasField, ValidationIssueCode.AliasInvalid));
            }
            else if (!aliases.Add(source.Alias))
            {
                errors.Add(Issue(aliasField, ValidationIssueCode.AliasDuplicate, source.Alias));
            }

            var pathField = $"{Sources}[{i}].{nameof(JobSource.Path)}";
            if (string.IsNullOrWhiteSpace(source.Path))
            {
                errors.Add(Issue(pathField, ValidationIssueCode.SourceNotFound));
                continue;
            }

            var folder = fileSystem.InspectFolder(source.Path);
            switch (folder.Status)
            {
                case LocalFolderStatus.Ok:
                    break;
                case LocalFolderStatus.Remote:
                    errors.Add(Issue(pathField, ValidationIssueCode.SourceNotLocal));
                    continue;
                case LocalFolderStatus.InvalidPath:
                    // Una ruta UNC o de dispositivo (\\servidor\…, \\?\…, \\.\…) no es una carpeta local.
                    errors.Add(Issue(pathField, source.Path.StartsWith(@"\\", StringComparison.Ordinal) || source.Path.StartsWith("//", StringComparison.Ordinal)
                        ? ValidationIssueCode.SourceNotLocal
                        : ValidationIssueCode.SourceNotFound));
                    continue;
                default:
                    errors.Add(Issue(pathField, ValidationIssueCode.SourceNotFound));
                    continue;
            }

            finalPaths.Add(folder.FinalPath);
            if (folder.Volume != LocalVolumeKind.Fixed)
            {
                errors.Add(Issue(pathField, ValidationIssueCode.SourceNotLocal));
                continue;
            }

            if (!string.Equals(folder.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Issue(pathField, ValidationIssueCode.SourceNotNtfs));
            }

            // §8.3, capa 1: comparando rutas finales; la lista se calcula una vez por validación.
            protectedSet ??= protectedFolders.Resolve();
            var protection = protectedSet.Classify(folder.FinalPath);
            if (protection.IsNotAllowed)
            {
                errors.Add(Issue(pathField, ValidationIssueCode.SourceNotAllowed, protection.ProtectedPath));
            }
            else if (protection.Kind == SourceProtectionKind.ContainsProtectedFolders)
            {
                warnings.Add(Issue(pathField, ValidationIssueCode.SourceContainsDataFolder, protection.ProtectedPath));
            }
        }

        return finalPaths;
    }

    private static void ValidateDestination(JobDefinition job, IReadOnlyList<JobDefinition> savedJobs, List<string> sourceFinalPaths, List<ValidationIssue> errors)
    {
        const string field = nameof(JobDefinition.Destination);
        if (!TryNormalizeDestination(job.Destination, out var destination, out var issue))
        {
            errors.Add(Issue(field, issue));
            return;
        }

        // RF-03: el destino no puede estar dentro de un origen. Como el destino es UNC y nunca el propio equipo, solo puede
        // ocurrir si un origen lleva a la red (un enlace hacia \\servidor\…), que ya se rechaza como SourceNotLocal.
        foreach (var sourcePath in sourceFinalPaths)
        {
            if (IsSameOrInside(destination, sourcePath))
            {
                errors.Add(Issue(field, ValidationIssueCode.DestinationInsideSource));
                break;
            }
        }

        if (IsDestinationUsedByOtherJob(job.Id, destination, savedJobs))
        {
            errors.Add(Issue(field, ValidationIssueCode.DestinationUsedByOtherJob));
        }
    }

    /// <summary>
    /// RF-03: el destino normalizado lo usa otro trabajo (distinto de <paramref name="jobId"/>). Un destino dentro del de otro
    /// trabajo, o que lo contiene, también cuenta: los dos repositorios quedarían uno dentro del otro.
    /// </summary>
    public static bool IsDestinationUsedByOtherJob(Guid jobId, string normalizedDestination, IEnumerable<JobDefinition> savedJobs)
    {
        ArgumentNullException.ThrowIfNull(savedJobs);
        foreach (var other in savedJobs)
        {
            if (other.Id != jobId && TryNormalizeDestination(other.Destination, out var otherDestination, out _)
                && (IsSameOrInside(normalizedDestination, otherDestination) || IsSameOrInside(otherDestination, normalizedDestination)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateExclusions(JobDefinition job, List<ValidationIssue> errors)
    {
        // La sintaxis de los patrones la define el motor (HU-08); aquí solo se rechazan los vacíos y los caracteres de control.
        for (var i = 0; i < job.Exclusions.Count; i++)
        {
            var pattern = job.Exclusions[i];
            if (string.IsNullOrWhiteSpace(pattern) || HasInvalidText(pattern))
            {
                errors.Add(Issue($"{nameof(JobDefinition.Exclusions)}[{i}]", ValidationIssueCode.ExclusionPatternInvalid));
            }
        }
    }

    private static void ValidateSchedule(JobSchedule schedule, List<ValidationIssue> errors)
    {
        switch (schedule.Kind)
        {
            case ScheduleKind.Daily:
                if (schedule.TimesOfDay.Count == 0)
                {
                    errors.Add(Issue($"{Schedule}.{nameof(JobSchedule.TimesOfDay)}", ValidationIssueCode.ScheduleIncomplete));
                }

                if (schedule.Days == DaysOfWeek.None)
                {
                    errors.Add(Issue($"{Schedule}.{nameof(JobSchedule.Days)}", ValidationIssueCode.ScheduleIncomplete));
                }

                break;
            case ScheduleKind.Interval when schedule.IntervalHours is < JobDefaults.MinIntervalHours or > JobDefaults.MaxIntervalHours:
                errors.Add(Issue($"{Schedule}.{nameof(JobSchedule.IntervalHours)}", ValidationIssueCode.ValueOutOfRange));
                break;
        }
    }

    private static void ValidateNumbers(JobDefinition job, List<ValidationIssue> errors)
    {
        var retention = job.Retention;
        NotNegative($"{Retention}.{nameof(RetentionPolicy.KeepLast)}", retention.KeepLast, errors);
        NotNegative($"{Retention}.{nameof(RetentionPolicy.KeepDaily)}", retention.KeepDaily, errors);
        NotNegative($"{Retention}.{nameof(RetentionPolicy.KeepWeekly)}", retention.KeepWeekly, errors);
        NotNegative($"{Retention}.{nameof(RetentionPolicy.KeepMonthly)}", retention.KeepMonthly, errors);
        NotNegative($"{Retention}.{nameof(RetentionPolicy.KeepYearly)}", retention.KeepYearly, errors);

        if (job.Conditions.MinBatteryPercent is < 0 or > 100)
        {
            errors.Add(Issue($"{Conditions}.{nameof(JobConditions.MinBatteryPercent)}", ValidationIssueCode.ValueOutOfRange));
        }

        if (job.MaxFileSizeBytes is <= 0)
        {
            errors.Add(Issue(nameof(JobDefinition.MaxFileSizeBytes), ValidationIssueCode.ValueOutOfRange));
        }

        if (job.BandwidthLimitBytesPerSecond is <= 0)
        {
            errors.Add(Issue(nameof(JobDefinition.BandwidthLimitBytesPerSecond), ValidationIssueCode.ValueOutOfRange));
        }
    }

    private static void NotNegative(string field, int value, List<ValidationIssue> errors)
    {
        if (value < 0)
        {
            errors.Add(Issue(field, ValidationIssueCode.ValueOutOfRange));
        }
    }

    /// <summary>Caracteres de control o suplentes sueltas (no se guardarían ni se mostrarían intactos).</summary>
    private static bool HasInvalidText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="path"/> es <paramref name="container"/> o está dentro, por segmentos completos y sin distinguir mayúsculas.</summary>
    private static bool IsSameOrInside(string path, string container)
    {
        var p = path.TrimEnd('\\');
        var c = container.TrimEnd('\\');
        return p.Equals(c, StringComparison.OrdinalIgnoreCase)
            || (p.Length > c.Length && p.StartsWith(c, StringComparison.OrdinalIgnoreCase) && p[c.Length] == '\\');
    }

    private static ValidationIssue Issue(string field, ValidationIssueCode code, string? value = null) =>
        new() { Field = field, Code = code, Value = value };
}
