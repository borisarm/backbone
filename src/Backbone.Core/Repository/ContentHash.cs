using System.Diagnostics.CodeAnalysis;

namespace Backbone.Core.Repository;

/// <summary>
/// SHA-256 del contenido de un objeto, en hexadecimal minúsculo de 64 caracteres (§6.1).
/// </summary>
public readonly struct ContentHash : IEquatable<ContentHash>
{
    /// <summary>Longitud en caracteres.</summary>
    public const int HexLength = 64;

    /// <summary>Longitud en bytes.</summary>
    public const int ByteLength = 32;

    private readonly string? hex;

    private ContentHash(string hex) => this.hex = hex;

    /// <summary>Dos primeros caracteres: carpeta dentro de <c>objects/</c>.</summary>
    public string Prefix => Value[..2];

    private string Value => hex ?? throw new InvalidOperationException("ContentHash sin inicializar.");

    /// <summary>Crea el hash a partir de los 32 bytes del SHA-256.</summary>
    public static ContentHash FromBytes(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != ByteLength)
        {
            throw new ArgumentException("Un SHA-256 tiene 32 bytes.", nameof(sha256));
        }

        return new ContentHash(Convert.ToHexStringLower(sha256));
    }

    /// <summary>Interpreta un hash en hexadecimal minúsculo.</summary>
    /// <exception cref="FormatException">No es un hash válido.</exception>
    public static ContentHash Parse(string value) =>
        TryParse(value, out var hash) ? hash : throw new FormatException($"Hash no válido: '{Untrusted.Describe(value)}'.");

    /// <summary>Interpreta un hash en hexadecimal minúsculo; las mayúsculas no se admiten.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out ContentHash hash)
    {
        hash = default;
        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        hash = new ContentHash(value);
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(ContentHash other) => string.Equals(hex, other.hex, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ContentHash other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => hex is null ? 0 : StringComparer.Ordinal.GetHashCode(hex);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Igualdad.</summary>
    public static bool operator ==(ContentHash left, ContentHash right) => left.Equals(right);

    /// <summary>Desigualdad.</summary>
    public static bool operator !=(ContentHash left, ContentHash right) => !left.Equals(right);
}
