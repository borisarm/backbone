using System.Collections.Concurrent;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Doble de <see cref="IEventLogWriter"/> que guarda las entradas.</summary>
internal sealed class FakeEventLogWriter : IEventLogWriter
{
    public ConcurrentQueue<(EventLogLevel Level, int EventId, string Message)> Entries { get; } = new();

    public void Write(EventLogLevel level, int eventId, string message) => Entries.Enqueue((level, eventId, message));
}
