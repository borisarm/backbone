using System.Net.Sockets;
using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Jobs;

/// <summary>Resultado de <see cref="JobValidator.ValidateAsync"/>.</summary>
/// <param name="Issues">Errores y avisos por campo.</param>
/// <param name="Job">
/// El trabajo con las rutas de origen normalizadas (§7.5 "Ruta de origen") y el destino normalizado
/// (<see cref="UncPaths.TryParse"/>) donde son válidos: lo que se guarda en <c>jobs.json</c> y devuelve <c>SaveJobAsync</c>.
/// </param>
internal sealed record JobValidation(ValidationResult Issues, JobDefinition Job);

/// <summary>
/// Reglas de negocio de un trabajo (RF-03, HU-07 criterios 2, 3 y 6, §7.5, §8.3). Es la única validación de trabajos: la
/// app no duplica reglas (criterio 4). Se aplica después de la validación genérica de la entrada
/// (<see cref="Ipc.IpcInputValidator.Job"/>: nulos, límites y enumerados).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Cada problema lleva <see cref="ValidationIssue.Field"/> con el nombre de la propiedad e índices desde 0
/// (<c>Sources[1].Path</c>). <see cref="ValidationIssue.Value"/> solo se rellena cuando ayuda al mensaje y no es un texto
/// arbitrario del cliente: el alias repetido y la carpeta protegida que causa <c>SourceNotAllowed</c> o
/// <c>SourceContainsDataFolder</c>.</item>
/// <item><b>Orígenes</b> (ADR-20, §7.5): cada uno da como mucho un error, en este orden: <c>SourcePathInvalid</c>,
/// <c>SourceNotLocal</c>, <c>SourceNotFound</c>, <c>SourceUnreadable</c>, <c>SourceNotNtfs</c> y <c>SourceNotAllowed</c>.
/// La resolución controlada y el cálculo de las carpetas protegidas se hacen fuera del hilo RPC, en
/// <see cref="SourceValidationRunner"/> (como mucho 4 validaciones a la vez en todo el servicio, cancelables con la llamada
/// y con el plazo de §8.2; MEDIA-1).</item>
/// <item><b>Destino</b>: <see cref="UncPaths.TryParse"/> y, después, <see cref="LocalMachine.ResolvesToSelfAsync"/> con un
/// tope de <see cref="SelfResolutionTimeout"/> (§7.5): si resuelve al propio equipo, <c>DestinationNotAllowed</c>; si la
/// resolución falla o vence el tope, no se rechaza por esto.</item>
/// </list>
/// </remarks>
internal sealed partial class JobValidator(
    ILocalFileSystemInspector fileSystem,
    ProtectedFolderResolver protectedFolders,
    SourceValidationRunner runner,
    IHostResolver hostResolver,
    TimeProvider timeProvider,
    ILogger<JobValidator> logger)
{
    /// <summary>Tope de la resolución del nombre del servidor del destino (§7.5, el mismo que en "Probar conexión").</summary>
    public static readonly TimeSpan SelfResolutionTimeout = TimeSpan.FromSeconds(4);

    private const string Sources = nameof(JobDefinition.Sources);
    private const string Schedule = nameof(JobDefinition.Schedule);
    private const string Retention = nameof(JobDefinition.Retention);
    private const string Conditions = nameof(JobDefinition.Conditions);

    /// <summary>Valida <paramref name="job"/> frente a los trabajos guardados (el propio, por <c>Id</c>, no cuenta).</summary>
    /// <exception cref="OperationCanceledException">Se ha cancelado la llamada.</exception>
    public async Task<JobValidation> ValidateAsync(JobDefinition job, IReadOnlyList<JobDefinition> savedJobs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(savedJobs);
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();

        ValidateName(job, errors);
        var (sources, sourceFinalPaths) = await ValidateSourcesAsync(job, errors, warnings, ct).ConfigureAwait(false);
        var destination = await ValidateDestinationAsync(job, savedJobs, sourceFinalPaths, errors, ct).ConfigureAwait(false);
        ValidateExclusions(job, errors);
        ValidateSchedule(job.Schedule, errors);
        ValidateNumbers(job, errors);

        var normalized = job with { Sources = sources, Destination = destination ?? job.Destination };
        return new JobValidation(new ValidationResult { Errors = errors, Warnings = warnings }, normalized);
    }

    /// <summary>
    /// Destino normalizado (<see cref="UncPaths.TryParse"/>: servidor y recurso en minúsculas, sin barra final) o el motivo del
    /// rechazo. También rechaza el propio equipo por nombre o dirección local y los caracteres que no se pueden guardar
    /// (<see cref="CredentialStore.IsStorable"/>). La resolución por DNS es aparte (<see cref="ValidateAsync"/>).
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

    /// <summary>Resultado de comprobar un origen en el hilo de trabajo.</summary>
    private sealed record SourceCheck(ValidationIssue? Error, ValidationIssue? Warning, string FinalPath);

    /// <summary>
    /// Alias (criterio 2) y rutas (criterios 3 y 6). Devuelve los orígenes con la ruta normalizada y la ruta final de los que
    /// se han podido resolver.
    /// </summary>
    private async Task<(List<JobSource> Sources, List<string> FinalPaths)> ValidateSourcesAsync(
        JobDefinition job, List<ValidationIssue> errors, List<ValidationIssue> warnings, CancellationToken ct)
    {
        var normalizedSources = new List<JobSource>(job.Sources.Count);
        var finalPaths = new List<string>();
        if (job.Sources.Count == 0)
        {
            errors.Add(Issue(Sources, ValidationIssueCode.SourcesRequired));
            return (normalizedSources, finalPaths);
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliasErrors = new ValidationIssue?[job.Sources.Count];
        var pathErrors = new ValidationIssue?[job.Sources.Count];
        var toResolve = new List<(int Index, string Path)>();
        for (var i = 0; i < job.Sources.Count; i++)
        {
            var source = job.Sources[i];
            var aliasField = $"{Sources}[{i}].{nameof(JobSource.Alias)}";
            if (string.IsNullOrWhiteSpace(source.Alias))
            {
                aliasErrors[i] = Issue(aliasField, ValidationIssueCode.AliasRequired);
            }
            else if (source.Alias.Length > ContractLimits.MaxAliasLength || !SnapshotPaths.IsValidSegment(source.Alias))
            {
                aliasErrors[i] = Issue(aliasField, ValidationIssueCode.AliasInvalid);
            }
            else if (!aliases.Add(source.Alias))
            {
                aliasErrors[i] = Issue(aliasField, ValidationIssueCode.AliasDuplicate, source.Alias);
            }

            switch (SourcePath.TryNormalize(source.Path, out var normalized))
            {
                case SourcePathCheck.Invalid:
                    pathErrors[i] = Issue(PathField(i), ValidationIssueCode.SourcePathInvalid);
                    normalizedSources.Add(source);
                    break;
                case SourcePathCheck.NotLocal:
                    pathErrors[i] = Issue(PathField(i), ValidationIssueCode.SourceNotLocal);
                    normalizedSources.Add(source);
                    break;
                default:
                    normalizedSources.Add(source with { Path = normalized });
                    toResolve.Add((i, normalized));
                    break;
            }
        }

        var checks = toResolve.Count == 0 ? [] : await ResolveSourcesAsync(toResolve, ct).ConfigureAwait(false);
        var next = 0;
        for (var i = 0; i < job.Sources.Count; i++)
        {
            if (aliasErrors[i] is { } aliasError)
            {
                errors.Add(aliasError);
            }

            if (pathErrors[i] is { } pathError)
            {
                errors.Add(pathError);
                continue;
            }

            var check = checks[next++];
            if (check.FinalPath.Length > 0)
            {
                finalPaths.Add(check.FinalPath);
            }

            if (check.Error is { } error)
            {
                errors.Add(error);
            }

            if (check.Warning is { } warning)
            {
                warnings.Add(warning);
            }
        }

        return (normalizedSources, finalPaths);
    }

    /// <summary>
    /// Resolución controlada y carpetas protegidas en el hilo de trabajo (MEDIA-1). Si una operación vence su plazo, ese
    /// origen da <c>SourceUnreadable</c>; si el hilo se abandona, también los que no se hayan terminado.
    /// </summary>
    private async Task<SourceCheck[]> ResolveSourcesAsync(List<(int Index, string Path)> sources, CancellationToken ct)
    {
        var results = new SourceCheck?[sources.Count];
        try
        {
            await runner.RunAsync(
                guard =>
                {
                    ProtectedFolderSet? protectedSet = null;
                    for (var k = 0; k < sources.Count; k++)
                    {
                        var (index, path) = sources[k];
                        SourceCheck check;
                        try
                        {
                            check = CheckSource(index, fileSystem.ResolveFolder(path, guard), ref protectedSet, guard);
                        }
                        catch (SourceIoTimeoutException)
                        {
                            LogSourceTimedOut(logger, index);
                            check = new SourceCheck(Issue(PathField(index), ValidationIssueCode.SourceUnreadable), null, string.Empty);
                        }

                        Volatile.Write(ref results[k], check);
                    }

                    return true;
                },
                limited: true,
                ct).ConfigureAwait(false);
        }
        catch (SourceIoTimeoutException)
        {
            // Hilo abandonado (§8.3 "Plazo"): sigue ocupando su plaza hasta que vuelva.
            LogValidationAbandoned(logger);
        }

        var checks = new SourceCheck[sources.Count];
        for (var k = 0; k < sources.Count; k++)
        {
            checks[k] = Volatile.Read(ref results[k])
                ?? new SourceCheck(Issue(PathField(sources[k].Index), ValidationIssueCode.SourceUnreadable), null, string.Empty);
        }

        return checks;
    }

    private SourceCheck CheckSource(int index, LocalFolderInfo folder, ref ProtectedFolderSet? protectedSet, SourceIoGuard guard)
    {
        var field = PathField(index);
        ValidationIssueCode? failure = folder.Status switch
        {
            LocalFolderStatus.Ok => null,
            LocalFolderStatus.NotLocal => ValidationIssueCode.SourceNotLocal,
            LocalFolderStatus.NotFound => ValidationIssueCode.SourceNotFound,
            LocalFolderStatus.InvalidPath => ValidationIssueCode.SourcePathInvalid,
            _ => ValidationIssueCode.SourceUnreadable,
        };
        if (failure is { } code)
        {
            if (code == ValidationIssueCode.SourceUnreadable)
            {
                var detail = UntrustedText.Describe(folder.Detail);
                LogSourceUnreadable(logger, index, detail);
            }

            return new SourceCheck(Issue(field, code), null, string.Empty);
        }

        if (!string.Equals(folder.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceCheck(Issue(field, ValidationIssueCode.SourceNotNtfs), null, folder.FinalPath);
        }

        // §8.3, capa 1: comparando rutas finales; la lista se calcula una vez por validación.
        protectedSet ??= protectedFolders.Resolve(guard);
        var protection = protectedSet.Classify(folder.FinalPath);
        if (protection.IsNotAllowed)
        {
            return new SourceCheck(Issue(field, ValidationIssueCode.SourceNotAllowed, protection.ProtectedPath), null, folder.FinalPath);
        }

        return protection.Kind == SourceProtectionKind.ContainsProtectedFolders
            ? new SourceCheck(null, Issue(field, ValidationIssueCode.SourceContainsDataFolder, protection.ProtectedPath), folder.FinalPath)
            : new SourceCheck(null, null, folder.FinalPath);
    }

    private static string PathField(int index) => $"{Sources}[{index}].{nameof(JobSource.Path)}";

    /// <returns>El destino normalizado, o <see langword="null"/> si no es válido.</returns>
    private async Task<string?> ValidateDestinationAsync(
        JobDefinition job, IReadOnlyList<JobDefinition> savedJobs, List<string> sourceFinalPaths, List<ValidationIssue> errors, CancellationToken ct)
    {
        const string field = nameof(JobDefinition.Destination);
        if (!TryNormalizeDestination(job.Destination, out var destination, out var issue))
        {
            errors.Add(Issue(field, issue));
            return null;
        }

        if (await ResolvesToSelfAsync(destination, ct).ConfigureAwait(false))
        {
            errors.Add(Issue(field, ValidationIssueCode.DestinationNotAllowed));
            return null;
        }

        // RF-03: el destino no puede estar dentro de un origen. Como el destino es UNC y nunca el propio equipo, solo podría
        // ocurrir con un origen que llevara a la red, que la resolución controlada ya rechaza como SourceNotLocal.
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

        return destination;
    }

    /// <summary>
    /// §7.5 (HU-05): el servidor del destino no puede resolver al propio equipo. Tope de <see cref="SelfResolutionTimeout"/>;
    /// si falla o vence, no se rechaza (decidirá la conexión). Riesgo residual de <i>DNS rebinding</i>: R14.
    /// </summary>
    private async Task<bool> ResolvesToSelfAsync(string normalizedDestination, CancellationToken ct)
    {
        var end = normalizedDestination.IndexOf('\\', 2);
        var server = normalizedDestination[2..end];
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resolution = LocalMachine.ResolvesToSelfAsync(server, hostResolver, LocalMachine.Addresses(), abandon.Token);
        try
        {
            var isSelf = await resolution.WaitAsync(SelfResolutionTimeout, timeProvider, ct).ConfigureAwait(false);
            if (isSelf)
            {
                LogResolvesToSelf(logger);
            }

            return isSelf;
        }
        catch (TimeoutException)
        {
            // La resolución recibe su propio token: se le pide que pare y se abandona.
            _ = resolution.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await abandon.CancelAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
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
        // La sintaxis de los patrones la define el motor (HU-08); aquí se rechazan los vacíos y los caracteres de control.
        var before = errors.Count;
        for (var i = 0; i < job.Exclusions.Count; i++)
        {
            var pattern = job.Exclusions[i];
            if (string.IsNullOrWhiteSpace(pattern) || HasInvalidText(pattern))
            {
                errors.Add(Issue($"{nameof(JobDefinition.Exclusions)}[{i}]", ValidationIssueCode.ExclusionPatternInvalid));
            }
        }

        if (errors.Count != before)
        {
            return;
        }

        // Pendiente de HU-08: Backbone.Core.Engine.ExclusionMatcher aún no está en main. Al fusionarla, sustituir este
        // comentario por la llamada siguiente (§7.5 "Exclusiones demasiado complejas"):
        //
        //   var error = ExclusionMatcher.ValidateList(job.Exclusions, out var index);
        //   if (error != ExclusionPatternError.None)
        //   {
        //       // Lista entera (TooMany, TooComplex): en "Exclusions", sin índice y con Value nulo; un patrón, en "Exclusions[i]".
        //       errors.Add(Issue(
        //           index < 0 ? nameof(JobDefinition.Exclusions) : $"{nameof(JobDefinition.Exclusions)}[{index}]",
        //           ValidationIssueCode.ExclusionPatternInvalid));
        //   }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validación de un trabajo: el origen {Index} no ha respondido en el plazo (§8.2); se da como ilegible.")]
    private static partial void LogSourceTimedOut(ILogger logger, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validación de un trabajo: la resolución de los orígenes no ha vuelto tras cancelarla; se abandona su hilo, que sigue ocupando una plaza (§8.3, R12).")]
    private static partial void LogValidationAbandoned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Validación de un trabajo: el origen {Index} no se puede comprobar ({Detail}).")]
    private static partial void LogSourceUnreadable(ILogger logger, int index, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Validación de un trabajo: el servidor del destino resuelve al propio equipo; se rechaza (§7.5).")]
    private static partial void LogResolvesToSelf(ILogger logger);
}
