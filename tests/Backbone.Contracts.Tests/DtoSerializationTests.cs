using System.Text.Json;

namespace Backbone.Contracts.Tests;

/// <summary>
/// HT-02 criterio 2: cada DTO se serializa y deserializa con System.Text.Json, con las opciones de
/// <c>SystemTextJsonFormatter</c>, sin pérdida. Los DTO se descubren por reflexión: un DTO nuevo queda cubierto solo.
/// </summary>
[Trait("Story", "HT-02")]
public sealed class DtoSerializationTests
{
    public static TheoryData<Type> AllRecords() => new(ContractTypes.Records);

    [Fact]
    public void Discovery_finds_the_DTOs_of_the_interface()
    {
        // Comprobación de cordura del descubrimiento por reflexión.
        Assert.Contains(typeof(JobDefinition), ContractTypes.Records);
        Assert.Contains(typeof(RestoreProgressEvent), ContractTypes.Records);
        Assert.Contains(typeof(BackboneErrorData), ContractTypes.Records);
        Assert.True(ContractTypes.Records.Count >= 30, $"Solo se han encontrado {ContractTypes.Records.Count} DTO.");
    }

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void Every_DTO_round_trips_without_loss(Type type)
    {
        var sample = ContractTypes.CreateSample(type);

        var json = JsonSerializer.Serialize(sample, type, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize(json, type, ContractTypes.FormatterOptions);

        var difference = ContractTypes.FindDifference(sample, copy, type.Name);
        Assert.True(difference is null, $"{difference}\nJSON: {json}");

        // Y una segunda vuelta produce exactamente el mismo JSON.
        Assert.Equal(json, JsonSerializer.Serialize(copy, type, ContractTypes.FormatterOptions));
    }

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void Every_DTO_deserializes_from_an_empty_object_with_its_defaults(Type type)
    {
        // Compatibilidad entre versiones menores: un campo que el otro extremo no conoce llega con su valor por defecto.
        var expected = Activator.CreateInstance(type);

        var actual = JsonSerializer.Deserialize("{}", type, ContractTypes.FormatterOptions);

        Assert.Null(ContractTypes.FindDifference(expected, actual, type.Name));
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        // Compatibilidad entre versiones menores: un campo nuevo no rompe a un cliente antiguo.
        var job = JsonSerializer.Deserialize<JobDefinition>(
            """{ "Name": "Documentos", "CampoDeUnaVersionFutura": { "x": 1 } }""", ContractTypes.FormatterOptions);

        Assert.Equal("Documentos", job?.Name);
    }

    [Fact]
    public void Formatter_default_options_use_property_names_as_is_and_numeric_enums()
    {
        var json = JsonSerializer.Serialize(
            new RunRecord { RunId = 7, State = RunState.CompletedWithWarnings, ErrorCode = BackboneErrorCode.NotSupported },
            ContractTypes.FormatterOptions);

        // §7.1: servicio y app usan SystemTextJsonFormatter sin cambiar sus opciones; los nombres viajan tal cual
        // (PascalCase) y los enumerados como número (valores explícitos y estables).
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("RunId").GetInt64());
        Assert.Equal((int)RunState.CompletedWithWarnings, doc.RootElement.GetProperty("State").GetInt32());
        Assert.Equal(1014, doc.RootElement.GetProperty("ErrorCode").GetInt32());
    }

