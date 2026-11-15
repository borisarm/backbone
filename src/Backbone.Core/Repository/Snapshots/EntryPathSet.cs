using System.Security.Cryptography;
using System.Text;

namespace Backbone.Core.Repository.Snapshots;

/// <summary>
/// Conjunto de rutas para comprobar que cada <c>p</c> es única en un snapshot (§6.3) sin guardar las
/// cadenas: se guarda un resumen de 128 bits (SHA-256 truncado) de cada ruta en UTF-8.
/// Con 500 000 rutas ocupa unos 20 MB y la probabilidad de colisión es despreciable (≈ 2⁻⁹⁰).
/// La comparación es ordinal (distingue mayúsculas), como la unicidad que pide el formato.
/// </summary>
internal sealed class EntryPathSet
{
    private readonly HashSet<UInt128> digests = [];

    public int Count => digests.Count;

    public bool Add(string path)
    {
        var byteCount = Encoding.UTF8.GetByteCount(path);
        var rented = byteCount <= 1024 ? null : new byte[byteCount];
        Span<byte> utf8 = rented ?? stackalloc byte[1024];
        utf8 = utf8[..Encoding.UTF8.GetBytes(path, utf8)];

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8, hash);
        return digests.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(hash));
    }
}
