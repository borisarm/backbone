using System.Reflection;
using System.Text.RegularExpressions;

namespace Backbone.Contracts.Tests;

/// <summary>
/// HT-02 criterio 1: la interfaz y los códigos de error coinciden con lo que dice <c>docs/ESPECIFICACION.md</c>
/// (§7.2 y §7.4), leyendo el documento y no una copia escrita a mano en la prueba. Si el código y la especificación
/// discrepan, esta prueba falla: hay que parar y preguntar (CLAUDE.md).
/// </summary>
[Trait("Story", "HT-02")]
public sealed partial class SpecConsistencyTests
{
    private static readonly string Spec = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "ESPECIFICACION.md"));

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encuentra la raíz del repositorio (Backbone.slnx).");
    }

    private static string Section(string startHeading, string endHeading)
    {
        var start = Spec.IndexOf(startHeading, StringComparison.Ordinal);
        var end = Spec.IndexOf(endHeading, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"No se encuentra la sección {startHeading} en la especificación.");
        return Spec[start..end];
    }

    [GeneratedRegex(@"^\s*(?<ret>Task(?:<[^()]+>)?)\s+(?<name>\w+)\((?<params>[^)]*)\)\s*;", RegexOptions.Multiline)]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"^\s*event\s+EventHandler<(?<arg>\w+)>\s+(?<name>\w+)\s*;", RegexOptions.Multiline)]
    private static partial Regex EventRegex();

    private static string Normalize(string type) => type.Replace("?", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string FormatType(Type type)
    {
        if (type == typeof(string)) { return "string"; }
        if (type == typeof(bool)) { return "bool"; }
        if (type == typeof(int)) { return "int"; }
        if (type == typeof(long)) { return "long"; }
        if (type == typeof(void)) { return "void"; }
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return name + "<" + string.Join(',', type.GetGenericArguments().Select(FormatType)) + ">";
        }

        return type.Name;
    }

    private static string SpecSignature(Match method)
    {
        var parameters = method.Groups["params"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=')[0].Trim())
            .Select(p =>
            {
                var cut = p.LastIndexOf(' ');
                return Normalize(p[..cut]) + " " + p[(cut + 1)..];
            });
        return Normalize(method.Groups["ret"].Value) + " " + method.Groups["name"].Value + "(" + string.Join(", ", parameters) + ")";
    }

    private static string CodeSignature(MethodInfo method)
    {
        var parameters = method.GetParameters().Select(p => FormatType(p.ParameterType) + " " + p.Name);
        return FormatType(method.ReturnType) + " " + method.Name + "(" + string.Join(", ", parameters) + ")";
    }

    private static string Section72() => Section("### 7.2 ", "### 7.3 ");

    [Fact]
    public void The_interface_signatures_match_section_7_2_exactly()
    {
        var spec = MethodRegex().Matches(Section72()).Select(SpecSignature).Order(StringComparer.Ordinal).ToList();
        var code = typeof(IBackboneService).GetMethods().Where(m => !m.IsSpecialName).Select(CodeSignature).Order(StringComparer.Ordinal).ToList();

        Assert.True(spec.Count >= 30, $"El análisis de §7.2 solo encontró {spec.Count} métodos.");
        Assert.Equal(spec, code);
    }

    [Fact]
    public void The_interface_events_match_section_7_2_exactly()
    {
        var spec = EventRegex().Matches(Section72())
            .Select(m => $"{m.Groups["name"].Value}:{m.Groups["arg"].Value}")
            .Order(StringComparer.Ordinal).ToList();
        var code = typeof(IBackboneService).GetEvents()
            .Select(e => $"{e.Name}:{e.EventHandlerType!.GetGenericArguments()[0].Name}")
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(5, spec.Count);
        Assert.Equal(spec, code);
    }

    [Fact]
    public void Error_codes_match_section_7_4_in_name_and_order_with_values_from_1000()
    {
        var line = Section("### 7.4 ", "### 7.5 ").Split('\n').First(l => l.StartsWith("`ValidationFailed`", StringComparison.Ordinal));
        var names = Regex.Matches(line, "`(?<n>[A-Za-z]+)`").Select(m => m.Groups["n"].Value).ToList();

        Assert.Equal(names, Enum.GetNames<BackboneErrorCode>());
        Assert.Equal(Enumerable.Range(1000, names.Count), Enum.GetValues<BackboneErrorCode>().Select(v => (int)v));
    }

    [Fact]
    public void Every_error_code_of_the_specification_has_a_translation_hook_in_the_contract_only_once()
    {
        // §7.4: "No se reutilizan ni se reordenan". Un valor duplicado o un hueco rompería a un cliente antiguo.
        var values = Enum.GetValues<BackboneErrorCode>().Select(v => (int)v).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(values.Order(), values);
    }

    [Fact]
    public void Snapshot_limits_of_section_6_3_use_the_contract_values()
    {
        // §6.3: el código del aviso resumen es un valor de RunIssueCode y los límites compartidos coinciden con ContractLimits.
        var section = Section("### 6.3 ", "### 6.4 ");

        Assert.Contains($"\"code\": \"{nameof(RunIssueCode.WarningsTruncated)}\"", section, StringComparison.Ordinal);
        Assert.Contains($"| `sources` | {ContractLimits.MaxSources} (`ContractLimits.MaxSources`) |", section, StringComparison.Ordinal);
        Assert.Contains($"| `jobName` | {ContractLimits.MaxJobNameLength} caracteres (`ContractLimits.MaxJobNameLength`) |", section, StringComparison.Ordinal);
        Assert.Contains($"| `host` | segmento válido ({ContractLimits.MaxSegmentLength} caracteres) |", section, StringComparison.Ordinal);
        Assert.Contains($"| `sources[].alias` | {ContractLimits.MaxAliasLength} caracteres y segmento válido |", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipe_constants_match_section_7_1()
    {
        var section = Section("### 7.1 ", "### 7.2 ");

        Assert.Contains($"`{BackboneIpc.PipeName}`", section, StringComparison.Ordinal);
        Assert.Contains($"`{BackboneIpc.PipePath}`", section, StringComparison.Ordinal);
        Assert.Contains($"hasta {BackboneIpc.MaxConnections} conexiones", section, StringComparison.Ordinal);
        Assert.Contains($"`BackboneIpc.MaxRunProgressEventsPerSecond` ({BackboneIpc.MaxRunProgressEventsPerSecond})", section, StringComparison.Ordinal);
        Assert.Contains($"`Value = \"{ContractVersion.Value}\"`", section, StringComparison.Ordinal);
        Assert.Contains($"`BackboneIpc.MaxMessageBytes` ({BackboneIpc.MaxMessageBytes / (1024 * 1024)} MiB)", section, StringComparison.Ordinal);
        Assert.Contains("FirstPipeInstance", section, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Enum_members_declare_explicit_numeric_values_in_the_source()
    {
        // §7.5: "valores numéricos explícitos y estables". Con un valor implícito, insertar un miembro a mitad de la lista
        // renumera los siguientes y rompe el cable sin que ninguna otra prueba lo vea. No es observable por reflexión,
        // así que se analiza el código fuente.
        var sourceDir = Path.Combine(FindRepositoryRoot(), "src", "Backbone.Contracts");
        var enums = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = Regex.Replace(File.ReadAllText(file), @"//[^\n]*", string.Empty);
            foreach (Match match in EnumRegex().Matches(text))
            {
                enums++;
                var members = match.Groups["body"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(m => Regex.Replace(m, @"\[[^\]]*\]", string.Empty).Trim())
                    .Where(m => m.Length > 0);
                foreach (var member in members)
                {
                    Assert.True(member.Contains('=', StringComparison.Ordinal), $"{Path.GetFileName(file)}: {match.Groups["name"].Value}.{member} sin valor explícito.");
                }
            }
        }

        Assert.Equal(ContractTypes.Enums.Count, enums);
    }

    [GeneratedRegex(@"public\s+enum\s+(?<name>\w+)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline)]
    private static partial Regex EnumRegex();
}
