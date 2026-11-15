using Backbone.Core.Storage;

namespace Backbone.Core.Repository.Locking;

/// <summary>Tipo de bloqueo (§6.4.4).</summary>
public enum RepositoryLockType
{
    /// <summary>Copia: compatible con otras copias, no con la limpieza.</summary>
    Shared,

    /// <summary>Limpieza: exige que no haya ningún otro bloqueo vigente.</summary>
    Exclusive,
}

/// <summary>Tiempos de los bloqueos (§6.4.4).</summary>
public static class RepositoryLockPolicy
{
    /// <summary>Cada cuánto se actualiza <c>heartbeatUtc</c>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>Sin latido durante este tiempo, otro proceso puede considerar el bloqueo abandonado y borrarlo.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sin latido propio durante este tiempo, el titular da el bloqueo por perdido y deja de escribir.
    /// Es menor que <see cref="StaleAfter"/> para que el titular se entere antes de que otro lo borre.
    /// </summary>
    public static readonly TimeSpan OwnerGivesUpAfter = StaleAfter - (2 * HeartbeatInterval);

    /// <summary>Indica si un bloqueo con último latido <paramref name="heartbeatUtc"/> está abandonado en <paramref name="nowUtc"/>.</summary>
    public static bool IsStale(DateTime heartbeatUtc, DateTime nowUtc) => nowUtc - heartbeatUtc >= StaleAfter;
}

/// <summary>Proceso titular de un bloqueo; da nombre al archivo <c>locks/&lt;host&gt;-&lt;pid&gt;.lock</c>.</summary>
public sealed record LockOwner
{
    /// <summary>Crea el titular.</summary>
    /// <exception cref="ArgumentException">El nombre de equipo no es válido como nombre de archivo.</exception>
    public LockOwner(string host, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        if (!RepositoryPath.IsValidSegment(host))
        {
            throw new ArgumentException($"Nombre de equipo no válido para un bloqueo: '{host}'.", nameof(host));
        }

        Host = host;
        ProcessId = processId;
    }

    /// <summary>Equipo.</summary>
    public string Host { get; }

    /// <summary>Identificador del proceso.</summary>
    public int ProcessId { get; }

    /// <summary>Proceso actual.</summary>
    public static LockOwner Current => new(Environment.MachineName, Environment.ProcessId);

    /// <summary>Nombre del archivo de bloqueo.</summary>
    public string FileName => $"{Host}-{ProcessId}{RepositoryLayout.LockExtension}";
}

/// <summary>Bloqueo leído de <c>locks/</c>.</summary>
/// <param name="FileName">Nombre del archivo.</param>
/// <param name="Host">Equipo titular.</param>
/// <param name="ProcessId">Proceso titular.</param>
/// <param name="Type">Tipo.</param>
/// <param name="HeartbeatUtc">Último latido.</param>
/// <param name="IsReadable">
/// <see langword="false"/> si el archivo no se pudo interpretar; entonces se trata como exclusivo
/// y su latido es la fecha de modificación del archivo (criterio conservador).
/// </param>
public sealed record RepositoryLockInfo(
    string FileName,
    string Host,
    int ProcessId,
    RepositoryLockType Type,
    DateTime HeartbeatUtc,
    bool IsReadable = true);
