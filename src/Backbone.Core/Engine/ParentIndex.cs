using System.Runtime.CompilerServices;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Snapshots;

namespace Backbone.Core.Engine;

/// <summary>
/// Índice compacto de los archivos del snapshot padre para la detección incremental (RF-23): por cada ruta guarda su
/// resumen de 128 bits (<see cref="EntryPathSet.Digest"/>), no la cadena, junto con el hash en binario, el tamaño, la
/// fecha y los atributos.
/// </summary>
/// <remarks>
/// Unos 100 bytes por archivo con el margen de crecimiento del diccionario: con 500 000 archivos (RNF-05), unos 50 MB.
/// Las carpetas vacías no se indexan (no hacen falta para comparar). La probabilidad de que dos rutas compartan resumen
/// es despreciable (≈ n²/2¹²⁹), y además tendrían que coincidir tamaño, fecha y atributos para reutilizar un hash ajeno.
/// </remarks>
internal sealed class ParentIndex
{
    /// <summary>Capacidad inicial máxima: la cabecera del padre no es confiable y no debe decidir cuánta memoria se reserva.</summary>
    private const int MaxInitialCapacity = 1 << 20;

    private readonly Dictionary<UInt128, ParentFile> files;

    private ParentIndex(int capacity) => files = new Dictionary<UInt128, ParentFile>(capacity);

    /// <summary>Índice vacío (primera copia).</summary>
    public static ParentIndex Empty { get; } = new(0);

    /// <summary>Número de archivos indexados.</summary>
    public int Count => files.Count;

    /// <summary>Lee el snapshot <paramref name="snapshotId"/> en streaming y construye el índice.</summary>
    /// <exception cref="RepositoryException">El snapshot no existe, es incompatible o es corrupto.</exception>
    public static async Task<ParentIndex> LoadAsync(BackupRepository repository, string snapshotId, CancellationToken cancellationToken)
    {
        var reader = await repository.OpenSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var index = new ParentIndex((int)Math.Clamp(reader.Header.Stats.Files, 0, MaxInitialCapacity));
            await foreach (var entry in reader.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                {
                    index.files[EntryPathSet.Digest(entry.Path)] = ParentFile.From(entry);
                }
            }

            return index;
        }
    }

    /// <summary>Busca un archivo del padre por su ruta en el snapshot.</summary>
    public bool TryGet(UInt128 pathDigest, out ParentFile file) => files.TryGetValue(pathDigest, out file);
}

/// <summary>Datos de un archivo del snapshot padre.</summary>
internal readonly struct ParentFile
{
    private readonly HashBytes hash;

    private ParentFile(HashBytes hash, long size, long modifiedTicks, FileAttributes attributes)
    {
        this.hash = hash;
        Size = size;
        ModifiedTicks = modifiedTicks;
        Attributes = attributes;
    }

    /// <summary>Tamaño en bytes.</summary>
    public long Size { get; }

    /// <summary>Fecha de modificación UTC en ticks.</summary>
    public long ModifiedTicks { get; }

    /// <summary>Atributos guardados.</summary>
    public FileAttributes Attributes { get; }

    /// <summary>Hash del contenido.</summary>
    public ContentHash Hash => ContentHash.FromBytes(hash);

    /// <summary>Fecha de modificación UTC.</summary>
    public DateTime ModifiedUtc => new(ModifiedTicks, DateTimeKind.Utc);

    /// <summary>Si los metadatos que compara RF-23 coinciden.</summary>
    public bool Matches(long size, DateTime modifiedUtc, FileAttributes attributes) =>
        Size == size && ModifiedTicks == modifiedUtc.Ticks && Attributes == attributes;

    public static ParentFile From(SnapshotEntry entry)
    {
        var bytes = default(HashBytes);
        if (Convert.FromHexString(entry.Hash.GetValueOrDefault().ToString(), bytes, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != ContentHash.ByteLength)
        {
            // No puede pasar: el lector solo devuelve hashes de 64 hexadecimales.
            throw new RepositoryException(RepositoryErrorKind.Corrupt, "Hash no válido en el snapshot padre.");
        }

        return new ParentFile(bytes, entry.Size, entry.ModifiedUtc.Ticks, entry.Attributes);
    }

    /// <summary>Los 32 bytes del SHA-256, en línea (sin la cadena hexadecimal de 64 caracteres).</summary>
    [InlineArray(ContentHash.ByteLength)]
    private struct HashBytes
    {
        private byte element;
    }
}
