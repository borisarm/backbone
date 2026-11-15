using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Proveedor de log que guarda los mensajes formateados (auditoría, ausencia de argumentos y secretos).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>Todo el texto registrado, incluidas las excepciones.</summary>
    public string AllText() => string.Join(Environment.NewLine, Entries.Select(e => e.Message + " " + e.Exception));

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            provider.Entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception, properties));
        }
    }
}

internal sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties);
