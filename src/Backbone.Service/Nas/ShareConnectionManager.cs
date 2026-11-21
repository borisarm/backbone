using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Backbone.Contracts;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Nas;

/// <summary>
/// Gestor único de las conexiones SMB del servicio (revisión de seguridad de HU-05, MEDIA-1): una conexión por raíz de recurso
/// con recuento de referencias. Toda conexión al NAS (la prueba de conexión y, después, las copias, restauraciones y
/// mantenimiento) pasa por aquí, nunca por <see cref="IShareConnector"/> directamente.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="AcquireAsync"/> reutiliza la conexión viva (o en curso) de la raíz o la crea; solo la última liberación la
/// cierra (<c>WNetCancelConnection2</c>), así que una prueba nunca corta la conexión de una copia.</item>
/// <item>Windows solo admite <b>un usuario por servidor</b> en la sesión de inicio del servicio, y con una sesión SMB viva no
/// vuelve a comprobar la contraseña. Por eso cada conexión guarda una huella de su credencial (HMAC-SHA-256 de usuario y
/// contraseña con una clave aleatoria de este proceso, que nunca sale de memoria) y, si ya hay una conexión con ese servidor
/// con otra credencial, se rechaza con <see cref="BackboneErrorCode.CredentialsRejected"/>
/// (<c>ERROR_SESSION_CREDENTIAL_CONFLICT</c>) sin llamar al sistema (BAJA-1).</item>
/// </list>
/// </remarks>
internal sealed partial class ShareConnectionManager(IShareConnector connector, ILogger<ShareConnectionManager> logger) : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _fingerprintKey = RandomNumberGenerator.GetBytes(32);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raíces con conexión viva o en curso.</summary>
    public IReadOnlyList<string> ActiveShareRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Keys];
            }
        }
    }

    /// <summary>Servidor de una raíz normalizada <c>\\servidor\recurso</c>.</summary>
    public static string ServerOf(string shareRoot) => shareRoot[2..shareRoot.IndexOf('\\', 2)];

    /// <summary>
    /// Conexión con <see cref="NasCredential.ShareRoot"/>. La credencial sigue siendo del llamante, que no la desecha hasta que
    /// esta tarea termina. El plazo lo pone el llamante; si abandona la tarea, debe desechar la concesión si acaba bien.
    /// </summary>
    /// <exception cref="ShareException">
    /// La conexión falla, o ya hay una conexión con ese servidor con otra credencial (<see cref="BackboneErrorCode.CredentialsRejected"/>).
    /// </exception>
    public async Task<ShareLease> AcquireAsync(NasCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var fingerprint = Fingerprint(credential);
        var server = ServerOf(credential.ShareRoot);
        Entry entry;
        lock (_gate)
        {
            foreach (var other in _entries.Values)
            {
                if (string.Equals(other.Server, server, StringComparison.OrdinalIgnoreCase)
                    && !CryptographicOperations.FixedTimeEquals(other.Fingerprint, fingerprint))
                {
                    LogConflict(logger, UntrustedText.Describe(server));
                    throw new ShareException(BackboneErrorCode.CredentialsRejected, ShareErrors.ErrorSessionCredentialConflict);
                }
            }

            if (_entries.TryGetValue(credential.ShareRoot, out var existing))
            {
                entry = existing;
                entry.References++;
            }
            else
            {
                entry = new Entry(credential.ShareRoot, server, fingerprint, connector.ConnectAsync(credential, CancellationToken.None));
                _entries.Add(credential.ShareRoot, entry);
            }
        }

        try
        {
            var connection = await entry.Connecting.WaitAsync(ct).ConfigureAwait(false);
            return new ShareLease(this, entry, connection);
        }
        catch
        {
            // Si se abandona por cancelación, la referencia se suelta cuando la conexión termine (y se cierra si era la última).
            if (entry.Connecting.IsCompleted)
            {
                await ReleaseAsync(entry).ConfigureAwait(false);
            }
            else
            {
                _ = entry.Connecting.ContinueWith(_ => ReleaseAsync(entry), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }

            throw;
        }
    }

    /// <summary>Cierra a la fuerza las conexiones que sigan abiertas o en curso (parada del servicio).</summary>
    public async Task ForceDisconnectAllAsync(CancellationToken ct)
    {
        foreach (var root in ActiveShareRoots)
        {
            try
            {
                await connector.ForceDisconnectAsync(root, ct).ConfigureAwait(false);
                LogForced(logger, UntrustedText.Describe(root));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogForceFailed(logger, UntrustedText.Describe(root), ex.GetType().Name, ex.HResult);
            }
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_fingerprintKey);

    internal async Task ReleaseAsync(Entry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References > 0)
            {
                return;
            }

            if (_entries.TryGetValue(entry.ShareRoot, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(entry.ShareRoot);
            }
        }

        if (entry.Connecting.IsCompletedSuccessfully)
        {
            var connection = await entry.Connecting.ConfigureAwait(false); // ya terminada: no espera
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        else if (entry.Connecting.IsFaulted)
        {
            _ = entry.Connecting.Exception; // observada
        }
    }

    private byte[] Fingerprint(NasCredential credential)
    {
        var password = credential.Password;
        var size = StrictUtf8.GetByteCount(credential.UserName) + 1 + StrictUtf8.GetByteCount(password);
        var buffer = GC.AllocateArray<byte>(size, pinned: true);
        try
        {
            var written = StrictUtf8.GetBytes(credential.UserName, buffer);
            buffer[written++] = 0;
            StrictUtf8.GetBytes(password, buffer.AsSpan(written));
            return HMACSHA256.HashData(_fingerprintKey, buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ya hay una conexión con el servidor {Server} con otra credencial: se rechaza sin conectar (Windows admite un solo usuario por servidor).")]
    private static partial void LogConflict(ILogger logger, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión con {ShareRoot} cerrada a la fuerza en la parada del servicio.")]
    private static partial void LogForced(ILogger logger, string shareRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo cerrar a la fuerza la conexión con {ShareRoot} ({ExceptionType}, HResult {HResult}).")]
    private static partial void LogForceFailed(ILogger logger, string shareRoot, string exceptionType, int hResult);

    /// <summary>Conexión de una raíz y sus referencias (protegidas por el cerrojo del gestor).</summary>
    internal sealed class Entry(string shareRoot, string server, byte[] fingerprint, Task<IShareConnection> connecting)
    {
        public string ShareRoot { get; } = shareRoot;

        public string Server { get; } = server;

        public byte[] Fingerprint { get; } = fingerprint;

        public Task<IShareConnection> Connecting { get; } = connecting;

        public int References { get; set; } = 1;
    }
}

/// <summary>Uso de una conexión del <see cref="ShareConnectionManager"/>; desecharla suelta la referencia (idempotente).</summary>
internal sealed class ShareLease : IAsyncDisposable
{
    private readonly ShareConnectionManager _manager;
    private readonly ShareConnectionManager.Entry _entry;
    private int _released;

    internal ShareLease(ShareConnectionManager manager, ShareConnectionManager.Entry entry, IShareConnection connection)
    {
        _manager = manager;
        _entry = entry;
        Connection = connection;
    }

    public IShareConnection Connection { get; }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _released, 1) == 0 ? new ValueTask(_manager.ReleaseAsync(_entry)) : ValueTask.CompletedTask;
}
