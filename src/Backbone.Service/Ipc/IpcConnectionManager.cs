using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Ipc;

/// <summary>
/// Conexiones abiertas: alta, límite por cuenta (§7.1, B-4), revalidación periódica y al cambiar la lista de usuarios
/// autorizados (§7.3), y envío de eventos a todas o solo a las de un SID.
/// </summary>
internal sealed partial class IpcConnectionManager : IAsyncDisposable
{
    private readonly CallerAuthorizer _authorizer;
    private readonly IAccountDirectory _accounts;
    private readonly IAuthorizedUserList _authorizedUsers;
    private readonly BackboneOperations _operations;
    private readonly IpcOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, IpcConnection> _connections = [];
    private readonly Dictionary<string, int> _perSid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITimer _revalidationTimer;
    private long _nextId;
    private bool _disposed;

    public IpcConnectionManager(
        CallerAuthorizer authorizer,
        IAccountDirectory accounts,
        IAuthorizedUserList authorizedUsers,
        BackboneOperations operations,
        IpcOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        _authorizer = authorizer;
        _accounts = accounts;
        _authorizedUsers = authorizedUsers;
        _operations = operations;
        _options = options;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IpcConnectionManager>();
        _authorizedUsers.Changed += OnAuthorizedUsersChanged;
        _revalidationTimer = timeProvider.CreateTimer(
            static state => ((IpcConnectionManager)state!).RevalidateAll(), this, options.RevalidationInterval, options.RevalidationInterval);
    }

    /// <summary>Conexiones abiertas.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>Se ha alcanzado <see cref="IpcOptions.MaxConnections"/> (o el gestor ya se ha liberado).</summary>
    public bool IsFull
    {
        get
        {
            lock (_gate)
            {
                return _disposed || _connections.Count >= _options.MaxConnections;
            }
        }
    }

    /// <summary>Instantánea de las conexiones abiertas.</summary>
    public IReadOnlyList<IpcConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _connections.Values];
            }
        }
    }

    /// <summary>
    /// Da de alta una conexión ya establecida y empieza a atenderla. Si se ha alcanzado
    /// <see cref="IpcOptions.MaxConnections"/>, la cierra y devuelve <see langword="null"/>.
    /// </summary>
    public IpcConnection? Accept(IClientChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        IpcConnection connection;
        lock (_gate)
        {
            if (_disposed || _connections.Count >= _options.MaxConnections)
            {
                LogServerFull(_logger, _connections.Count);
                _ = channel.DisposeAsync().AsTask();
                return null;
            }

            var id = ++_nextId;
            connection = new IpcConnection(
                id, channel, this, _authorizer, _accounts, _options, _timeProvider, _loggerFactory.CreateLogger<IpcConnection>());
            _connections.Add(id, connection);
        }

        connection.Start(new ConnectionRpcTarget(connection, _operations));
        return connection;
    }

    /// <summary>Envía un evento a todas las conexiones autorizadas.</summary>
    public void Broadcast(string eventName, object argument)
    {
        foreach (var connection in Connections)
        {
            connection.Notify(eventName, argument);
        }
    }

    /// <summary>Envía un evento solo a las conexiones autorizadas de <paramref name="sid"/> (restauraciones, §7.3 A-2).</summary>
    public void SendToSid(string sid, string eventName, object argument)
    {
        foreach (var connection in Connections)
        {
            if (connection.Identity is { } identity && string.Equals(identity.Sid.Value, sid, StringComparison.OrdinalIgnoreCase))
            {
                connection.Notify(eventName, argument);
            }
        }
    }

    /// <summary>Revalida todas las conexiones autorizadas y cierra las que ya no lo están.</summary>
    public void RevalidateAll()
    {
        foreach (var connection in Connections)
        {
            connection.Revalidate();
        }
    }

    /// <summary>Contabiliza la conexión para su SID; <see langword="false"/> si la cuenta ya tiene el máximo.</summary>
    internal bool TryCountConnection(IpcConnection connection, string sid)
    {
        lock (_gate)
        {
            if (connection.CountedSid is not null)
            {
                return true;
            }

            _perSid.TryGetValue(sid, out var count);
            if (count >= _options.MaxConnectionsPerSid)
            {
                return false;
            }

            _perSid[sid] = count + 1;
            connection.CountedSid = sid;
            return true;
        }
    }

    internal void Remove(IpcConnection connection)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connection.Id))
            {
                return;
            }

            if (connection.CountedSid is { } sid && _perSid.TryGetValue(sid, out var count))
            {
                if (count <= 1)
                {
                    _perSid.Remove(sid);
                }
                else
                {
                    _perSid[sid] = count - 1;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _authorizedUsers.Changed -= OnAuthorizedUsersChanged;
        await _revalidationTimer.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(Connections.Select(c => c.CloseAsync(CloseReason.ServerShutdown))).ConfigureAwait(false);
    }

    private void OnAuthorizedUsersChanged(object? sender, EventArgs e) => RevalidateAll();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexión IPC rechazada: ya hay {Count} conexiones abiertas.")]
    private static partial void LogServerFull(ILogger logger, int count);
}
