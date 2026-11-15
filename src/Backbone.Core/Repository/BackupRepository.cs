using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Repository.Snapshots;
using Backbone.Core.Storage;

namespace Backbone.Core.Repository;

/// <summary>
/// Repositorio en formato v1 (§6): creación, apertura, objetos, snapshots y bloqueos.
/// Todo el acceso al disco pasa por <see cref="IRepositoryStorage"/>.
/// </summary>
/// <remarks>
/// Una instancia por repositorio y proceso: los bloqueos se nombran por equipo y proceso
/// (<c>locks/&lt;host&gt;-&lt;pid&gt;.lock</c>), así que una instancia solo admite un bloqueo a la vez.
/// </remarks>
public sealed class BackupRepository
{
    private const int CopyChunkSize = 1024 * 1024;

    private readonly IRepositoryStorage storage;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private RepositoryLock? heldLock;
    private bool lockBusy;

    private BackupRepository(IRepositoryStorage storage, TimeProvider timeProvider, RepositoryConfiguration configuration)
    {
        this.storage = storage;
        this.timeProvider = timeProvider;
        Configuration = configuration;
    }

    /// <summary>Contenido de <c>repo.json</c>.</summary>
    public RepositoryConfiguration Configuration { get; }

    /// <summary>Tamaño inicial del búfer de lectura de manifiestos (las pruebas lo reducen para forzar cortes).</summary>
    internal int SnapshotReadBufferSize { get; set; } = JsonStreamReader.DefaultBufferSize;

    // ---------------------------------------------------------------- Creación y apertura

    /// <summary>Examina un destino sin escribir nada.</summary>
    public static async Task<RepositoryProbeResult> ProbeAsync(IRepositoryStorage storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!await storage.DirectoryExistsAsync(string.Empty, cancellationToken).ConfigureAwait(false))
        {
            return new RepositoryProbeResult(RepositoryState.Missing);
        }

