namespace Backbone.Service.Sources;

/// <summary>
/// Caché de handles de carpeta de una ejecución (§8.2 "Apertura relativa", "Caché de carpetas"): por origen y ruta relativa,
/// como mucho <see cref="Capacity"/> (128) handles, con expulsión del usado hace más tiempo (LRU) y nunca de uno en uso. Las
/// raíces de los orígenes no están aquí: son fijas durante toda la ejecución.
/// </summary>
/// <remarks>
/// Un handle sustituido o expulsado mientras está en uso se cierra cuando lo suelta el último que lo usa. Thread-safe.
/// </remarks>
internal sealed class SourceDirectoryCache(int capacity) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = [];
    private bool _disposed;

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>Handles en la caché (para pruebas).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Clave de una carpeta: índice del origen y ruta relativa.</summary>
    public static string Key(int root, IReadOnlyList<string> relative, int count) =>
        root.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + string.Join('\\', relative.Take(count));

    /// <summary>Toma el handle de la carpeta si está; quien lo recibe lo suelta con <see cref="Lease.Dispose"/>.</summary>
    public Lease? TryAcquire(string key)
    {
        lock (_lock)
        {
            if (_disposed || !_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.Users++;
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            return new Lease(this, entry);
        }
    }

    /// <summary>
    /// Añade (o sustituye) el handle de una carpeta recién abierta y comprobada, y lo devuelve tomado. Si la caché está llena,
    /// expulsa los menos usados que no estén en uso.
    /// </summary>
    public Lease Add(string key, ISourceHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var toClose = new List<ISourceHandle>();
        Lease lease;
        lock (_lock)
        {
            var entry = new Entry(key, handle) { Users = 1 };
            if (_disposed)
            {
                entry.Retired = true;
                return new Lease(this, entry);
            }

            if (_entries.Remove(key, out var previous))
            {
                Retire(previous, toClose);
            }

            entry.Node = _lru.AddFirst(entry);
            _entries[key] = entry;
            lease = new Lease(this, entry);

            var node = _lru.Last;
            while (_entries.Count > Capacity && node is not null)
            {
                var before = node.Previous;
                if (node.Value.Users == 0)
                {
                    _entries.Remove(node.Value.Key);
                    Retire(node.Value, toClose);
                }

                node = before;
            }
        }

        foreach (var closing in toClose)
        {
            closing.Dispose();
        }

        return lease;
    }

    public void Dispose()
    {
        var toClose = new List<ISourceHandle>();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                Retire(entry, toClose);
            }

            _entries.Clear();
        }

        foreach (var closing in toClose)
        {
            closing.Dispose();
        }
    }

    /// <summary>Saca la entrada de la lista LRU; se cierra ya si nadie la usa (bajo el cerrojo).</summary>
    private void Retire(Entry entry, List<ISourceHandle> toClose)
    {
        if (entry.Node.List is not null)
        {
            _lru.Remove(entry.Node);
        }

        entry.Retired = true;
        if (entry.Users == 0)
        {
            toClose.Add(entry.Handle);
        }
    }

    private void Release(Entry entry)
    {
        bool close;
        lock (_lock)
        {
            entry.Users--;
            close = entry.Retired && entry.Users == 0;
        }

        if (close)
        {
            entry.Handle.Dispose();
        }
    }

    internal sealed class Entry(string key, ISourceHandle handle)
    {
        public string Key => key;

        public ISourceHandle Handle => handle;

        public int Users { get; set; }

        public bool Retired { get; set; }

        public LinkedListNode<Entry> Node { get; set; } = null!; // asignado al añadirla, bajo el cerrojo
    }

    /// <summary>Uso de un handle de la caché; al desecharlo se suelta (y se cierra si ya se había sustituido o expulsado).</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly SourceDirectoryCache _cache;
        private readonly Entry _entry;
        private int _released;

        internal Lease(SourceDirectoryCache cache, Entry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        public ISourceHandle Handle => _entry.Handle;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _cache.Release(_entry);
            }
        }
    }
}
