using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using StreamJsonRpc;

namespace Backbone.Contracts.Tests;

/// <summary>
/// Descubre por reflexión los DTO del contrato y genera instancias de ejemplo en las que <b>todas</b> las propiedades
/// tienen un valor distinto del predeterminado, para que una propiedad que no viaje se detecte al comparar.
/// </summary>
internal static class ContractTypes
{
    public static readonly Assembly Assembly = typeof(IBackboneService).Assembly;

    /// <summary>Opciones de System.Text.Json que usa el <see cref="SystemTextJsonFormatter"/> de StreamJsonRpc (ADR-03).</summary>
    public static JsonSerializerOptions FormatterOptions { get; } = new SystemTextJsonFormatter().JsonSerializerOptions;

    /// <summary>Todos los <c>record</c> públicos del ensamblado de contratos.</summary>
    public static IReadOnlyList<Type> Records { get; } =
        Assembly.GetExportedTypes()
            .Where(IsRecord)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Todos los enumerados públicos del ensamblado de contratos.</summary>
    public static IReadOnlyList<Type> Enums { get; } =
        Assembly.GetExportedTypes()
            .Where(t => t.IsEnum)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    public static bool IsRecord(Type type) =>
        type.IsClass && type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

    public static bool IsContractType(Type type) => type.Assembly == Assembly;

    /// <summary>Propiedades de datos de un DTO (públicas, de instancia y con <c>init</c>); excluye las calculadas.</summary>
    public static IEnumerable<PropertyInfo> DataProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null && p.Name != "EqualityContract");

    /// <summary>Indica si el descriptor de acceso <c>set</c> es <c>init</c>.</summary>
    public static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit") == true;

    public static Type? ReadOnlyListElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? type.GetGenericArguments()[0]
            : null;

    // ---- Generación de ejemplos ----

    public static object CreateSample(Type type) => CreateSample(type, seed: 1, depth: 0);

    private static object CreateSample(Type type, int seed, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException($"Anidamiento excesivo al generar {type}; ¿hay un ciclo en los DTO?");
        }

        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{type} necesita un constructor sin parámetros.");
        var i = 0;
        foreach (var property in DataProperties(type))
        {
            var current = property.GetValue(instance);
            var value = CreateValue(property.PropertyType, current, seed * 31 + ++i, depth + 1, $"{type.Name}.{property.Name}");
            property.SetValue(instance, value);
        }

        return instance;
    }

    /// <summary>Crea un valor de <paramref name="type"/> distinto de <paramref name="current"/> (el valor por defecto de la propiedad).</summary>
    private static object CreateValue(Type type, object? current, int seed, int depth, string where)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return CreateValue(underlying, null, seed, depth, where);
        }

        if (type == typeof(string))
        {
            // Incluye caracteres que exigen escape y no ASCII.
            return string.Create(CultureInfo.InvariantCulture, $"{where} \"ñ\\ü\" {seed}");
        }

        if (type == typeof(bool))
        {
            return current is bool b ? !b : true;
        }

        if (type == typeof(int))
        {
            return (current as int? ?? 0) + 100 + seed;
        }

        if (type == typeof(long))
        {
            // Fuera del intervalo de int y de la precisión exacta de double.
            return (current as long? ?? 0) + 9_007_199_254_740_993L + seed;
        }

        if (type == typeof(double))
        {
            return 1234.5678 + seed;
        }

        if (type == typeof(Guid))
        {
            return new Guid(seed, 0x1234, 0x5678, [1, 2, 3, 4, 5, 6, 7, 8]);
        }

        if (type == typeof(DateTimeOffset))
        {
            // Precisión de 100 ns (§6.3).
            return new DateTimeOffset(2026, 11, 15, 2, 3, 4, TimeSpan.Zero).AddTicks(1_234_567 + seed);
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromTicks(37_251_234_567 + seed);
        }

        if (type == typeof(TimeOnly))
        {
            return new TimeOnly(13, 30, 15).Add(TimeSpan.FromTicks(1_234_567 + seed));
        }

        if (type.IsEnum)
        {
            return CreateEnumValue(type, current, where);
        }

        if (ReadOnlyListElementType(type) is { } element)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            list.Add(CreateValue(element, null, seed + 1, depth, where + "[0]"));
            list.Add(CreateValue(element, null, seed + 2, depth, where + "[1]"));
            return list;
        }

        if (IsContractType(type) && IsRecord(type))
        {
            return CreateSample(type, seed, depth);
        }

        throw new NotSupportedException(
            $"{where}: tipo {type} no admitido en los DTO del contrato (o falta en el generador de pruebas).");
    }

    private static object CreateEnumValue(Type type, object? current, string where)
    {
        var values = Enum.GetValues(type).Cast<object>().ToList();
        var defaultValue = current ?? Activator.CreateInstance(type)!;
        if (type.IsDefined(typeof(FlagsAttribute)))
        {
            // Combinación de dos indicadores distintos del valor por defecto.
            var flags = values.Select(Convert.ToInt64).Where(v => v != 0 && (v & (v - 1)) == 0).Take(2).Sum();
            var combined = Enum.ToObject(type, flags);
            if (!combined.Equals(defaultValue))
            {
                return combined;
            }
        }

        return values.LastOrDefault(v => !v.Equals(defaultValue))
            ?? throw new InvalidOperationException($"{where}: el enumerado {type} no tiene un valor distinto del predeterminado.");
    }

    // ---- Comparación estructural ----

    /// <summary>Compara dos valores recorriendo propiedades de los DTO y elementos de las listas.</summary>
    /// <returns>Descripción de la primera diferencia, o <see langword="null"/> si son iguales.</returns>
    public static string? FindDifference(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: esperado {expected ?? "null"}, obtenido {actual ?? "null"}";
        }

        var type = expected.GetType();
        if (type != actual.GetType() && !(expected is IEnumerable && actual is IEnumerable))
        {
            return $"{path}: tipo esperado {type}, obtenido {actual.GetType()}";
        }

        if (IsContractType(type) && IsRecord(type))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.Name != "EqualityContract"))
            {
                var difference = FindDifference(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}");
                if (difference is not null)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected is IEnumerable expectedItems and not string && actual is IEnumerable actualItems)
        {
            var e = expectedItems.Cast<object?>().ToList();
            var a = actualItems.Cast<object?>().ToList();
            if (e.Count != a.Count)
            {
                return $"{path}: {e.Count} elementos esperados, {a.Count} obtenidos";
            }

            for (var i = 0; i < e.Count; i++)
            {
                var difference = FindDifference(e[i], a[i], $"{path}[{i}]");
                if (difference is not null)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected is DateTimeOffset expectedDate && actual is DateTimeOffset actualDate)
        {
            // Mismo instante, mismos ticks y mismo desplazamiento (UTC).
            return expectedDate.UtcTicks == actualDate.UtcTicks && expectedDate.Offset == actualDate.Offset
                ? null
                : $"{path}: esperado {expectedDate:O}, obtenido {actualDate:O}";
        }

        return Equals(expected, actual) ? null : $"{path}: esperado {expected}, obtenido {actual}";
    }
}
