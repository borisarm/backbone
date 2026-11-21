using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace Backbone.Contracts.Tests;

/// <summary>
/// HT-02 criterios 1, 3 y 4, y convenciones de los DTO (§7.2): <c>record</c> sellados e inmutables, colecciones
/// <see cref="IReadOnlyList{T}"/>, fechas <see cref="DateTimeOffset"/>, sin herencia, sin contraseñas de salida.
/// </summary>
[Trait("Story", "HT-02")]
public sealed class ContractConventionTests
{
    public static TheoryData<Type> AllRecords() => new(ContractTypes.Records);

    // ---- Criterio 1: interfaz de §7.2 ----

    private static readonly string[] SpecMethods =
    [
        "GetServiceInfoAsync", "GetStatusAsync",
        "GetJobsAsync", "SaveJobAsync", "DeleteJobAsync", "ValidateJobAsync",
        "RunNowAsync", "CancelRunAsync", "SetPauseAsync",
        "SaveCredentialAsync", "GetCredentialInfoAsync", "DeleteCredentialAsync", "TestConnectionAsync", "GetStorageInfoAsync",
        "GetHistoryAsync", "GetRunDetailAsync",
        "GetSnapshotsAsync", "BrowseSnapshotAsync", "SearchSnapshotAsync", "GetFileVersionsAsync",
        "StartRestoreAsync", "ResolveRestoreConflictAsync", "CancelRestoreAsync",
        "ApplyRetentionAsync", "StartPruneAsync", "StartCheckAsync",
        "GetSettingsAsync", "SaveSettingsAsync",
        "GetAuthorizedUsersAsync", "AddAuthorizedUserAsync", "RemoveAuthorizedUserAsync",
        "ExportDiagnosticsAsync",
    ];

    private static readonly string[] SpecEvents =
        ["StatusChanged", "RunProgress", "RunCompleted", "RestoreProgress", "RestoreConflict"];

    private static IEnumerable<MethodInfo> InterfaceMethods() =>
        typeof(IBackboneService).GetMethods().Where(m => !m.IsSpecialName);

