using System.Text.Json;
using Backbone.Core.Storage;

namespace Backbone.Core.Repository.Locking;

/// <summary>
/// Bloqueo adquirido sobre un repositorio (§6.4.4). Mientras está vivo, actualiza su latido cada
/// <see cref="RepositoryLockPolicy.HeartbeatInterval"/>. Si no consigue renovarlo durante
/// <see cref="RepositoryLockPolicy.OwnerGivesUpAfter"/>, o su archivo desaparece, el bloqueo se da por
/// perdido: <see cref="EnsureHeldAsync"/> falla con <see cref="RepositoryErrorKind.LockLost"/> y el llamante
/// no debe publicar nada más (otro proceso pudo considerarlo abandonado y empezar una limpieza).
/// </summary>
public sealed class RepositoryLock : IAsyncDisposable
{
    private readonly IRepositoryStorage storage;
    private readonly TimeProvider timeProvider;
    private readonly string path;
    private readonly SemaphoreSlim renewGate = new(1, 1);
    private readonly CancellationTokenSource heartbeatCancellation = new();
    private readonly Action<RepositoryLock>? onReleased;
    private Task heartbeatTask = Task.CompletedTask;
    private long lastHeartbeatTicks;
    private bool lost;
    private int released;

    private RepositoryLock(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        LockOwner owner,
        RepositoryLockType type,
        DateTime heartbeatUtc,
        Action<RepositoryLock>? onReleased)
    {
        this.storage = storage;
        this.timeProvider = timeProvider;
        this.onReleased = onReleased;
        Owner = owner;
        Type = type;
        path = RepositoryPath.Combine(RepositoryLayout.LocksDirectory, owner.FileName);
        lastHeartbeatTicks = heartbeatUtc.Ticks;
    }

    /// <summary>Titular.</summary>
    public LockOwner Owner { get; }

    /// <summary>Almacenamiento del repositorio bloqueado.</summary>
    internal IRepositoryStorage Storage => storage;

    /// <summary>Tipo.</summary>
    public RepositoryLockType Type { get; }

    /// <summary>Último latido escrito con éxito (UTC).</summary>
    public DateTime LastHeartbeatUtc => new(Interlocked.Read(ref lastHeartbeatTicks), DateTimeKind.Utc);

    /// <summary>Si el bloqueo se ha perdido.</summary>
    public bool IsLost => Volatile.Read(ref lost);

    /// <summary>Si se ha liberado.</summary>
    public bool IsReleased => Volatile.Read(ref released) != 0;

    /// <summary>Último error de E/S del latido automático, si lo hubo.</summary>
    public Exception? LastHeartbeatError { get; private set; }

    /// <summary>
    /// Renueva el latido ahora y confirma que el bloqueo sigue siendo nuestro.
    /// Debe llamarse justo antes de publicar algo que dependa del bloqueo (p. ej. un snapshot).
    /// </summary>
    /// <exception cref="RepositoryException"><see cref="RepositoryErrorKind.LockLost"/> si el bloqueo se ha perdido.</exception>
    public Task EnsureHeldAsync(CancellationToken cancellationToken) => RenewAsync(cancellationToken);

    /// <summary>Lanza <see cref="RepositoryErrorKind.LockLost"/> si el bloqueo se ha perdido o liberado, sin tocar el disco.</summary>
    public void ThrowIfLost()
    {
        if (IsReleased)
        {
            throw new RepositoryException(RepositoryErrorKind.LockLost, "El bloqueo ya se ha liberado.");
        }

        if (IsLost || HasExpired(timeProvider.GetUtcNow().UtcDateTime))
        {
            Volatile.Write(ref lost, true);
            throw new RepositoryException(RepositoryErrorKind.LockLost, "El bloqueo del repositorio se ha perdido (sin latido o borrado por otro proceso).");
        }
    }

