using System.Security.Cryptography;
using System.Text;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Conjunto de rutas para comprobar que cada <c>p</c> es única en un snapshot (§6.3) sin guardar las
/// cadenas: se guarda un resumen de 128 bits (SHA-256 truncado) de cada ruta en UTF-8.
/// Cada ruta ocupa unos 28 bytes en el <see cref="HashSet{T}"/> (más el margen de crecimiento): con 500 000 rutas
/// (RNF-05) son unos 20 MB y con el máximo de 5 000 000 de entradas (§6.3) unos 200 MB. La probabilidad de colisión es
/// despreciable incluso en el máximo (≈ n²/2¹²⁹ ≈ 2⁻⁸⁴).
/// La comparación es ordinal (distingue mayúsculas), como la unicidad que pide el formato.
/// </summary>
internal sealed class EntryPathSet
{
    private readonly HashSet<UInt128> digests = [];

    public int Count => digests.Count;

    public bool Add(string path) => digests.Add(Digest(path));

    /// <summary>Resumen de 128 bits de una ruta (SHA-256 truncado de su UTF-8); también lo usa el índice del snapshot padre.</summary>
    public static UInt128 Digest(string path)
    {
        var byteCount = Encoding.UTF8.GetByteCount(path);
        var rented = byteCount <= 1024 ? null : new byte[byteCount];
        Span<byte> utf8 = rented ?? stackalloc byte[1024];
        utf8 = utf8[..Encoding.UTF8.GetBytes(path, utf8)];

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8, hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }
}
