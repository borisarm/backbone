using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Os;

/// <summary>
/// <see cref="IEventLogWriter"/> sobre <see cref="EventLog"/>. No crea el origen (lo registra el instalador, §10): si no
/// existe, no escribe, porque <see cref="EventLog.WriteEntry(string, string)"/> intentaría crearlo.
/// </summary>
internal sealed partial class WindowsEventLogWriter(ILogger<WindowsEventLogWriter> logger) : IEventLogWriter
{
    public const string SourceName = "Backbone";

    public void Write(EventLogLevel level, int eventId, string message)
    {
        try
        {
            if (!EventLog.SourceExists(SourceName))
            {
                LogSourceMissing(logger, SourceName, eventId);
                return;
            }

            var type = level switch
            {
                EventLogLevel.Error => EventLogEntryType.Error,
                EventLogLevel.Warning => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information,
            };
            EventLog.WriteEntry(SourceName, message, type, eventId);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or Win32Exception or IOException or ArgumentException)
        {
            LogWriteFailed(logger, eventId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "El origen {Source} no existe en el Visor de eventos; no se escribe el evento {EventId}.")]
    private static partial void LogSourceMissing(ILogger logger, string source, int eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se ha podido escribir el evento {EventId} en el Visor de eventos ({ErrorType}).")]
    private static partial void LogWriteFailed(ILogger logger, int eventId, string errorType);
}
