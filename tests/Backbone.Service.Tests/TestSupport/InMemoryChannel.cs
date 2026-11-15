using Backbone.Service.Ipc;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary><see cref="IClientChannel"/> en memoria: flujo dúplex con una identidad de cliente fija.</summary>
internal sealed class InMemoryChannel(Stream serverStream, Func<ClientIdentity> identity) : IClientChannel
{
    private int _disposed;
    private int _identityReads;

    public Stream Stream => serverStream;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public int IdentityReads => Volatile.Read(ref _identityReads);

    public ClientIdentity GetClientIdentity()
    {
        Interlocked.Increment(ref _identityReads);
        return identity();
    }

    public void RunAsClient(Action action) => action();

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return serverStream.DisposeAsync();
    }
}