    [Fact]
    public void Interface_has_exactly_the_methods_and_events_of_the_specification()
    {
        Assert.Equal(SpecMethods.Order(StringComparer.Ordinal), InterfaceMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(SpecEvents.Order(StringComparer.Ordinal), typeof(IBackboneService).GetEvents().Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_method_is_async_with_an_optional_cancellation_token_last()
    {
        foreach (var method in InterfaceMethods())
        {
            Assert.True(typeof(Task).IsAssignableFrom(method.ReturnType), $"{method.Name} no devuelve Task.");
            Assert.EndsWith("Async", method.Name, StringComparison.Ordinal);
            var last = method.GetParameters().Last();
            Assert.Equal(typeof(CancellationToken), last.ParameterType);
            Assert.True(last.HasDefaultValue, $"{method.Name}: el CancellationToken debe ser opcional.");
            Assert.Single(method.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
        }
    }

    [Fact]
    public void Events_use_generic_EventHandler_of_a_contract_record()
    {
        foreach (var e in typeof(IBackboneService).GetEvents())
        {
            var handler = e.EventHandlerType!;
            Assert.Equal(typeof(EventHandler<>), handler.GetGenericTypeDefinition());
            var args = handler.GetGenericArguments()[0];
            Assert.True(ContractTypes.IsRecord(args) && ContractTypes.IsContractType(args), $"{e.Name}: {args} no es un DTO del contrato.");
        }
    }

    [Fact]
    public void Every_type_in_the_interface_signatures_is_a_primitive_or_a_contract_type()
    {
        var allowed = new HashSet<Type>
        {
            typeof(void), typeof(Task), typeof(string), typeof(bool), typeof(int), typeof(long), typeof(Guid), typeof(CancellationToken),
        };

        foreach (var method in InterfaceMethods())
        {
            var types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
            foreach (var type in types.SelectMany(Unwrap))
            {
                Assert.True(allowed.Contains(type) || ContractTypes.IsContractType(type), $"{method.Name}: tipo {type} no permitido.");
            }
        }

        static IEnumerable<Type> Unwrap(Type type)
        {
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                Assert.True(
                    definition == typeof(Task<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(Nullable<>),
                    $"Genérico no permitido en la interfaz: {type}.");
                return type.GetGenericArguments().SelectMany(Unwrap);
            }

            return [type];
        }
    }

    // ---- Criterio 4: métodos F2/F3 presentes y documentados ----

    [Theory]
    [InlineData("SetPauseAsync", "F2")]
    [InlineData("GetStorageInfoAsync", "F2")]
    [InlineData("GetFileVersionsAsync", "F2")]
    [InlineData("ApplyRetentionAsync", "F2")]
    [InlineData("StartPruneAsync", "F2")]
    [InlineData("StartCheckAsync", "F2")]
    [InlineData("ExportDiagnosticsAsync", "F2")]
    public void Later_phase_methods_document_their_phase_and_NotSupported(string method, string phase)
    {
        var summary = XmlDocSummary("M:Backbone.Contracts.IBackboneService." + method + "(");

        Assert.Contains(phase, summary, StringComparison.Ordinal);
        Assert.Contains("NotSupported", summary, StringComparison.Ordinal);
    }

    private static string XmlDocSummary(string memberPrefix) => DocText(XmlDocElement(memberPrefix).Element("summary")!);

    private static string XmlDocMember(string memberPrefix) => DocText(XmlDocElement(memberPrefix));

    private static string DocText(XElement element) =>
        string.Concat(element.DescendantNodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement { Name.LocalName: "see" } s => (string?)s.Attribute("cref") ?? string.Empty,
            _ => string.Empty,
        }));

    private static XElement XmlDocElement(string memberPrefix)
    {
        var xmlPath = Path.ChangeExtension(ContractTypes.Assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), $"No se encuentra la documentación XML {xmlPath}.");
        return XDocument.Load(xmlPath).Descendants("member")
            .Single(m => ((string?)m.Attribute("name"))?.StartsWith(memberPrefix, StringComparison.Ordinal) == true);
    }

    // ---- Criterio 3 y transporte (§7.1) ----

    [Fact]
    public void Contract_version_is_semantic_1_3()
    {
        const string expected = "1.3";
        Assert.Equal(expected, ContractVersion.Value);
        Assert.Equal(ContractVersion.Value, string.Create(CultureInfo.InvariantCulture, $"{ContractVersion.Major}.{ContractVersion.Minor}"));
        Assert.True(ContractVersion.TryParse(ContractVersion.Value, out var major, out var minor));
        Assert.Equal((1, 3), (major, minor));
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.7", true)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("v1.0", false)]
    [InlineData("-1.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Compatibility_requires_the_same_major_version(string? other, bool compatible)
    {
        Assert.Equal(compatible, ContractVersion.IsCompatibleWith(other));
    }

    [Fact]
    public void Pipe_name_matches_the_specification_and_the_major_version()
    {
        Assert.Equal("Backbone.Service.v1", BackboneIpc.PipeName);
        Assert.Equal(@"\\.\pipe\Backbone.Service.v1", BackboneIpc.PipePath);
        Assert.EndsWith(".v" + ContractVersion.Major, BackboneIpc.PipeName, StringComparison.Ordinal);
        Assert.Equal(8, BackboneIpc.MaxConnections);
        Assert.Equal(4, BackboneIpc.MaxRunProgressEventsPerSecond);
    }

    // ---- Códigos de error (§7.4) ----

    [Fact]
    public void Error_codes_are_exactly_those_of_the_specification()
    {
        string[] spec =
        [
            "ValidationFailed", "JobNotFound", "JobAlreadyRunning", "NasUnreachable", "CredentialsRejected", "ShareNotFound",
            "AccessDenied", "RepositoryLocked", "RepositoryIncompatible", "RepositoryCorrupt", "SnapshotNotFound",
            "RestoreTargetNotWritable", "Interrupted", "NotAuthorized", "NotSupported", "Internal", "CredentialRequired",
            "SnapshotTooLarge", "NasBusy",
        ];

        Assert.Equal(spec, Enum.GetNames<BackboneErrorCode>());
    }

    [Fact]
    public void Error_codes_are_unique_and_outside_the_JSON_RPC_reserved_range()
    {
        var values = Enum.GetValues<BackboneErrorCode>().Select(BackboneErrors.ToJsonRpcErrorCode).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, v => Assert.False(v is >= -32768 and <= -32000, $"{v} está en el intervalo reservado."));
        Assert.All(Enum.GetValues<BackboneErrorCode>(), c =>
        {
            Assert.True(BackboneErrors.TryGetErrorCode(BackboneErrors.ToJsonRpcErrorCode(c), out var back));
            Assert.Equal(c, back);
        });
        Assert.False(BackboneErrors.TryGetErrorCode(-32000, out _));
    }

    [Fact]
    public void Error_code_values_are_stable()
    {
        // Los valores viajan por el cable: cambiarlos es un cambio incompatible (versión mayor).
        Assert.Equal(1000, (int)BackboneErrorCode.ValidationFailed);
        Assert.Equal(1014, (int)BackboneErrorCode.NotSupported);
        Assert.Equal(1015, (int)BackboneErrorCode.Internal);
        Assert.Equal(1016, (int)BackboneErrorCode.CredentialRequired);
        Assert.Equal(1017, (int)BackboneErrorCode.SnapshotTooLarge); // contrato 1.2
        Assert.Equal(1018, (int)BackboneErrorCode.NasBusy); // contrato 1.3
        Assert.Equal(1018, Enum.GetValues<BackboneErrorCode>().Max(v => (int)v));
    }

    [Fact]
    public void Validation_issue_code_values_are_stable()
    {
        // Contrato 1.2 (ADR-16): los dos códigos de la carpeta de datos se añaden al final, sin renumerar los anteriores.
        Assert.Equal(25, (int)ValidationIssueCode.SnapshotPathInvalid);
        Assert.Equal(26, (int)ValidationIssueCode.SourceNotAllowed);
        Assert.Equal(27, (int)ValidationIssueCode.SourceContainsDataFolder);
        // Contrato 1.3 (ADR-20, §7.5 "Ruta de origen"): origen no comprobable y ruta de origen no normalizada.
        Assert.Equal(28, (int)ValidationIssueCode.SourceUnreadable);
        Assert.Equal(29, (int)ValidationIssueCode.SourcePathInvalid);
        Assert.Equal(29, Enum.GetValues<ValidationIssueCode>().Max(v => (int)v));
    }

    [Fact]
    public void Run_issue_code_values_are_stable()
    {
        // Viajan por el cable y se guardan por nombre en los avisos del snapshot (§6.3): se añaden al final sin renumerar.
        Assert.Equal(9, (int)RunIssueCode.ReadError);
        Assert.Equal(12, (int)RunIssueCode.WarningsTruncated); // contrato 1.1
        Assert.Equal(13, (int)RunIssueCode.SourceRootUnreadable); // contrato 1.2
        Assert.Equal(13, Enum.GetValues<RunIssueCode>().Max(v => (int)v));
    }

    [Fact]
    public void No_interface_parameter_is_a_loose_password()
    {
        // M-1: una contraseña solo viaja dentro de CredentialInput (cuyo ToString la oculta), nunca como string suelto
        // que un registro de argumentos o una traza podría volcar.
        foreach (var method in InterfaceMethods())
        {
            Assert.DoesNotContain(method.GetParameters(), p => p.Name!.Contains("password", StringComparison.OrdinalIgnoreCase));
        }

        var save = typeof(IBackboneService).GetMethod(nameof(IBackboneService.SaveCredentialAsync))!;
        Assert.Contains(save.GetParameters(), p => p.ParameterType == typeof(CredentialInput));
    }

    [Fact]
    public void Caller_info_only_exposes_the_elevation_computed_by_the_server()
    {
        // C-1: con el modelo "solo cuentas administradoras", gestionar usuarios depende solo de la elevación.
        Assert.Equal(
            ["IsElevated", "UserName", "UserSid"],
            ContractTypes.DataProperties(typeof(CallerInfo)).Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Limits_are_consistent()
    {
        Assert.Equal(4 * 1024 * 1024, BackboneIpc.MaxMessageBytes);
        Assert.Equal(ContractLimits.MaxPageSize, HistoryQuery.MaxLimit);
        Assert.InRange(HistoryQuery.DefaultLimit, 1, HistoryQuery.MaxLimit);
        Assert.Equal(64, ContractLimits.MaxJsonDepth);
        // Las opciones del formatter no cambian la profundidad máxima por defecto de System.Text.Json (0 = 64).
        Assert.True(ContractTypes.FormatterOptions.MaxDepth is 0 or ContractLimits.MaxJsonDepth);
        // Una petición de restauración al máximo de rutas largas cabe en un mensaje si las rutas son razonables (~1 KB).
        Assert.True(ContractLimits.MaxRestorePaths * 1024 < BackboneIpc.MaxMessageBytes);
    }

    // ---- Convenciones de los DTO ----

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void DTOs_are_sealed_records_without_inheritance_and_with_a_parameterless_constructor(Type type)
    {
        Assert.True(type.IsSealed, $"{type.Name} debe ser sealed.");
        Assert.Equal(typeof(object), type.BaseType);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
    }

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void DTO_properties_are_init_only_and_use_contract_friendly_types(Type type)
    {
        foreach (var property in ContractTypes.DataProperties(type))
        {
            var where = $"{type.Name}.{property.Name}";
            Assert.True(ContractTypes.IsInitOnly(property), $"{where} debe ser init, no set.");

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            Assert.NotEqual(typeof(DateTime), propertyType);
            Assert.False(propertyType.IsArray, $"{where}: usa IReadOnlyList<T>, no un array.");
            if (propertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                Assert.NotNull(ContractTypes.ReadOnlyListElementType(propertyType));
            }
        }
    }

    [Fact]
    public void Contract_enums_have_a_defined_zero_or_are_flags()
    {
        // Un valor por defecto definido evita estados imposibles al deserializar "{}"; BackboneErrorCode es la excepción
        // documentada (sus valores son los códigos JSON-RPC y su valor por defecto en BackboneErrorData es Internal).
        foreach (var type in ContractTypes.Enums.Where(t => t != typeof(BackboneErrorCode)))
        {
            Assert.True(Enum.IsDefined(type, Activator.CreateInstance(type)!), $"{type.Name} no define el valor 0.");
        }
    }

    [Fact]
    public void Only_CredentialInput_carries_a_password_and_its_ToString_hides_it()
    {
        var withPassword = ContractTypes.Records
            .Where(t => t.GetProperties().Any(p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Equal([typeof(CredentialInput)], withPassword);
        Assert.DoesNotContain(typeof(CredentialInfo).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));

        var input = new CredentialInput { UserName = "backup", Password = "s3cr3t-P@ss" };
        Assert.DoesNotContain("s3cr3t-P@ss", input.ToString(), StringComparison.Ordinal);
        Assert.Contains("backup", input.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AddAuthorizedUserAsync")]
    [InlineData("RemoveAuthorizedUserAsync")]
    public void Authorized_user_changes_document_elevation_and_NotAuthorized(string method)
    {
        var member = XmlDocMember("M:Backbone.Contracts.IBackboneService." + method + "(");

        Assert.Contains("elevado", member, StringComparison.Ordinal);
        Assert.Contains("NotAuthorized", member, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_defaults_match_the_specification()
    {
        var settings = new ServiceSettings();

        Assert.Equal(TimeSpan.FromMinutes(5), settings.MissedRunGracePeriod);
        Assert.Equal(3, settings.OutdatedAfterDays);
        Assert.True(settings.Notifications.OnWarnings);
        Assert.False(settings.Notifications.OnSuccess);
        Assert.True(settings.Notifications.OnOutdated);
        Assert.True(settings.Notifications.OnLowSpace);
    }

    [Fact]
    public void Job_defaults_match_the_specification()
    {
        var job = JobDefaults.NewJob();

        Assert.Equal(Guid.Empty, job.Id);
        Assert.True(job.Enabled);
        Assert.Equal(["desktop.ini", "thumbs.db", "~$*", "*.tmp", "$RECYCLE.BIN", "System Volume Information"], job.Exclusions);
        Assert.Equal((3, 7, 4, 12, 0), (job.Retention.KeepLast, job.Retention.KeepDaily, job.Retention.KeepWeekly, job.Retention.KeepMonthly, job.Retention.KeepYearly));
        Assert.Equal(30, job.Conditions.MinBatteryPercent);
        Assert.True(job.Conditions.SkipOnMeteredConnection);
        Assert.True(job.Conditions.RequireNasReachable);
        Assert.Equal(ScheduleKind.Manual, job.Schedule.Kind);
        Assert.True(job.Schedule.CatchUpMissedRuns);
    }
}
