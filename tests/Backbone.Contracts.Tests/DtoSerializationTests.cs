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
