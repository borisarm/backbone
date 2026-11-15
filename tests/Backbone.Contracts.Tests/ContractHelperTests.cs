using System.Net;
using System.Reflection;
using System.Text.Json;

namespace Backbone.Contracts.Tests;

/// <summary>
/// Utilidades puras del contrato (§7.5): <see cref="SnapshotPaths"/> (M-3), <see cref="UncPaths"/> (A-3 y raíz del recurso)
/// y normalización de <c>null</c> en los DTO (F-1).
/// </summary>
[Trait("Story", "HT-02")]
public sealed class ContractHelperTests
{
    // ---- Identificador de snapshot (§6.3) ----

    [Theory]
    [InlineData("20261115T020000Z-a1b2", true)]
    [InlineData("20240229T235959Z-0000", true)]
    [InlineData("20230229T000000Z-a1b2", false)] // 29 de febrero en año no bisiesto
    [InlineData("20261115T020000Z-A1B2", false)] // hexadecimal en mayúscula
    [InlineData("20261115T020000Z-a1b", false)]
    [InlineData("20261115T020000Z-a1b2c", false)]
    [InlineData("20261115T020000-a1b2", false)]
    [InlineData("20261115T250000Z-a1b2", false)]
    [InlineData("../20261115T020000Z", false)]
    [InlineData("20261115T020000Z_a1b2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Snapshot_ids_follow_the_format_of_section_6_3(string? id, bool valid)
    {
        Assert.Equal(valid, SnapshotPaths.IsValidSnapshotId(id));
    }

    // ---- Segmentos y rutas de snapshot ----

    [Theory]
    [InlineData("enero.pdf")]
    [InlineData("Documentos")]
    [InlineData("archivo con espacios.txt")]
    [InlineData("ñandú-日本語-\U0001F4C1")]
    [InlineData(".gitignore")]
    [InlineData("CONSOLA.txt")]
    [InlineData("COM10")]
    [InlineData("Backups$")]
    public void Valid_segments_are_accepted(string segment)
    {
        Assert.True(SnapshotPaths.IsValidSegment(segment));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("archivo.txt:flujo")] // flujo alternativo (ADS)
    [InlineData("C:")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\u0000b")]
    [InlineData("a\u001Fb")]
    [InlineData("acaba en punto.")]
    [InlineData("acaba en espacio ")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("aux.tar.gz")]
    [InlineData("COM1")]
    [InlineData("LPT9.log")]
    [InlineData("COM\u00B9")]
    [InlineData("CON .txt")]
    [InlineData("CONIN$")]
    [InlineData("conout$")]
    [InlineData("CONOUT$.txt")]
    public void Invalid_or_dangerous_segments_are_rejected(string segment)
    {
        Assert.False(SnapshotPaths.IsValidSegment(segment));
    }

    [Fact]
    public void A_segment_longer_than_the_limit_is_rejected()
    {
        Assert.True(SnapshotPaths.IsValidSegment(new string('a', ContractLimits.MaxSegmentLength)));
        Assert.False(SnapshotPaths.IsValidSegment(new string('a', ContractLimits.MaxSegmentLength + 1)));
    }

    [Theory]
    [InlineData("Documentos", true)]
    [InlineData("Documentos/Facturas/2026/enero.pdf", true)]
    [InlineData("/Documentos", false)]
    [InlineData("Documentos/", false)]
    [InlineData("Documentos//enero.pdf", false)]
    [InlineData("Documentos/../Windows", false)]
    [InlineData("Documentos/./enero.pdf", false)]
    [InlineData("Documentos\\enero.pdf", false)]
    [InlineData("C:/Windows", false)]
    [InlineData("Documentos/CON", false)]
    public void Snapshot_paths_are_validated_segment_by_segment(string path, bool valid)
    {
        Assert.Equal(valid, SnapshotPaths.IsValidPath(path));
    }

    [Fact]
    public void The_root_is_only_valid_when_allowed()
    {
        Assert.False(SnapshotPaths.IsValidPath(string.Empty));
        Assert.True(SnapshotPaths.IsValidPath(string.Empty, allowRoot: true));
        Assert.False(SnapshotPaths.IsValidPath(null, allowRoot: true));
    }

    [Fact]
    public void A_path_longer_than_the_limit_is_rejected()
    {
        var segment = new string('a', 200);
        var count = (ContractLimits.MaxSnapshotPathLength / (segment.Length + 1)) + 1;
        var path = string.Join('/', Enumerable.Repeat(segment, count));

        Assert.True(path.Length > ContractLimits.MaxSnapshotPathLength);
        Assert.False(SnapshotPaths.IsValidPath(path));
    }

    // ---- Destino UNC y raíz del recurso ----

    [Theory]
    [InlineData(@"\\192.168.1.50\Backups\PC-Salon", @"\\192.168.1.50\backups", @"\\192.168.1.50\backups\PC-Salon")]
    [InlineData(@"\\NAS\Backups", @"\\nas\backups", @"\\nas\backups")]
    [InlineData(@"\\nas\Backups\", @"\\nas\backups", @"\\nas\backups")]
    [InlineData(@"\\nas.casa.lan\Copias$\Ana\Portátil", @"\\nas.casa.lan\copias$", @"\\nas.casa.lan\copias$\Ana\Portátil")]
    public void A_valid_destination_yields_its_normalized_share_root(string destination, string root, string normalized)
    {
        Assert.True(UncPaths.TryParse(destination, out var shareRoot, out var normalizedDestination, out _));
        Assert.Equal(root, shareRoot);
        Assert.Equal(normalized, normalizedDestination);
        Assert.True(UncPaths.TryGetShareRoot(destination, out var again));
        Assert.Equal(root, again);
    }

    [Theory]
    [InlineData(null, ValidationIssueCode.DestinationRequired)]
    [InlineData("", ValidationIssueCode.DestinationRequired)]
    [InlineData("   ", ValidationIssueCode.DestinationRequired)]
    [InlineData(@"C:\Copias", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas\", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\\nas\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas\share/otra", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\na s\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\[::1]\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\?\UNC\nas\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\?\C:\Windows", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\.\pipe\Backbone.Service.v1", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\??\UNC\nas\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData("//nas/share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\localhost\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\LOCALHOST\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\LocalHost\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\equipo.localhost\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\127.0.0.1\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\127.1.2.3\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\0.0.0.0\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\C$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\d$\copias", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\ADMIN$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\ipc$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\..\otra", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\.\x", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\\x", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\a:b", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\CON", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\..", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\c$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\Admin$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\IPC$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\share\CONIN$", ValidationIssueCode.DestinationNotAllowed)]
    // Formas alternativas de loopback (IPAddress.TryParse acepta notaciones IPv4 heredadas).
    [InlineData(@"\\2130706433\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\0x7f.1\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\0177.1\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\127.1\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\0\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\0--1.ipv6-literal.net\share", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\FE80--1.IPV6-LITERAL.NET\share", ValidationIssueCode.DestinationNotAllowed)]
    // IPv6 entre corchetes, no ASCII y punto final: no son nombres de servidor admitidos.
    [InlineData(@"\\[0:0:0:0:0:0:0:1]\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\[::ffff:127.0.0.1]\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData("\\\\\uFF4C\uFF4F\uFF43\uFF41\uFF4C\uFF48\uFF4F\uFF53\uFF54\\share", ValidationIssueCode.DestinationNotUnc)] // ｌｏｃａｌｈｏｓｔ
    [InlineData("\\\\\uFF11\uFF12\uFF17.\uFF10.\uFF10.\uFF11\\share", ValidationIssueCode.DestinationNotUnc)] // １２７.０.０.１
    [InlineData(@"\\nás\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\localhost.\share", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas.\share", ValidationIssueCode.DestinationNotUnc)]
    public void Invalid_or_dangerous_destinations_are_rejected_with_a_specific_code(string? destination, ValidationIssueCode expected)
    {
        Assert.False(UncPaths.TryParse(destination, out var shareRoot, out var normalized, out var issue));
        Assert.Equal(expected, issue);
        Assert.Equal(string.Empty, shareRoot);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void A_destination_longer_than_the_limit_is_rejected()
    {
        var destination = @"\\nas\share\" + new string('a', ContractLimits.MaxDestinationLength);

        Assert.False(UncPaths.TryParse(destination, out _, out _, out var issue));
        Assert.Equal(ValidationIssueCode.LimitExceeded, issue);
    }

    [Theory]
    [InlineData(@"\\PC-SALON\share")]
    [InlineData(@"\\pc-salon\share")]
    [InlineData(@"\\pc-salon.casa.lan\share\x")]
    public void The_own_machine_names_given_by_the_service_are_rejected(string destination)
    {
        Assert.True(UncPaths.TryParse(destination, out _, out _, out _));
        Assert.False(UncPaths.TryParse(destination, out _, out _, out var issue, ["PC-SALON", "pc-salon.casa.lan"]));
        Assert.Equal(ValidationIssueCode.DestinationNotAllowed, issue);
    }

    [Fact]
    public void A_punycode_server_name_is_accepted()
    {
        Assert.True(UncPaths.TryParse(@"\\xn--ns-yia.casa.lan\copias", out var root, out _, out _));
        Assert.Equal(@"\\xn--ns-yia.casa.lan\copias", root);
    }

    [Theory]
    [InlineData(@"\\192.168.1.10\share")]
    [InlineData(@"\\169.254.10.20\share")]
    [InlineData(@"\\3232235786\share")] // 192.168.1.10 en decimal
    public void Literal_addresses_of_the_local_interfaces_given_by_the_service_are_rejected(string destination)
    {
        IPAddress[] local = [IPAddress.Parse("192.168.1.10"), IPAddress.Parse("169.254.10.20"), IPAddress.Parse("::ffff:10.0.0.5")];

        Assert.True(UncPaths.TryParse(destination, out _, out _, out _));
        Assert.False(UncPaths.TryParse(destination, out _, out _, out var issue, localAddresses: local));
        Assert.Equal(ValidationIssueCode.DestinationNotAllowed, issue);
        Assert.False(UncPaths.TryParse(@"\\10.0.0.5\share", out _, out _, out _, localAddresses: local));
        Assert.True(UncPaths.TryParse(@"\\192.168.1.11\share", out _, out _, out _, localAddresses: local));
    }

    // ---- F-1: null explícito en colecciones, textos y registros anidados ----

    public static TheoryData<Type> AllRecords() => new(ContractTypes.Records);

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void Explicit_nulls_in_non_nullable_properties_become_empty_values(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var nonNullable = ContractTypes.DataProperties(type)
            .Where(p => !p.PropertyType.IsValueType && nullability.Create(p).WriteState == NullabilityState.NotNull)
            .ToList();
        if (nonNullable.Count == 0)
        {
            return;
        }

        var json = "{" + string.Join(',', nonNullable.Select(p => $"\"{p.Name}\":null")) + "}";
        var value = JsonSerializer.Deserialize(json, type, ContractTypes.FormatterOptions)!;

        foreach (var property in nonNullable)
        {
            Assert.True(property.GetValue(value) is not null, $"{type.Name}.{property.Name} queda null con un null explícito.");
        }
    }
}
