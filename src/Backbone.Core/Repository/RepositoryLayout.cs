using Backbone.Core.Storage;

namespace Backbone.Core.Repository;

/// <summary>Nombres y rutas del formato v1 (§6.1).</summary>
public static class RepositoryLayout
{
    /// <summary>Versión de formato que esta versión de Backbone lee y escribe (RNF-14).</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Máximo de entradas (archivos y carpetas vacías) de un snapshot (§6.3, 10 × RNF-05). Al leer, más se considera
    /// corrupción (protege al lector de manifiestos hostiles, M-2); al escribir, la copia falla sin publicar.
    /// </summary>
    public const long MaxSnapshotEntries = 5_000_000;

    /// <summary>Tamaño máximo de un manifiesto completo (16 GiB, §6.3).</summary>
    public const long MaxSnapshotManifestBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>Archivo de configuración.</summary>
    public const string ConfigFile = "repo.json";

    /// <summary>Carpeta de objetos.</summary>
    public const string ObjectsDirectory = "objects";

    /// <summary>Carpeta de manifiestos de snapshots.</summary>
    public const string SnapshotsDirectory = "snapshots";

    /// <summary>Carpeta de bloqueos.</summary>
    public const string LocksDirectory = "locks";

    /// <summary>Carpeta de escritura temporal.</summary>
    public const string TempDirectory = "tmp";

    /// <summary>Extensión de los manifiestos.</summary>
    public const string SnapshotExtension = ".json";

    /// <summary>Extensión de los bloqueos.</summary>
    public const string LockExtension = ".lock";

    /// <summary>Extensión de los temporales.</summary>
    public const string TempExtension = ".tmp";

    /// <summary>Carpetas que crea la inicialización.</summary>
    public static IReadOnlyList<string> Directories { get; } =
        [ObjectsDirectory, SnapshotsDirectory, LocksDirectory, TempDirectory];

    /// <summary>Carpeta de un objeto: <c>objects/ab</c>.</summary>
    public static string ObjectDirectory(ContentHash hash) =>
        RepositoryPath.Combine(ObjectsDirectory, hash.Prefix);

    /// <summary>Ruta de un objeto: <c>objects/ab/ab12…ef</c>.</summary>
    public static string ObjectPath(ContentHash hash) =>
        RepositoryPath.Combine(ObjectsDirectory, hash.Prefix, hash.ToString());

    /// <summary>Ruta de un manifiesto: <c>snapshots/&lt;id&gt;.json</c>.</summary>
    public static string SnapshotPath(string snapshotId) =>
        RepositoryPath.Combine(SnapshotsDirectory, snapshotId + SnapshotExtension);

    /// <summary>Ruta de un temporal nuevo: <c>tmp/&lt;guid&gt;.tmp</c>.</summary>
    public static string NewTempPath() =>
        RepositoryPath.Combine(TempDirectory, Guid.NewGuid().ToString("N") + TempExtension);
}
