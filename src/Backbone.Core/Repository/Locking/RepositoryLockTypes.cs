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

    /// <summary>
    /// Sin latido durante este tiempo, otro proceso puede considerar el bloqueo abandonado y borrarlo. Es también la
    /// tolerancia de reloj para latidos en el futuro (§6.4.4).
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sin latido propio durante este tiempo, el titular da el bloqueo por perdido y deja de escribir.
    /// Es menor que <see cref="StaleAfter"/> para que el titular se entere antes de que otro lo borre.
    /// </summary>
    public static readonly TimeSpan OwnerGivesUpAfter = StaleAfter - (2 * HeartbeatInterval);

    /// <summary>Indica si el bloqueo <paramref name="info"/> está abandonado en <paramref name="nowUtc"/> (§6.4.4).</summary>
    public static bool IsStale(RepositoryLockInfo info, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(info);
        return IsStale(info.HeartbeatUtc, info.ModifiedUtc, nowUtc);
    }

    /// <summary>
    /// Regla de vigencia sin estado (§6.4.4). <paramref name="heartbeatUtc"/> es el latido del bloqueo (su
    /// <c>heartbeatUtc</c> si es legible; la fecha de modificación si no) y <paramref name="modifiedUtc"/> la fecha de
    /// modificación del archivo. <see cref="StaleAfter"/> hace también de tolerancia de reloj:
    /// <list type="number">
    /// <item>Un latido más allá de <c>ahora + T</c> es anómalo y se sustituye por la fecha de modificación si esta no lo
    /// es; si también lo es, el bloqueo está abandonado (nadie puede bloquear indefinidamente con fechas futuras).</item>
    /// <item>Con un latido no anómalo, el bloqueo está vigente si <c>ahora − L &lt; T</c> (un latido futuro dentro de la
    /// tolerancia es vigente) y abandonado si no.</item>
    /// </list>
    /// </summary>
    public static bool IsStale(DateTime heartbeatUtc, DateTime modifiedUtc, DateTime nowUtc)
    {
        var latestAcceptable = nowUtc + StaleAfter;
        var heartbeat = heartbeatUtc;
        if (heartbeat > latestAcceptable)
        {
            if (modifiedUtc > latestAcceptable)
            {
                return true;
            }

            heartbeat = modifiedUtc;
        }

        return nowUtc - heartbeat >= StaleAfter;
    }
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
            throw new ArgumentException($"Nombre de equipo no válido para un bloqueo: '{Untrusted.Describe(host)}'.", nameof(host));
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

/// <summary>Bloqueo leído de <c>locks/</c>. <see cref="FileName"/> y <see cref="Host"/> son texto no confiable (§6.1): se sanean antes de registrarlos o mostrarlos.</summary>
/// <param name="FileName">Nombre del archivo.</param>
/// <param name="Host">Equipo titular (segmento válido; <c>?</c> si el bloqueo es ilegible).</param>
/// <param name="ProcessId">Proceso titular.</param>
/// <param name="Type">Tipo.</param>
/// <param name="HeartbeatUtc">Último latido (<c>heartbeatUtc</c> si es legible; la fecha de modificación si no).</param>
/// <param name="ModifiedUtc">Fecha de modificación del archivo; sustituye a un latido anómalo (§6.4.4).</param>
/// <param name="IsReadable">
/// <see langword="false"/> si el archivo no se pudo interpretar; entonces se trata como exclusivo
/// y su latido es la fecha de modificación del archivo (criterio conservador).
/// </param>
/// <param name="IsAddressable">
/// <see langword="false"/> si el nombre no es un segmento que Backbone pueda direccionar (§6.1, R8): cuenta como ilegible
/// y no se borra nunca, tampoco abandonado.
/// </param>
public sealed record RepositoryLockInfo(
    string FileName,
    string Host,
    int ProcessId,
    RepositoryLockType Type,
    DateTime HeartbeatUtc,
    DateTime ModifiedUtc,
    bool IsReadable = true,
    bool IsAddressable = true);
