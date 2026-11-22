using System.Collections.Concurrent;
using Backbone.Core.Storage;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// Doble de <see cref="IRepositoryStorageFactory"/> (HU-06): un <see cref="InMemoryRepositoryStorage"/> por destino (sin
/// distinguir mayúsculas), vacío si la prueba no lo ha preparado. Comprueba que la conexión sigue abierta al crear el
/// almacenamiento y registra las conexiones con las que se pidió.
/// </summary>
internal sealed class FakeRepositoryStorageFactory : IRepositoryStorageFactory
{
    private readonly ConcurrentDictionary<string, IRepositoryStorage> _storages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InMemoryRepositoryStorage> _memories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Destinos para los que se ha creado un almacenamiento, en orden.</summary>
    public ConcurrentQueue<string> Requested { get; } = new();

    /// <summary>Conexiones con las que se ha pedido el almacenamiento.</summary>
    public ConcurrentQueue<IShareConnection> Connections { get; } = new();

    /// <summary>Almacenamiento (en memoria) del destino; lo crea vacío si no existía.</summary>
    public InMemoryRepositoryStorage Memory(string destination) =>
        _memories.GetOrAdd(destination, static _ => new InMemoryRepositoryStorage(createRoot: false));

    /// <summary>
    /// Sustituye el almacenamiento del destino (p. ej. por uno que falla o que envuelve a <see cref="Memory"/>); <see cref="Memory"/>
    /// sigue dando el repositorio en memoria.
    /// </summary>
    public void Set(string destination, IRepositoryStorage storage) => _storages[destination] = storage;

    public IRepositoryStorage Create(IShareConnection connection, string destination)
    {
        Requested.Enqueue(destination);
        Connections.Enqueue(connection);
        return _storages.TryGetValue(destination, out var storage) ? storage : Memory(destination);
    }
}
