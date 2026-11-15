using System.Text.Json;

namespace Backbone.Core.Repository;

/// <summary>Contenido de <c>repo.json</c> (§6.2).</summary>
/// <param name="FormatVersion">Versión de formato.</param>
/// <param name="RepositoryId">Identificador único del repositorio.</param>
/// <param name="CreatedUtc">Fecha de creación (UTC).</param>
/// <param name="CreatedBy">
/// Texto libre con la versión de Backbone y el equipo que lo creó. Al leerlo es <b>no confiable</b> (§6.1): se sanea antes
/// de registrarlo o mostrarlo.
/// </param>
/// <param name="Hash">Algoritmo de hash de los objetos (<c>sha256</c> en v1).</param>
/// <param name="Compression">Compresión de los objetos (<c>none</c> en v1).</param>
/// <param name="Encryption">Cifrado de los objetos (<c>none</c> en v1).</param>
public sealed record RepositoryConfiguration(
    int FormatVersion,
    Guid RepositoryId,
    DateTime CreatedUtc,
    string CreatedBy,
    string Hash,
    string Compression,
    string Encryption)
{
    /// <summary>Algoritmo de hash de v1.</summary>
    public const string Sha256 = "sha256";

    /// <summary>Valor "sin compresión" / "sin cifrado".</summary>
    public const string None = "none";

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("repositoryId", RepositoryId.ToString("D"));
        JsonFormat.WriteUtc(writer, "createdUtc", CreatedUtc);
        writer.WriteString("createdBy", JsonFormat.SanitizeText(CreatedBy));
        writer.WriteString("hash", Hash);
        writer.WriteString("compression", Compression);
        writer.WriteString("encryption", Encryption);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Interpreta <c>repo.json</c>. La versión se comprueba antes que ningún otro campo, para que un
    /// formato superior se reconozca como tal aunque el resto de su estructura haya cambiado (RNF-14).
    /// </summary>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.Incompatible"/> si la versión es superior o usa características
    /// desconocidas; <see cref="RepositoryErrorKind.Corrupt"/> si no es válido.
    /// </exception>
    internal static RepositoryConfiguration Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (InvalidOperationException ex)
        {
            // GetString de un campo con una suplente suelta escapada: UTF-16 no válido.
            throw Corrupt("repo.json tiene un texto que no es UTF-16 válido.", ex);
        }
    }

    private static RepositoryConfiguration ParseCore(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw Corrupt("repo.json no es un JSON válido.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt("repo.json no es un objeto JSON.");
            }

            if (!root.TryGetProperty("formatVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number)
            {
                throw Corrupt("repo.json no tiene una versión de formato válida.");
            }

            if (!versionElement.TryGetInt32(out var version))
            {
                // Un entero positivo que no cabe en Int32 es una versión mayor que cualquiera conocida (§6.2, RNF-14), no
                // una corrupción. Los no enteros (1.5, 1e3) y los negativos siguen siendo corrupción.
                var raw = versionElement.GetRawText();
                if (IsPositiveIntegerLiteral(raw))
                {
                    throw new RepositoryException(
                        RepositoryErrorKind.Incompatible,
                        $"El repositorio usa la versión de formato {Untrusted.Describe(raw)}; esta versión de Backbone solo entiende hasta la {RepositoryLayout.FormatVersion}.");
                }

                throw Corrupt("repo.json no tiene una versión de formato válida.");
            }

            if (version > RepositoryLayout.FormatVersion)
            {
                throw new RepositoryException(
                    RepositoryErrorKind.Incompatible,
                    $"El repositorio usa la versión de formato {version}; esta versión de Backbone solo entiende hasta la {RepositoryLayout.FormatVersion}.");
            }

            if (version < 1)
            {
                throw Corrupt($"Versión de formato no válida: {version}.");
            }

            var hash = RequiredString(root, "hash");
            var compression = RequiredString(root, "compression");
            var encryption = RequiredString(root, "encryption");
            if (hash != Sha256 || compression != None || encryption != None)
            {
                throw new RepositoryException(
                    RepositoryErrorKind.Incompatible,
                    $"Características no admitidas en el formato {RepositoryLayout.FormatVersion}: hash={Untrusted.Describe(hash)}, compression={Untrusted.Describe(compression)}, encryption={Untrusted.Describe(encryption)}.");
            }

            if (!Guid.TryParseExact(RequiredString(root, "repositoryId"), "D", out var repositoryId))
            {
                throw Corrupt("repo.json tiene un repositoryId no válido.");
            }

            if (!root.TryGetProperty("createdUtc", out var createdElement) || !JsonFormat.TryGetUtc(createdElement, out var createdUtc))
            {
                throw Corrupt("repo.json tiene un createdUtc no válido.");
            }

            // Texto libre (§6.2): el UTF-16 no válido se sustituye por U+FFFD en lugar de rechazar el repositorio.
            var createdBy = root.TryGetProperty("createdBy", out var createdByElement) && createdByElement.ValueKind == JsonValueKind.String
                ? JsonFormat.GetStringReplacingInvalidUtf16(createdByElement)
                : throw Corrupt("repo.json no tiene 'createdBy'.");
            return new RepositoryConfiguration(version, repositoryId, createdUtc, createdBy, hash, compression, encryption);
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? throw Corrupt($"repo.json no tiene '{name}'.")
            : throw Corrupt($"repo.json no tiene '{name}'.");

    private static RepositoryException Corrupt(string message, Exception? inner = null) =>
        new(RepositoryErrorKind.Corrupt, message, inner);

    /// <summary>Si el literal JSON es un entero positivo (solo dígitos, sin signo, fracción ni exponente).</summary>
    private static bool IsPositiveIntegerLiteral(string raw) =>
        raw.Length > 0 && raw[0] is >= '1' and <= '9' && !raw.AsSpan().ContainsAnyExceptInRange('0', '9');
}
