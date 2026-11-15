using System.Globalization;
using Backbone.Contracts;

namespace Backbone.Service.Ipc;

/// <summary>
/// Validación genérica de las entradas IPC (§7.5, RNF-08), común a todos los métodos e independiente de la lógica de cada
/// historia: <see langword="null"/> donde el contrato no lo admite, límites de <see cref="ContractLimits"/>, enumerados no
/// definidos (y bits fuera de los definidos en los de indicadores), paginación, rutas de snapshot y fechas (que se normalizan
/// a UTC). Las reglas de negocio (p. ej. RF-03) las añade cada historia después de esta validación.
/// </summary>
/// <remarks>
/// Los errores llevan <see cref="ValidationIssue.Field"/> con los nombres de propiedad del DTO e índices desde 0, y nunca el
/// valor recibido salvo el número de un enumerado (no se devuelven textos del cliente, y nunca una contraseña).
/// </remarks>
internal static class IpcInputValidator
{
    /// <summary>Un DTO o texto obligatorio que llega como <see langword="null"/> (JSON <c>null</c>) se rechaza.</summary>
    public static T Require<T>(T? value)
        where T : class =>
        value ?? throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.ValueOutOfRange);

    public static JobDefinition Job(JobDefinition? job)
    {
        job = Require(job);
        var v = new Collector();

        v.MaxLength(nameof(JobDefinition.Name), job.Name, ContractLimits.MaxJobNameLength);
        v.MaxLength(nameof(JobDefinition.Destination), job.Destination, ContractLimits.MaxDestinationLength);

        v.MaxCount(nameof(JobDefinition.Sources), job.Sources.Count, ContractLimits.MaxSources);
        for (var i = 0; i < job.Sources.Count && i < ContractLimits.MaxSources; i++)
        {
            var field = $"{nameof(JobDefinition.Sources)}[{i}]";
            if (job.Sources[i] is not { } source)
            {
                v.Fail(field, ValidationIssueCode.ValueOutOfRange);
                continue;
            }

            v.MaxLength($"{field}.{nameof(JobSource.Alias)}", source.Alias, ContractLimits.MaxAliasLength);
            v.MaxLength($"{field}.{nameof(JobSource.Path)}", source.Path, ContractLimits.MaxLocalPathLength);
        }

        v.MaxCount(nameof(JobDefinition.Exclusions), job.Exclusions.Count, ContractLimits.MaxExclusions);
        for (var i = 0; i < job.Exclusions.Count && i < ContractLimits.MaxExclusions; i++)
        {
            var field = $"{nameof(JobDefinition.Exclusions)}[{i}]";
            if (job.Exclusions[i] is not { } pattern)
            {
                v.Fail(field, ValidationIssueCode.ValueOutOfRange);
                continue;
            }

            v.MaxLength(field, pattern, ContractLimits.MaxExclusionPatternLength);
        }

        const string schedule = nameof(JobDefinition.Schedule);
        v.Defined($"{schedule}.{nameof(JobSchedule.Kind)}", job.Schedule.Kind);
        v.DefinedFlags($"{schedule}.{nameof(JobSchedule.Days)}", job.Schedule.Days, DaysOfWeek.All);
        v.MaxCount($"{schedule}.{nameof(JobSchedule.TimesOfDay)}", job.Schedule.TimesOfDay.Count, ContractLimits.MaxTimesOfDay);

        v.ThrowIfAny();
        return job;
    }

    public static PauseRequest Pause(PauseRequest? request)
    {
        request = Require(request);
        return request with { UntilUtc = request.UntilUtc?.ToUniversalTime() };
    }

    /// <summary>Raíz de recurso o destino UNC (la validación de formato con <see cref="UncPaths"/> es de cada historia).</summary>
    public static string Destination(string? value)
    {
        value = Require(value);
        var v = new Collector();
        v.MaxLength(string.Empty, value, ContractLimits.MaxDestinationLength);
        v.ThrowIfAny();
        return value;
    }

    /// <summary>Credencial opcional (<c>TestConnectionAsync</c>); <see langword="null"/> se admite.</summary>
    public static CredentialInput? OptionalCredential(CredentialInput? credential)
    {
        if (credential is null)
        {
            return null;
        }

        var v = new Collector();
        v.MaxLength(nameof(CredentialInput.UserName), credential.UserName, ContractLimits.MaxAccountNameLength);
        v.MaxLength(nameof(CredentialInput.Password), credential.Password, ContractLimits.MaxPasswordLength);
        v.ThrowIfAny();
        return credential;
    }

    public static CredentialInput Credential(CredentialInput? credential)
    {
        var required = Require(credential);
        OptionalCredential(required);
        return required;
    }

    public static HistoryQuery History(HistoryQuery? query)
    {
        query = Require(query);
        var v = new Collector();
        var normalized = query with
        {
            FromUtc = query.FromUtc?.ToUniversalTime(),
            ToUtc = query.ToUtc?.ToUniversalTime(),
        };

        for (var i = 0; i < normalized.States.Count; i++)
        {
            v.Defined($"{nameof(HistoryQuery.States)}[{i}]", normalized.States[i]);
        }

        if (normalized.FromUtc is { } from && normalized.ToUtc is { } to && from > to)
        {
            v.Fail(nameof(HistoryQuery.FromUtc), ValidationIssueCode.ValueOutOfRange);
        }

        v.Page(nameof(HistoryQuery.Offset), normalized.Offset, nameof(HistoryQuery.Limit), normalized.Limit);
        v.ThrowIfAny();
        return normalized;
    }

    public static void SnapshotId(string? snapshotId)
    {
        var v = new Collector();
        v.SnapshotIdentifier(string.Empty, snapshotId);
        v.ThrowIfAny();
    }

    public static void Browse(string? snapshotId, string? path, int offset, int limit)
    {
        var v = new Collector();
        v.SnapshotIdentifier("snapshotId", snapshotId);
        v.SnapshotPath("path", path, allowRoot: true);
        v.Page("offset", offset, "limit", limit);
        v.ThrowIfAny();
    }

    public static void Search(string? snapshotId, string? text, int limit)
    {
        var v = new Collector();
        v.SnapshotIdentifier("snapshotId", snapshotId);
        if (string.IsNullOrEmpty(text))
        {
            v.Fail("text", ValidationIssueCode.ValueOutOfRange);
        }
        else
        {
            v.MaxLength("text", text, ContractLimits.MaxSearchTextLength);
        }

        v.Page(null, 0, "limit", limit);
        v.ThrowIfAny();
    }

    public static void SnapshotFilePath(string? path)
    {
        var v = new Collector();
        v.SnapshotPath(string.Empty, path, allowRoot: false);
        v.ThrowIfAny();
    }

    public static RestoreRequest Restore(RestoreRequest? request)
    {
        request = Require(request);
        var v = new Collector();
        v.SnapshotIdentifier(nameof(RestoreRequest.SnapshotId), request.SnapshotId);
        v.Defined(nameof(RestoreRequest.ConflictPolicy), request.ConflictPolicy);
        if (request.Paths.Count == 0)
        {
            v.Fail(nameof(RestoreRequest.Paths), ValidationIssueCode.ValueOutOfRange);
        }

        v.MaxCount(nameof(RestoreRequest.Paths), request.Paths.Count, ContractLimits.MaxRestorePaths);
        for (var i = 0; i < request.Paths.Count && i < ContractLimits.MaxRestorePaths; i++)
        {
            v.SnapshotPath($"{nameof(RestoreRequest.Paths)}[{i}]", request.Paths[i], allowRoot: false);
        }

        if (request.TargetFolder is { } target)
        {
            v.MaxLength(nameof(RestoreRequest.TargetFolder), target, ContractLimits.MaxLocalPathLength);
        }

        v.ThrowIfAny();
        return request;
    }

    public static void ConflictResolution(ConflictResolution resolution)
    {
        var v = new Collector();
        v.Defined("resolution", resolution);
        v.ThrowIfAny();
    }

    public static ServiceSettings Settings(ServiceSettings? settings)
    {
        settings = Require(settings);
        var v = new Collector();
        if (settings.MissedRunGracePeriod < TimeSpan.Zero || settings.MissedRunGracePeriod > ServiceSettingsDefaults.MaxMissedRunGracePeriod)
        {
            v.Fail(nameof(ServiceSettings.MissedRunGracePeriod), ValidationIssueCode.ValueOutOfRange);
        }

        if (settings.OutdatedAfterDays is < 1 or > ServiceSettingsDefaults.MaxOutdatedAfterDays)
        {
            v.Fail(nameof(ServiceSettings.OutdatedAfterDays), ValidationIssueCode.ValueOutOfRange);
        }

        v.ThrowIfAny();
        return settings;
    }

    /// <summary>Cuenta o SID recibido en <c>AddAuthorizedUserAsync</c>/<c>RemoveAuthorizedUserAsync</c>.</summary>
    public static string Account(string? account)
    {
        account = Require(account);
        var v = new Collector();
        v.MaxLength(string.Empty, account, ContractLimits.MaxAccountNameLength);
        v.ThrowIfAny();
        return account;
    }

    public static DiagnosticsExportRequest Diagnostics(DiagnosticsExportRequest? request)
    {
        request = Require(request);
        var v = new Collector();
        v.MaxLength(nameof(DiagnosticsExportRequest.TargetPath), request.TargetPath, ContractLimits.MaxLocalPathLength);
        v.ThrowIfAny();
        return request;
    }

    /// <summary>Acumula errores y lanza <see cref="BackboneException"/> con <see cref="BackboneErrorCode.ValidationFailed"/>.</summary>
    private sealed class Collector
    {
        private List<ValidationIssue>? _errors;

        public void Fail(string field, ValidationIssueCode code, string? value = null) =>
            (_errors ??= []).Add(new ValidationIssue { Field = field, Code = code, Value = value });

        public bool NotNull(string field, object? value)
        {
            if (value is null)
            {
                Fail(field, ValidationIssueCode.ValueOutOfRange);
                return false;
            }

            return true;
        }

        public void MaxLength(string field, string? value, int max)
        {
            if (value is not null && value.Length > max)
            {
                Fail(field, ValidationIssueCode.LimitExceeded);
            }
        }

        public void MaxCount(string field, int count, int max)
        {
            if (count > max)
            {
                Fail(field, ValidationIssueCode.LimitExceeded);
            }
        }

        public void Defined<T>(string field, T value)
            where T : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                Fail(field, ValidationIssueCode.ValueOutOfRange, Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
        }

        public void DefinedFlags<T>(string field, T value, T allDefined)
            where T : struct, Enum
        {
            var bits = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            var allowed = Convert.ToInt64(allDefined, CultureInfo.InvariantCulture);
            if ((bits & ~allowed) != 0)
            {
                Fail(field, ValidationIssueCode.ValueOutOfRange, bits.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void Page(string? offsetField, int offset, string limitField, int limit)
        {
            if (offsetField is not null && offset < 0)
            {
                Fail(offsetField, ValidationIssueCode.ValueOutOfRange);
            }

            if (limit < 1)
            {
                Fail(limitField, ValidationIssueCode.ValueOutOfRange);
            }
            else if (limit > ContractLimits.MaxPageSize)
            {
                Fail(limitField, ValidationIssueCode.LimitExceeded);
            }
        }

        public void SnapshotIdentifier(string field, string? snapshotId)
        {
            if (!SnapshotPaths.IsValidSnapshotId(snapshotId))
            {
                Fail(field, ValidationIssueCode.SnapshotPathInvalid);
            }
        }

        public void SnapshotPath(string field, string? path, bool allowRoot)
        {
            if (path is not null && path.Length > ContractLimits.MaxSnapshotPathLength)
            {
                Fail(field, ValidationIssueCode.LimitExceeded);
            }
            else if (!SnapshotPaths.IsValidPath(path, allowRoot))
            {
                Fail(field, ValidationIssueCode.SnapshotPathInvalid);
            }
        }

        public void ThrowIfAny()
        {
            if (_errors is { Count: > 0 })
            {
                throw BackboneException.ValidationFailed(new ValidationResult { Errors = _errors });
            }
        }
    }
}
