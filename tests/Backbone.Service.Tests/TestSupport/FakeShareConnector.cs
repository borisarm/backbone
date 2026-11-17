using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Persistence;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="IShareConnector"/> (HU-05): un recurso SMB en memoria con carpetas, archivos, permisos y fallos
/// configurables, y "puertas" para dejar colgada cualquier operación (servidor apagado, red cortada a mitad).
/// </summary>
internal sealed class FakeShareConnector : IShareConnector
{
    private int _openConnections;

    /// <summary>Error de <c>WNetAddConnection2</c>, si lo hay.</summary>
    public ShareException? ConnectError { get; set; }

    /// <summary>Si no es nulo, la conexión espera a que se complete (servidor que no responde).</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    /// <summary>Recurso simulado.</summary>
    public FakeShare Share { get; } = new();

    /// <summary>Credenciales usadas: raíz, usuario y contraseña con NUL final (como las recibe <c>WNetAddConnection2</c>).</summary>
    public ConcurrentQueue<(string ShareRoot, string UserName, string PasswordWithNul)> Connections { get; } = new();

    /// <summary>La contraseña seguía legible (no puesta a cero) al terminar la llamada de conexión.</summary>
    public bool PasswordReadableAtEndOfConnect { get; private set; }

    /// <summary>Última credencial recibida (para comprobar que se desecha).</summary>
    public NasCredential? LastCredential { get; private set; }

    /// <summary>Conexiones abiertas y aún no cerradas.</summary>
    public int OpenConnections => Volatile.Read(ref _openConnections);

    /// <summary>Conexiones creadas.</summary>
    public ConcurrentQueue<FakeShareConnection> Created { get; } = new();

    public async Task<IShareConnection> ConnectAsync(NasCredential credential, CancellationToken ct)
    {
        LastCredential = credential;
        Connections.Enqueue((credential.ShareRoot, credential.UserName, credential.PasswordWithNullTerminator.ToString()));
        if (ConnectGate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        try
        {
            _ = credential.Password.Length;
            PasswordReadableAtEndOfConnect = true;
        }
        catch (ObjectDisposedException)
        {
            PasswordReadableAtEndOfConnect = false;
        }

        if (ConnectError is { } error)
        {
            throw error;
        }

        Interlocked.Increment(ref _openConnections);
        var connection = new FakeShareConnection(this, credential.ShareRoot);
        Created.Enqueue(connection);
        return connection;
    }

    internal void Closed() => Interlocked.Decrement(ref _openConnections);

    /// <summary>Indica si la credencial está desechada (contraseña puesta a cero).</summary>
    public static bool IsDisposed(NasCredential credential)
    {
        try
        {
            _ = credential.Password.Length;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}

/// <summary>Estado del recurso simulado.</summary>
internal sealed class FakeShare
{
    public ConcurrentDictionary<string, byte> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ShareException? ListError { get; set; }

    public ShareException? WriteError { get; set; }

    public ShareException? DeleteError { get; set; }

    public ShareException? SpaceError { get; set; }

    public ShareException? DialectError { get; set; }

    /// <summary>El servidor no aplica el borrado al cerrar (el archivo queda tras la escritura).</summary>
    public bool IgnoresDeleteOnClose { get; set; }

    public ShareSpace Space { get; set; } = new(100L * 1024 * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

    public string? Dialect { get; set; } = "3.1.1";

    /// <summary>
    /// Puertas por operación (<c>Exists</c>, <c>List</c>, <c>Space</c>, <c>Dialect</c>, <c>Write</c>, <c>FileExists</c>,
    /// <c>Delete</c>): la operación espera a que se completen. En <c>Write</c>, el archivo ya existe mientras espera.
    /// </summary>
    public ConcurrentDictionary<string, TaskCompletionSource> Gates { get; } = new();

    /// <summary>Rutas de todos los archivos de prueba que se han intentado crear.</summary>
    public ConcurrentQueue<string> ProbeFiles { get; } = new();

    public TaskCompletionSource Gate(string operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gates[operation] = gate;
        return gate;
    }

    internal async Task WaitAsync(string operation)
    {
        if (Gates.TryGetValue(operation, out var gate))
        {
            await gate.Task.ConfigureAwait(false);
        }
    }
}

/// <summary>Conexión simulada; falla si se usa después de cerrarla o si se cierra con operaciones en curso.</summary>
internal sealed class FakeShareConnection(FakeShareConnector owner, string shareRoot) : IShareConnection
{
    private int _inFlight;
    private int _disposed;

    public string ShareRoot { get; } = shareRoot;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Se cerró con operaciones aún en curso (no debe pasar).</summary>
    public bool DisposedWithOperationsInFlight { get; private set; }

    private FakeShare Share => owner.Share;

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) =>
        RunAsync("Exists", () => Task.FromResult(string.Equals(path, ShareRoot, StringComparison.OrdinalIgnoreCase) || Share.Directories.ContainsKey(path)));

    public Task ListDirectoryAsync(string path, CancellationToken ct) =>
        RunAsync<object?>("List", () => Share.ListError is { } e ? throw e : Task.FromResult<object?>(null));

    public Task<ShareSpace> GetSpaceAsync(string path, CancellationToken ct) =>
        RunAsync("Space", () => Share.SpaceError is { } e ? throw e : Task.FromResult(Share.Space));

    public Task<string?> GetSmbDialectAsync(string path, CancellationToken ct) =>
        RunAsync("Dialect", () => Share.DialectError is { } e ? throw e : Task.FromResult(Share.Dialect));

    public Task WriteProbeFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        RunAsync<object?>("Write", async () =>
        {
            Share.ProbeFiles.Enqueue(path);
            if (Share.WriteError is { } error)
            {
                // Como CreateFile con DELETE_ON_CLOSE: sin permiso de escritura (o de borrado) no se llega a crear.
                throw error;
            }

            if (!Share.Files.TryAdd(path, content.ToArray()))
            {
                throw new ShareException(BackboneErrorCode.AccessDenied, 80);
            }

            try
            {
                await Share.WaitAsync("Write").ConfigureAwait(false);
            }
            finally
            {
                if (!Share.IgnoresDeleteOnClose)
                {
                    Share.Files.TryRemove(path, out _);
                }
            }

            return null;
        },
        gate: false);

    public Task<bool> FileExistsAsync(string path, CancellationToken ct) =>
        RunAsync("FileExists", () => Task.FromResult(Share.Files.ContainsKey(path)));

    public Task DeleteFileAsync(string path, CancellationToken ct) =>
        RunAsync<object?>("Delete", () =>
        {
            if (Share.DeleteError is { } error)
            {
                throw error;
            }

            Share.Files.TryRemove(path, out _);
            return Task.FromResult<object?>(null);
        });

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisposedWithOperationsInFlight = Volatile.Read(ref _inFlight) != 0;
            owner.Closed();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<T> RunAsync<T>(string operation, Func<Task<T>> body, bool gate = true)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Interlocked.Increment(ref _inFlight);
        try
        {
            await Task.Yield();
            if (gate)
            {
                await Share.WaitAsync(operation).ConfigureAwait(false);
            }

            return await body().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