    [Fact]
    public void Warnings_truncated_issue_code_round_trips_with_its_stable_value()
    {
        // Contrato 1.1 (§6.3): resumen de los avisos que no caben en el snapshot.
        var issue = new RunIssue { Severity = IssueSeverity.Warning, Code = RunIssueCode.WarningsTruncated, Message = "3 avisos más no se han guardado en el snapshot." };

        var json = JsonSerializer.Serialize(issue, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<RunIssue>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(12, doc.RootElement.GetProperty("Code").GetInt32());
        Assert.Equal(issue, copy);
    }

    [Fact]
    public void Source_root_unreadable_issue_code_round_trips_with_its_stable_value()
    {
        // Contrato 1.2 (§8.4 "Memoria del motor", R11): la raíz de un origen no se pudo recorrer; Path es el alias.
        var issue = new RunIssue
        {
            Severity = IssueSeverity.Warning,
            Code = RunIssueCode.SourceRootUnreadable,
            Path = "Sistema",
            Message = "La carpeta tiene demasiados elementos para recorrerla con la memoria reservada a la copia.",
        };

        var json = JsonSerializer.Serialize(issue, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<RunIssue>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(13, doc.RootElement.GetProperty("Code").GetInt32());
        Assert.Equal(issue, copy);
    }

    [Fact]
    public void Snapshot_too_large_error_code_round_trips_in_a_run_record_with_its_stable_value()
    {
        // Contrato 1.2 (§6.3, §7.4): copia con demasiados archivos y carpetas para un solo snapshot.
        var record = new RunRecord { RunId = 9, State = RunState.Failed, ErrorCode = BackboneErrorCode.SnapshotTooLarge, ErrorMessage = "Entry limit exceeded." };

        var json = JsonSerializer.Serialize(record, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<RunRecord>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1017, doc.RootElement.GetProperty("ErrorCode").GetInt32());
        Assert.Null(ContractTypes.FindDifference(record, copy, nameof(RunRecord)));

        var data = new BackboneErrorData { Code = BackboneErrorCode.SnapshotTooLarge, Message = "Entry limit exceeded." };
        Assert.Equal(data, JsonSerializer.Deserialize<BackboneErrorData>(JsonSerializer.Serialize(data, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions));
    }

    [Theory]
    [InlineData(ValidationIssueCode.SourceNotAllowed, 26)]
    [InlineData(ValidationIssueCode.SourceContainsDataFolder, 27)]
    public void Data_folder_validation_codes_round_trip_with_their_stable_values(ValidationIssueCode code, int expected)
    {
        // Contrato 1.2 (ADR-16, §7.5): error y aviso de la carpeta de datos, en Sources[i].Path.
        var result = code == ValidationIssueCode.SourceNotAllowed
            ? new ValidationResult { Errors = [new ValidationIssue { Field = "Sources[0].Path", Code = code, Value = @"C:\ProgramData\Backbone" }] }
            : new ValidationResult { Warnings = [new ValidationIssue { Field = "Sources[0].Path", Code = code, Value = @"C:\" }] };

        var json = JsonSerializer.Serialize(result, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<ValidationResult>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        var list = code == ValidationIssueCode.SourceNotAllowed ? "Errors" : "Warnings";
        Assert.Equal(expected, doc.RootElement.GetProperty(list)[0].GetProperty("Code").GetInt32());
        Assert.Null(ContractTypes.FindDifference(result, copy, nameof(ValidationResult)));
        Assert.Equal(code == ValidationIssueCode.SourceContainsDataFolder, copy?.IsValid);
    }

    [Theory]
    [InlineData(ValidationIssueCode.SourceUnreadable, 28, @"C:\Users\ana\OneDrive\Fotos")]
    [InlineData(ValidationIssueCode.SourcePathInvalid, 29, @"C:\ProgramData::$INDEX_ALLOCATION")]
    public void Source_resolution_validation_codes_round_trip_with_their_stable_values(ValidationIssueCode code, int expected, string path)
    {
        // Contrato 1.3 (ADR-20, §7.5 "Ruta de origen"): errores de origen en Sources[i].Path.
        var result = new ValidationResult { Errors = [new ValidationIssue { Field = "Sources[1].Path", Code = code, Value = path }] };

        var json = JsonSerializer.Serialize(result, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<ValidationResult>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("Errors")[0].GetProperty("Code").GetInt32());
        Assert.Null(ContractTypes.FindDifference(result, copy, nameof(ValidationResult)));
        Assert.False(copy?.IsValid);
    }

    [Fact]
    public void Protected_folders_unavailable_round_trips_with_its_stable_value_and_a_null_value()
    {
        // Contrato 1.4 (§8.3, PD desconocida): error en Sources[i].Path, sin Value.
        var result = new ValidationResult { Errors = [new ValidationIssue { Field = "Sources[0].Path", Code = ValidationIssueCode.ProtectedFoldersUnavailable }] };

        var json = JsonSerializer.Serialize(result, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<ValidationResult>(json, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        var issue = doc.RootElement.GetProperty("Errors")[0];
        Assert.Equal(30, issue.GetProperty("Code").GetInt32());
        Assert.Null(ContractTypes.FindDifference(result, copy, nameof(ValidationResult)));
        Assert.Null(copy?.Errors[0].Value);
        Assert.False(copy?.IsValid);
    }

    [Fact]
    public void A_validation_code_newer_than_this_contract_is_kept_as_an_undefined_value()
    {
        // Compatibilidad de versiones menores: así ve un cliente 1.3 el código 30, y este cliente uno futuro.
        const string json = """{"Errors":[{"Field":"Sources[0].Path","Code":31}]}""";

        var copy = JsonSerializer.Deserialize<ValidationResult>(json, ContractTypes.FormatterOptions);

        Assert.NotNull(copy);
        var code = Assert.Single(copy.Errors).Code;
        Assert.Equal(31, (int)code);
        Assert.False(Enum.IsDefined(code));
        Assert.False(copy.IsValid);
    }

    [Fact]
    public void Dates_keep_UTC_and_100ns_precision()
    {
        var instant = new DateTimeOffset(2026, 1, 31, 10, 22, 1, TimeSpan.Zero).AddTicks(1_234_567);
        var item = new SnapshotItem { Path = "Documentos/enero.pdf", ModifiedUtc = instant };

        var json = JsonSerializer.Serialize(item, ContractTypes.FormatterOptions);
        var copy = JsonSerializer.Deserialize<SnapshotItem>(json, ContractTypes.FormatterOptions);

        Assert.Contains("2026-01-31T10:22:01.1234567+00:00", json, StringComparison.Ordinal);
        Assert.Equal(instant.UtcTicks, copy?.ModifiedUtc?.UtcTicks);
        Assert.Equal(TimeSpan.Zero, copy?.ModifiedUtc?.Offset);
    }

    [Fact]
    public void Validation_result_IsValid_is_computed_and_not_serialized()
    {
        var invalid = new ValidationResult { Errors = [new ValidationIssue { Field = "Name", Code = ValidationIssueCode.NameRequired }] };

        var json = JsonSerializer.Serialize(invalid, ContractTypes.FormatterOptions);

        Assert.False(invalid.IsValid);
        Assert.True(new ValidationResult().IsValid);
        Assert.DoesNotContain("isValid", json, StringComparison.OrdinalIgnoreCase);
    }
}
