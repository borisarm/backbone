using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backbone.Contracts.Tests;

/// <summary>
/// HT-02 criterio 2, casos límite que complementan a <see cref="DtoSerializationTests"/>: opcionales a <c>null</c> y
/// colecciones vacías por separado, instancias por defecto, valores extremos, texto Unicode y rutas largas, y
/// propiedades que no viajarían (sin <c>init</c>, <c>[JsonIgnore]</c>, campos públicos).
/// </summary>
[Trait("Story", "HT-02")]
public sealed class DtoEdgeCaseTests
{
    public static TheoryData<Type> AllRecords() => new(ContractTypes.Records);

    /// <summary>Cada propiedad opcional (nullable o lista) de cada DTO, para probarla a <c>null</c>/vacía de forma aislada.</summary>
    private static List<(Type Type, string Name)> OptionalPropertyList()
    {
        var data = new List<(Type Type, string Name)>();
        var nullability = new NullabilityInfoContext();
        foreach (var type in ContractTypes.Records)
        {
            foreach (var property in ContractTypes.DataProperties(type))
            {
                if (IsOptional(property, nullability))
                {
                    data.Add((type, property.Name));
                }
            }
        }

        return data;
    }

    public static TheoryData<Type, string> OptionalProperties()
    {
        var data = new TheoryData<Type, string>();
        foreach (var (type, name) in OptionalPropertyList())
        {
            data.Add(type, name);
        }

        return data;
    }

    private static bool IsOptional(PropertyInfo property, NullabilityInfoContext nullability) =>
        ContractTypes.ReadOnlyListElementType(property.PropertyType) is not null
        || Nullable.GetUnderlyingType(property.PropertyType) is not null
        || (!property.PropertyType.IsValueType && nullability.Create(property).WriteState == NullabilityState.Nullable);

    private static string RoundTripJson(object value, Type type, out object? copy)
    {
        var json = JsonSerializer.Serialize(value, type, ContractTypes.FormatterOptions);
        copy = JsonSerializer.Deserialize(json, type, ContractTypes.FormatterOptions);
        return json;
    }

    [Fact]
    public void Discovery_covers_optional_properties_of_every_kind()
    {
        // Cordura del generador: hay opcionales nullable de valor, nullable de referencia y listas.
        var all = OptionalPropertyList();

        Assert.Contains((typeof(JobDefinition), nameof(JobDefinition.MaxFileSizeBytes)), all);
        Assert.Contains((typeof(RunRecord), nameof(RunRecord.ErrorMessage)), all);
        Assert.Contains((typeof(ServiceStatus), nameof(ServiceStatus.CurrentRun)), all);
        Assert.Contains((typeof(JobDefinition), nameof(JobDefinition.Sources)), all);
        Assert.Contains((typeof(RunProgressEvent), nameof(RunProgressEvent.EstimatedRemaining)), all);
        Assert.True(all.Count >= 40, $"Solo {all.Count} propiedades opcionales descubiertas.");
    }

    [Theory]
    [MemberData(nameof(OptionalProperties))]
    public void An_optional_property_set_to_null_or_empty_round_trips_without_affecting_the_rest(Type type, string propertyName)
    {
        // Complementa el ejemplo "todo relleno": aquí el resto está relleno y solo una propiedad es null / vacía,
        // así que un convertidor que confunda null con ausente, o [] con null, se detecta propiedad a propiedad.
        var sample = ContractTypes.CreateSample(type);
        var property = type.GetProperty(propertyName)!;
        var element = ContractTypes.ReadOnlyListElementType(property.PropertyType);
        property.SetValue(sample, element is null ? null : Activator.CreateInstance(typeof(List<>).MakeGenericType(element)));

        var json = RoundTripJson(sample, type, out var copy);

        var difference = ContractTypes.FindDifference(sample, copy, type.Name);
        Assert.True(difference is null, $"{difference}\nJSON: {json}");
        if (element is not null)
        {
            Assert.NotNull(property.GetValue(copy));
            Assert.Empty((System.Collections.IEnumerable)property.GetValue(copy)!);
        }
        else
        {
            Assert.Null(property.GetValue(copy));
        }
    }

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void A_default_instance_round_trips_with_nulls_and_empty_collections(Type type)
    {
        var instance = Activator.CreateInstance(type)!;

        var json = RoundTripJson(instance, type, out var copy);

        var difference = ContractTypes.FindDifference(instance, copy, type.Name);
        Assert.True(difference is null, $"{difference}\nJSON: {json}");
    }