    /// <summary>Renueva el latido (§6.4.4).</summary>
    /// <exception cref="RepositoryException"><see cref="RepositoryErrorKind.LockLost"/> si el bloqueo se ha perdido.</exception>
    public async Task RenewAsync(CancellationToken cancellationToken)
    {
        await renewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfLost();

            // Si el archivo ya no está, otro proceso nos dio por abandonados: no se recrea.
            if (await storage.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false) is null)
            {
                Volatile.Write(ref lost, true);
                throw new RepositoryException(RepositoryErrorKind.LockLost, "El archivo de bloqueo ha desaparecido.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            await WriteAsync(storage, path, Owner, Type, now, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref lastHeartbeatTicks, now.Ticks);
        }
        finally
        {
            renewGate.Release();
        }
    }

    /// <summary>Detiene el latido y borra el archivo de bloqueo.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return;
        }

        try
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            await heartbeatTask.ConfigureAwait(false);
            await renewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await storage.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                renewGate.Release();
            }
        }
        finally
        {
            onReleased?.Invoke(this);
        }
    }

    /// <summary>Libera el bloqueo; los errores de E/S se ignoran (el bloqueo caducará solo).</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin latido, otro proceso lo considerará abandonado pasados 15 minutos.
        }
        finally
        {
            heartbeatCancellation.Dispose();
            renewGate.Dispose();
        }
    }

    /// <summary>Adquiere un bloqueo (§6.4.4).</summary>
    /// <remarks>
    /// Algoritmo: primero se publica el bloqueo propio y después se examinan los demás. Si dos procesos
    /// compiten, al menos uno ve al otro; en el peor caso fallan ambos, nunca entran los dos.
    /// La adquisición exclusiva borra los bloqueos abandonados.
    /// </remarks>
    internal static async Task<RepositoryLock> AcquireAsync(
        IRepositoryStorage storage,
        TimeProvider timeProvider,
        LockOwner owner,
        RepositoryLockType type,
        Action<RepositoryLock>? onReleased,
        CancellationToken cancellationToken)
    {
        var ownPath = RepositoryPath.Combine(RepositoryLayout.LocksDirectory, owner.FileName);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Si existe un archivo con nuestro nombre, es de un proceso anterior con el mismo PID que ya no
        // existe (el nuestro está vivo): se sustituye.
        await WriteAsync(storage, ownPath, owner, type, now, cancellationToken).ConfigureAwait(false);

        var acquired = false;
        try
        {
            var others = await ListAsync(storage, cancellationToken).ConfigureAwait(false);
            var stale = new List<RepositoryLockInfo>();
            foreach (var other in others)
            {
                if (string.Equals(other.FileName, owner.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (RepositoryLockPolicy.IsStale(other, now))
                {
                    stale.Add(other);
                    continue;
                }

                if (type == RepositoryLockType.Exclusive || other.Type == RepositoryLockType.Exclusive)
                {
                    throw new RepositoryException(
                        RepositoryErrorKind.Locked,
                        $"El repositorio está bloqueado ({(other.Type == RepositoryLockType.Exclusive ? "exclusive" : "shared")}) por {Untrusted.Describe(other.Host)} (proceso {other.ProcessId}).");
                }
            }

            if (type == RepositoryLockType.Exclusive)
            {
                foreach (var abandoned in stale)
                {
                    // Un nombre que no es un segmento válido no se puede direccionar en el repositorio: no se borra nunca
                    // (R8); abandonado ya no bloquea a nadie.
                    if (!abandoned.IsAddressable || !RepositoryPath.IsValidSegment(abandoned.FileName))
                    {
                        continue;
                    }

                    await storage.DeleteFileAsync(
                        RepositoryPath.Combine(RepositoryLayout.LocksDirectory, abandoned.FileName),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var repositoryLock = new RepositoryLock(storage, timeProvider, owner, type, now, onReleased);
            repositoryLock.heartbeatTask = repositoryLock.HeartbeatLoopAsync(repositoryLock.heartbeatCancellation.Token);
            acquired = true;
            return repositoryLock;
        }
        finally
        {
            if (!acquired)
            {
                await AtomicFile.TryDeleteAsync(storage, ownPath).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lee todos los bloqueos de <c>locks/</c>.</summary>
    internal static async Task<IReadOnlyList<RepositoryLockInfo>> ListAsync(IRepositoryStorage storage, CancellationToken cancellationToken)
    {
        var result = new List<RepositoryLockInfo>();
        await foreach (var entry in storage.EnumerateAsync(RepositoryLayout.LocksDirectory, cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsDirectory || !entry.Name.EndsWith(RepositoryLayout.LockExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = await TryReadAsync(storage, entry, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>
    /// Lee un bloqueo. Su contenido es no confiable (§6.1, §6.4.4): un nombre de archivo que no es un segmento válido, un
    /// <c>host</c> que no lo es o cualquier dato que no se entienda hacen que se trate como ilegible (exclusivo vigente).
    /// </summary>
    private static async Task<RepositoryLockInfo?> TryReadAsync(IRepositoryStorage storage, StorageEntry entry, CancellationToken cancellationToken)
    {
        if (!RepositoryPath.IsValidSegment(entry.Name))
        {
            // Nombre que el NAS admite pero Backbone no (p. ej. un nombre de dispositivo): no se puede abrir.
            return Unreadable(entry, addressable: false);
        }

        var lockPath = RepositoryPath.Combine(RepositoryLayout.LocksDirectory, entry.Name);
        byte[] content;
        try
        {
            var stream = await storage.OpenReadAsync(lockPath, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                content = await JsonFormat.ReadSmallFileAsync(stream, "El bloqueo", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            // Se liberó mientras se enumeraba.
            return null;
        }
        catch (RepositoryException)
        {
            return Unreadable(entry);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Error de lectura (permisos, archivo en uso, red): bloqueo ilegible, no un fallo de la operación (§6.4.4).
            return Unreadable(entry);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var host = root.GetProperty("host").GetString();
            var pid = root.GetProperty("pid").GetInt32();
            RepositoryLockType? type = root.GetProperty("type").GetString() switch
            {
                "shared" => RepositoryLockType.Shared,
                "exclusive" => RepositoryLockType.Exclusive,
                _ => null,
            };
            if (!RepositoryPath.IsValidSegment(host) || type is null || !JsonFormat.TryGetUtc(root.GetProperty("heartbeatUtc"), out var heartbeat))
            {
                return Unreadable(entry);
            }

            return new RepositoryLockInfo(entry.Name, host, pid, type.Value, heartbeat, entry.LastWriteUtc);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Unreadable(entry);
        }
    }

    private static RepositoryLockInfo Unreadable(StorageEntry entry, bool addressable = true) =>
        new(entry.Name, "?", 0, RepositoryLockType.Exclusive, entry.LastWriteUtc, entry.LastWriteUtc, IsReadable: false, IsAddressable: addressable);

    private static Task<bool> WriteAsync(
        IRepositoryStorage storage,
        string lockPath,
        LockOwner owner,
        RepositoryLockType type,
        DateTime heartbeatUtc,
        CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            storage,
            lockPath,
            overwrite: true,
            async (stream, ct) =>
            {
                var writer = new Utf8JsonWriter(stream, JsonFormat.WriterOptions(indented: true));
                await using (writer.ConfigureAwait(false))
                {
                    writer.WriteStartObject();
                    writer.WriteString("host", owner.Host);
                    writer.WriteNumber("pid", owner.ProcessId);
                    writer.WriteString("type", type == RepositoryLockType.Exclusive ? "exclusive" : "shared");
                    JsonFormat.WriteUtc(writer, "heartbeatUtc", heartbeatUtc);
                    writer.WriteEndObject();
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            },
            cancellationToken);

    private bool HasExpired(DateTime nowUtc) => nowUtc - LastHeartbeatUtc >= RepositoryLockPolicy.OwnerGivesUpAfter;

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // Cede el control para que la adquisición termine antes del primer latido.
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RepositoryLockPolicy.HeartbeatInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                await RenewAsync(cancellationToken).ConfigureAwait(false);
                LastHeartbeatError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RepositoryException ex) when (ex.Kind == RepositoryErrorKind.LockLost)
            {
                LastHeartbeatError = ex;
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Error transitorio (red): se reintenta en el siguiente latido. Si persiste, el bloqueo caduca.
                LastHeartbeatError = ex;
            }
        }
    }
}