        byte[]? config;
        try
        {
            config = await TryReadConfigAsync(storage, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.Corrupt)
        {
            return new RepositoryProbeResult(RepositoryState.Corrupt, Detail: ex.Message);
        }

        if (config is null)
        {
            return await IsEmptyOrInterruptedInitializationAsync(storage, cancellationToken).ConfigureAwait(false)
                ? new RepositoryProbeResult(RepositoryState.Empty)
                : new RepositoryProbeResult(RepositoryState.NotARepository, Detail: "Hay archivos y no hay repo.json.");
        }

        var declaredVersion = TryGetDeclaredVersion(config);
        try
        {
            var configuration = RepositoryConfiguration.Parse(config);
            await EnsureDirectoriesAsync(storage, cancellationToken).ConfigureAwait(false);
            return new RepositoryProbeResult(RepositoryState.Compatible, configuration.FormatVersion, configuration);
        }
        catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.Incompatible)
        {
            return new RepositoryProbeResult(RepositoryState.Incompatible, declaredVersion, Detail: ex.Message);
        }
        catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.Corrupt)
        {
            return new RepositoryProbeResult(RepositoryState.Corrupt, declaredVersion, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Crea la estructura de §6.1 y <c>repo.json</c> (§6.2). <c>repo.json</c> se escribe el último: hasta que
    /// existe, el destino no es un repositorio y una inicialización interrumpida se puede repetir.
    /// </summary>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.NotEmpty"/> si el destino contiene otros archivos;
    /// <see cref="RepositoryErrorKind.AlreadyInitialized"/> si ya hay un repositorio.
    /// </exception>
    public static async Task<BackupRepository> InitializeAsync(
        IRepositoryStorage storage,
        RepositoryInitializationOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CreatedBy);

        var probe = await ProbeAsync(storage, cancellationToken).ConfigureAwait(false);
        switch (probe.State)
        {
            case RepositoryState.Missing or RepositoryState.Empty:
                break;
            case RepositoryState.NotARepository:
                throw new RepositoryException(RepositoryErrorKind.NotEmpty, "El destino contiene archivos que no son de un repositorio de Backbone.");
            default:
                throw new RepositoryException(RepositoryErrorKind.AlreadyInitialized, "El destino ya contiene un repositorio.");
        }

        await storage.CreateDirectoryAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var directory in RepositoryLayout.Directories)
        {
            await storage.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        var configuration = new RepositoryConfiguration(
            RepositoryLayout.FormatVersion,
            options.RepositoryId ?? Guid.NewGuid(),
            timeProvider.GetUtcNow().UtcDateTime,
            JsonFormat.SanitizeText(options.CreatedBy),
            RepositoryConfiguration.Sha256,
            RepositoryConfiguration.None,
            RepositoryConfiguration.None);

        var written = await AtomicFile.WriteAsync(
            storage,
            RepositoryLayout.ConfigFile,
            overwrite: false,
            async (stream, ct) =>
            {
                var writer = new Utf8JsonWriter(stream, JsonFormat.WriterOptions(indented: true));
                await using (writer.ConfigureAwait(false))
                {
                    configuration.WriteTo(writer);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (!written)
        {
            throw new RepositoryException(RepositoryErrorKind.AlreadyInitialized, "Otro proceso inicializó el repositorio a la vez.");
        }

        return new BackupRepository(storage, timeProvider, configuration);
    }

    /// <summary>Abre un repositorio existente y valida su formato (RNF-14).</summary>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.NotARepository"/> si no hay <c>repo.json</c>;
    /// <see cref="RepositoryErrorKind.Incompatible"/> si el formato es superior o desconocido;
    /// <see cref="RepositoryErrorKind.Corrupt"/> si no es válido.
    /// </exception>
    public static async Task<BackupRepository> OpenAsync(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var config = await TryReadConfigAsync(storage, cancellationToken).ConfigureAwait(false)
            ?? throw new RepositoryException(RepositoryErrorKind.NotARepository, "El destino no contiene un repositorio de Backbone (falta repo.json).");

        var configuration = RepositoryConfiguration.Parse(config);
        await EnsureDirectoriesAsync(storage, cancellationToken).ConfigureAwait(false);
        return new BackupRepository(storage, timeProvider, configuration);
    }

    // ---------------------------------------------------------------- Objetos

    /// <summary>Indica si el objeto existe.</summary>
    public async Task<bool> ObjectExistsAsync(ContentHash hash, CancellationToken cancellationToken) =>
        await storage.GetFileInfoAsync(RepositoryLayout.ObjectPath(hash), cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Guarda un objeto: copia <paramref name="content"/> a <c>tmp/</c> calculando el SHA-256 sobre los bytes
    /// que se escriben (§6.4.2), lo vacía al disco y lo renombra a <c>objects/ab/&lt;hash&gt;</c>.
    /// Si el objeto ya existe, el temporal se descarta (deduplicación).
    /// </summary>
    /// <param name="content">Contenido; se lee hasta el final.</param>
    /// <param name="expectedHash">Hash calculado al inspeccionar el archivo, si se conoce.</param>
    /// <param name="repositoryLock">Bloqueo vigente sobre este repositorio.</param>
    /// <param name="cancellationToken">Se comprueba en cada bloque de 1 MiB.</param>
    public async Task<ObjectWriteResult> WriteObjectAsync(
        Stream content,
        ContentHash? expectedHash,
        RepositoryLock repositoryLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        RequireLock(repositoryLock);

        var tempPath = RepositoryLayout.NewTempPath();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        var moved = false;
        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;

            var output = await storage.CreateNewAsync(tempPath, cancellationToken).ConfigureAwait(false);
            await using (output.ConfigureAwait(false))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, CopyChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    // El hash se calcula sobre el mismo búfer que se acaba de escribir.
                    sha256.AppendData(buffer, 0, read);
                    length += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = ContentHash.FromBytes(sha256.GetHashAndReset());
            await storage.CreateDirectoryAsync(RepositoryLayout.ObjectDirectory(hash), cancellationToken).ConfigureAwait(false);
            moved = await storage.TryMoveAsync(tempPath, RepositoryLayout.ObjectPath(hash), overwrite: false, cancellationToken).ConfigureAwait(false);

            return new ObjectWriteResult(
                hash,
                length,
                Stored: moved,
                ContentChanged: expectedHash is { } expected && expected != hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!moved)
            {
                await AtomicFile.TryDeleteAsync(storage, tempPath).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Abre un objeto para lectura.</summary>
    /// <exception cref="RepositoryException"><see cref="RepositoryErrorKind.ObjectNotFound"/> si no existe.</exception>
    public async Task<Stream> OpenObjectAsync(ContentHash hash, CancellationToken cancellationToken)
    {
        try
        {
            return await storage.OpenReadAsync(RepositoryLayout.ObjectPath(hash), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new RepositoryException(RepositoryErrorKind.ObjectNotFound, $"No existe el objeto {hash}.", ex);
        }
    }

    // ---------------------------------------------------------------- Snapshots

    /// <summary>Identificadores de los snapshots publicados, de más antiguo a más reciente.</summary>
    /// <remarks>Los archivos de <c>snapshots/</c> que no tienen nombre de snapshot se ignoran.</remarks>
    public async Task<IReadOnlyList<string>> ListSnapshotIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var entry in storage.EnumerateAsync(RepositoryLayout.SnapshotsDirectory, cancellationToken).ConfigureAwait(false))
        {
            if (!entry.IsDirectory && entry.Name.EndsWith(RepositoryLayout.SnapshotExtension, StringComparison.Ordinal))
            {
                var id = entry.Name[..^RepositoryLayout.SnapshotExtension.Length];
                if (SnapshotId.IsValid(id))
                {
                    ids.Add(id);
                }
            }
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>Abre un snapshot y lee su cabecera; las entradas se leen después en streaming.</summary>
    /// <exception cref="RepositoryException">
    /// <see cref="RepositoryErrorKind.SnapshotNotFound"/>, <see cref="RepositoryErrorKind.Incompatible"/> o
    /// <see cref="RepositoryErrorKind.Corrupt"/>.
    /// </exception>
    public Task<SnapshotReader> OpenSnapshotAsync(string snapshotId, CancellationToken cancellationToken) =>
        SnapshotReader.OpenAsync(storage, snapshotId, SnapshotReadBufferSize, cancellationToken);

    /// <summary>Lee solo la cabecera de un snapshot.</summary>
    public async Task<SnapshotHeader> ReadSnapshotHeaderAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var reader = await OpenSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            return reader.Header;
        }
    }

    /// <summary>Empieza a escribir un snapshot. Requiere un bloqueo vigente sobre este repositorio.</summary>
    public Task<SnapshotWriter> CreateSnapshotAsync(SnapshotStart start, RepositoryLock repositoryLock, CancellationToken cancellationToken)
    {
        RequireLock(repositoryLock);
        return SnapshotWriter.CreateAsync(storage, timeProvider, repositoryLock, start, cancellationToken);
    }

    // ---------------------------------------------------------------- Bloqueos y temporales

    /// <summary>Adquiere un bloqueo (§6.4.4).</summary>
    /// <param name="type">Tipo de bloqueo.</param>
    /// <param name="owner">Titular; por defecto, el proceso actual.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <exception cref="RepositoryException"><see cref="RepositoryErrorKind.Locked"/> si hay un bloqueo incompatible vigente.</exception>
    /// <exception cref="InvalidOperationException">Esta instancia ya tiene un bloqueo.</exception>
    public async Task<RepositoryLock> AcquireLockAsync(RepositoryLockType type, LockOwner? owner, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (lockBusy)
            {
                throw new InvalidOperationException("Esta instancia del repositorio ya tiene un bloqueo.");
            }

            // Reserva: impide una segunda adquisición simultánea mientras esta termina.
            lockBusy = true;
        }

        try
        {
            var acquired = await RepositoryLock.AcquireAsync(
                storage,
                timeProvider,
                owner ?? LockOwner.Current,
                type,
                OnLockReleased,
                cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                heldLock = acquired;
            }

            return acquired;
        }
        catch
        {
            lock (gate)
            {
                lockBusy = false;
            }

            throw;
        }
    }

    /// <summary>Bloqueos presentes en <c>locks/</c>.</summary>
    public Task<IReadOnlyList<RepositoryLockInfo>> ListLocksAsync(CancellationToken cancellationToken) =>
        RepositoryLock.ListAsync(storage, cancellationToken);

    /// <summary>Vacía <c>tmp/</c> (§6.4.6). Exige el bloqueo exclusivo.</summary>
    /// <returns>Número de archivos borrados.</returns>
    public async Task<int> CleanTemporaryFilesAsync(RepositoryLock exclusiveLock, CancellationToken cancellationToken)
    {
        RequireLock(exclusiveLock);
        if (exclusiveLock.Type != RepositoryLockType.Exclusive)
        {
            throw new InvalidOperationException("Vaciar tmp/ exige el bloqueo exclusivo.");
        }

        await exclusiveLock.EnsureHeldAsync(cancellationToken).ConfigureAwait(false);

        var names = new List<string>();
        await foreach (var entry in storage.EnumerateAsync(RepositoryLayout.TempDirectory, cancellationToken).ConfigureAwait(false))
        {
            if (!entry.IsDirectory)
            {
                names.Add(entry.Name);
            }
        }

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await storage.DeleteFileAsync(RepositoryPath.Combine(RepositoryLayout.TempDirectory, name), cancellationToken).ConfigureAwait(false);
        }

        return names.Count;
    }

    // ---------------------------------------------------------------- Auxiliares

    private void RequireLock(RepositoryLock repositoryLock)
    {
        ArgumentNullException.ThrowIfNull(repositoryLock);
        if (!ReferenceEquals(repositoryLock.Storage, storage))
        {
            throw new ArgumentException("El bloqueo es de otro repositorio.", nameof(repositoryLock));
        }

        repositoryLock.ThrowIfLost();
    }

    private void OnLockReleased(RepositoryLock released)
    {
        lock (gate)
        {
            if (ReferenceEquals(heldLock, released))
            {
                heldLock = null;
                lockBusy = false;
            }
        }
    }

    private static async Task<byte[]?> TryReadConfigAsync(IRepositoryStorage storage, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await storage.OpenReadAsync(RepositoryLayout.ConfigFile, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonFormat.ReadSmallFileAsync(stream, "repo.json", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static int? TryGetDeclaredVersion(byte[] config)
    {
        try
        {
            using var document = JsonDocument.Parse(config);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("formatVersion", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task EnsureDirectoriesAsync(IRepositoryStorage storage, CancellationToken cancellationToken)
    {
        foreach (var directory in RepositoryLayout.Directories)
        {
            if (!await storage.DirectoryExistsAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                throw new RepositoryException(RepositoryErrorKind.Corrupt, $"Falta la carpeta '{directory}' del repositorio.");
            }
        }
    }

    /// <summary>
    /// Vacía, o solo con lo que deja una inicialización interrumpida: las carpetas del formato, vacías
    /// salvo <c>tmp/</c> (que puede contener temporales).
    /// </summary>
    private static async Task<bool> IsEmptyOrInterruptedInitializationAsync(IRepositoryStorage storage, CancellationToken cancellationToken)
    {
        await foreach (var entry in storage.EnumerateAsync(string.Empty, cancellationToken).ConfigureAwait(false))
        {
            var isLayoutDirectory = entry.IsDirectory
                && RepositoryLayout.Directories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
            if (!isLayoutDirectory)
            {
                return false;
            }

            if (string.Equals(entry.Name, RepositoryLayout.TempDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await foreach (var _ in storage.EnumerateAsync(entry.Name, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }
}