    [Fact]
    public void Extreme_numbers_and_dates_survive()
    {
        var stats = new RunStats
        {
            Files = long.MaxValue,
            Bytes = long.MaxValue,
            NewFiles = 0,
            ChangedFiles = long.MinValue,
            NewObjects = 9_007_199_254_740_993L, // 2^53 + 1: no representable en double (JavaScript, etc.)
            NewBytes = 1,
            Skipped = int.MaxValue + 1L,
        };
        var item = new SnapshotItem
        {
            Path = "Documentos/vacio.txt",
            Name = "vacio.txt",
            Size = 0, // archivo de 0 bytes: distinto de "sin dato"
            ModifiedUtc = DateTimeOffset.MinValue,
            Attributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive,
        };
        var last = new SnapshotItem { ModifiedUtc = DateTimeOffset.MaxValue };

        foreach (var value in new object[] { stats, item, last })
        {
            var json = RoundTripJson(value, value.GetType(), out var copy);
            var difference = ContractTypes.FindDifference(value, copy, value.GetType().Name);
            Assert.True(difference is null, $"{difference}\nJSON: {json}");
        }
    }

    [Fact]
    public void A_zero_size_file_is_not_confused_with_a_missing_value()
    {
        var withZero = new SnapshotItem { Size = 0, ModifiedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var withoutDate = new SnapshotItem { Size = 0, ModifiedUtc = null };

        var copyA = JsonSerializer.Deserialize<SnapshotItem>(JsonSerializer.Serialize(withZero, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions);
        var copyB = JsonSerializer.Deserialize<SnapshotItem>(JsonSerializer.Serialize(withoutDate, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions);

        Assert.Equal(0, copyA?.Size);
        Assert.NotNull(copyA?.ModifiedUtc);
        Assert.Null(copyB?.ModifiedUtc);
    }

    [Fact]
    public void Long_unicode_paths_survive()
    {
        // RNF-09: rutas de más de 260 caracteres; nombres Unicode (par sustituto, combinación, CJK, RTL) y barras invertidas.
        var segment = "Carpeta-ñandú-日本語-\U0001F4C1-e\u0301-مرحبا";
        var snapshotPath = string.Join('/', Enumerable.Repeat(segment, 12)) + "/archivo con \"comillas\" y \\barra.txt";
        var windowsPath = @"C:\Usuarios\" + string.Join('\\', Enumerable.Repeat(segment, 12)) + @"\archivo.txt";
        Assert.True(snapshotPath.Length > 260 && windowsPath.Length > 260);

        var item = new SnapshotItem { Path = snapshotPath, Name = "archivo.txt" };
        var source = new JobSource { Alias = "Documentos", Path = windowsPath };
        var issue = new RunIssue { Path = windowsPath, Message = "línea 1\nlínea 2\t\u0001" };

        foreach (var value in new object[] { item, source, issue })
        {
            var json = RoundTripJson(value, value.GetType(), out var copy);
            var difference = ContractTypes.FindDifference(value, copy, value.GetType().Name);
            Assert.True(difference is null, $"{difference}\nJSON: {json}");
        }
    }

    [Fact]
    public void Case_differences_in_paths_and_aliases_are_preserved()
    {
        var sources = new JobSource[]
        {
            new() { Alias = "Docs", Path = @"C:\Datos" },
            new() { Alias = "docs", Path = @"C:\DATOS" },
        };
        var job = new JobDefinition { Sources = sources };

        var copy = JsonSerializer.Deserialize<JobDefinition>(
            JsonSerializer.Serialize(job, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions);

        Assert.Equal(["Docs", "docs"], copy?.Sources.Select(s => s.Alias));
        Assert.Equal([@"C:\Datos", @"C:\DATOS"], copy?.Sources.Select(s => s.Path));
    }

    [Fact]
    public void Order_of_collections_is_preserved()
    {
        var schedule = new JobSchedule { TimesOfDay = [new TimeOnly(23, 59, 59, 999), new TimeOnly(0, 0), new TimeOnly(13, 0)] };

        var copy = JsonSerializer.Deserialize<JobSchedule>(
            JsonSerializer.Serialize(schedule, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions);

        Assert.Equal(schedule.TimesOfDay, copy?.TimesOfDay);
    }

    [Fact]
    public void Local_times_of_day_are_not_shifted_by_serialization()
    {
        // §7.5: TimeOnly en hora local del equipo, no un instante: viaja como hora de reloj sin desplazamiento.
        var json = JsonSerializer.Serialize(new JobSchedule { TimesOfDay = [new TimeOnly(2, 30)] }, ContractTypes.FormatterOptions);

        using var doc = JsonDocument.Parse(json);
        Assert.StartsWith("02:30:00", doc.RootElement.GetProperty("TimesOfDay")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Flags_enums_combine_and_unknown_numeric_values_are_tolerated()
    {
        // §7.5: "Un extremo debe tolerar valores que no conoce (llegan como número no definido)".
        var weekdays = DaysOfWeek.Monday | DaysOfWeek.Friday | DaysOfWeek.Sunday;
        var schedule = JsonSerializer.Deserialize<JobSchedule>(
            JsonSerializer.Serialize(new JobSchedule { Days = weekdays }, ContractTypes.FormatterOptions), ContractTypes.FormatterOptions);
        Assert.Equal(weekdays, schedule?.Days);

        var run = JsonSerializer.Deserialize<RunRecord>(
            """{ "State": 99, "Kind": 42, "Trigger": -1, "ErrorCode": 9999 }""", ContractTypes.FormatterOptions);
        Assert.Equal((RunState)99, run?.State);
        Assert.Equal((RunKind)42, run?.Kind);
        Assert.Equal((RunTrigger)(-1), run?.Trigger);
        Assert.Equal((BackboneErrorCode)9999, run?.ErrorCode);
        Assert.False(BackboneErrors.TryGetErrorCode(9999, out _));
    }

    [Fact]
    public void Enums_travel_as_numbers_and_a_string_name_is_rejected()
    {
        // ADR-10: sin JsonStringEnumConverter. Si alguien lo añade a las opciones del formatter, este contrato cambia.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunRecord>("""{ "State": "Failed" }""", ContractTypes.FormatterOptions));
        Assert.Equal(RunState.Failed, JsonSerializer.Deserialize<RunRecord>("""{ "State": 4 }""", ContractTypes.FormatterOptions)?.State);
    }

    [Fact]
    public void Property_names_are_case_sensitive_so_a_misspelled_field_is_not_silently_accepted()
    {
        // Documenta el comportamiento de las opciones por defecto: los nombres viajan tal cual (PascalCase).
        var job = JsonSerializer.Deserialize<JobDefinition>("""{ "name": "minúscula", "Name": "exacto" }""", ContractTypes.FormatterOptions);

        Assert.Equal("exacto", job?.Name);
        Assert.Equal(string.Empty, JsonSerializer.Deserialize<JobDefinition>("""{ "name": "minúscula" }""", ContractTypes.FormatterOptions)?.Name);
    }

    [Theory]
    [InlineData("""{ "Name": 5 }""")]
    [InlineData("""{ "Enabled": "sí" }""")]
    [InlineData("""{ "Sources": {} }""")]
    [InlineData("""{ "Sources": [ 1, 2 ] }""")]
    [InlineData("""{ "Id": "no-es-un-guid" }""")]
    [InlineData("""{ "Schedule": { "TimesOfDay": [ "25:99" ] } }""")]
    [InlineData("""{ "MaxFileSizeBytes": "grande" }""")]
    [InlineData("""{ "Name": """)]
    [InlineData("[]")]
    [InlineData("42")]
    public void Malformed_JSON_for_a_DTO_fails_with_JsonException_and_nothing_else(string json)
    {
        // RNF-08: la entrada malformada se rechaza con un error de deserialización controlado, nunca otra excepción.
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<JobDefinition>(json, ContractTypes.FormatterOptions));
    }

    [Fact]
    public void Null_collections_and_nested_records_from_a_peer_are_normalized_to_empty_values()
    {
        // RNF-08: un cliente (o una versión futura) puede enviar null explícito. Los DTO declaran estas propiedades
        // como no nulas; si llegan null, el servicio se expone a NullReferenceException al validar.
        var job = JsonSerializer.Deserialize<JobDefinition>(
            """{ "Name": "x", "Sources": null, "Exclusions": null, "Schedule": null, "Retention": null, "Conditions": null, "Destination": null }""",
            ContractTypes.FormatterOptions);

        Assert.NotNull(job);
        Assert.NotNull(job.Sources);
        Assert.NotNull(job.Exclusions);
        Assert.NotNull(job.Schedule);
        Assert.NotNull(job.Retention);
        Assert.NotNull(job.Conditions);
        Assert.NotNull(job.Destination);
    }

    // ---- Propiedades que no viajarían ----

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void Every_public_property_is_init_or_explicitly_not_serialized_and_there_are_no_public_fields(Type type)
    {
        // Una propiedad solo con get (p. ej. `public int X { get; }`) se serializa pero no se deserializa; el generador de
        // ejemplos no la ve (solo recorre propiedades con setter), así que la ida y vuelta no la detectaría.
        var computed = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract"))
        {
            if (property.SetMethod is null)
            {
                Assert.True(
                    property.IsDefined(typeof(JsonIgnoreAttribute)),
                    $"{type.Name}.{property.Name} no tiene init y no está marcada [JsonIgnore]: no viajaría de vuelta.");
                computed.Add(property.Name);
            }
        }

        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        // Las únicas calculadas admitidas están enumeradas aquí; una nueva exige decidirlo conscientemente.
        Assert.All(computed, name => Assert.Contains((type, name), AllowedComputedProperties));
    }

    private static readonly (Type, string)[] AllowedComputedProperties = [(typeof(ValidationResult), nameof(ValidationResult.IsValid))];

    [Fact]
    public void No_DTO_property_overrides_the_wire_format_with_serialization_attributes()
    {
        // Los nombres viajan tal cual (§7.1) y los enumerados como número: ni [JsonPropertyName] ni convertidores propios.
        foreach (var type in ContractTypes.Records.Concat(ContractTypes.Enums))
        {
            AssertNoFormatAttributes(type, type.GetCustomAttributes(inherit: false), type.Name);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                AssertNoFormatAttributes(type, property.GetCustomAttributes(inherit: false), $"{type.Name}.{property.Name}");
            }
        }

        static void AssertNoFormatAttributes(Type owner, object[] attributes, string where)
        {
            foreach (var attribute in attributes)
            {
                var attributeType = attribute.GetType();
                var isJsonAttribute = attributeType.Namespace == "System.Text.Json.Serialization";
                Assert.False(
                    isJsonAttribute && attributeType != typeof(JsonIgnoreAttribute),
                    $"{where} usa {attributeType.Name}; el formato del cable no se personaliza ({owner.Name}).");
            }
        }
    }

    [Fact]
    public void Every_exported_type_is_an_enum_a_sealed_record_the_interface_or_a_known_static_helper()
    {
        // Un DTO escrito como clase normal (no record) no lo descubriría la reflexión de las pruebas de ida y vuelta.
        var allowedStatics = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(JobDefaults), nameof(ServiceSettingsDefaults), nameof(BackboneErrors), nameof(BackboneIpc), nameof(ContractVersion),
            nameof(ContractLimits), nameof(SnapshotPaths), nameof(UncPaths),
        };

        foreach (var type in ContractTypes.Assembly.GetExportedTypes())
        {
            var allowed = type.IsEnum
                || type == typeof(IBackboneService)
                || (ContractTypes.IsRecord(type) && type.IsSealed)
                || (type.IsAbstract && type.IsSealed && allowedStatics.Contains(type.Name));
            Assert.True(allowed, $"{type.FullName}: tipo público inesperado en Backbone.Contracts (¿DTO que no es 'sealed record'?).");
        }
    }

    [Fact]
    public void Default_job_round_trips_and_keeps_the_specified_defaults()
    {
        var job = JobDefaults.NewJob();

        var json = RoundTripJson(job, typeof(JobDefinition), out var copy);

        Assert.True(ContractTypes.FindDifference(job, copy, "job") is null, json);
        var parsed = (JobDefinition)copy!;
        Assert.Equal(JobDefaults.Exclusions, parsed.Exclusions);
        Assert.Equal(30, parsed.Conditions.MinBatteryPercent);
    }

    [Fact]
    public void A_field_missing_in_an_older_peer_takes_the_documented_default_not_the_type_default()
    {
        // Compatibilidad hacia atrás: un cliente 1.0 que no conoce un campo nuevo no debe cambiar su semántica.
        var job = JsonSerializer.Deserialize<JobDefinition>("""{ "Name": "x" }""", ContractTypes.FormatterOptions);

        Assert.True(job?.Enabled);
        Assert.Equal(DaysOfWeek.All, job?.Schedule.Days);
        Assert.True(job?.Schedule.CatchUpMissedRuns);
        Assert.Equal(24, job?.Schedule.IntervalHours);
        Assert.Equal(30, job?.Conditions.MinBatteryPercent);
        Assert.Equal(RestoreConflictPolicy.Ask, JsonSerializer.Deserialize<RestoreRequest>("{}", ContractTypes.FormatterOptions)?.ConflictPolicy);
        Assert.Equal(HistoryQuery.DefaultLimit, JsonSerializer.Deserialize<HistoryQuery>("{}", ContractTypes.FormatterOptions)?.Limit);
    }

    [Fact]
    public void A_password_never_appears_in_the_serialized_form_of_any_output_DTO()
    {
        // RNF-06 / §7.5. Solo CredentialInput (entrada) puede llevar contraseña; ningún DTO de salida tiene una propiedad
        // con ese nombre, ni siquiera anidada. Se usa una contraseña centinela en todos los textos de los ejemplos.
        const string sentinel = "CENTINELA-contraseña-9f3a";
        var outputTypes = ContractTypes.Records.Where(t => t != typeof(CredentialInput)).ToList();

        foreach (var type in outputTypes)
        {
            var json = JsonSerializer.Serialize(ContractTypes.CreateSample(type), type, ContractTypes.FormatterOptions);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        }

        var input = new CredentialInput { UserName = "backup", Password = sentinel };
        Assert.DoesNotContain(sentinel, input.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, $"{input}", StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, $"{new CredentialHolder(input)}", StringComparison.Ordinal);
    }

    // Un registro que contiene una credencial: su ToString (generado) debe usar el de CredentialInput, que la oculta.
    private sealed record CredentialHolder(CredentialInput Credential);
}
