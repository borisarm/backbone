using Backbone.Contracts;

namespace Backbone.Service.Jobs;

/// <summary>Lo que necesita <see cref="JobRunner"/> para hacer una ejecución.</summary>
/// <param name="RunId">Identificador de la ejecución (el del historial cuando exista, HU-21).</param>
/// <param name="Job">El trabajo tal como está en <c>jobs.json</c> cuando empieza la ejecución.</param>
/// <param name="Kind">Tipo de ejecución. Hoy solo <see cref="RunKind.Backup"/>; el mantenimiento (F2) usa el mismo ciclo.</param>
/// <param name="Trigger">Qué la lanzó.</param>
/// <param name="QueuedUtc">Cuándo entró en la cola (UTC).</param>
/// <param name="Report">Recibe el progreso (fase y, al copiar, archivos y bytes); lo publica <see cref="RunQueue"/>.</param>
internal sealed record RunRequest(
    long RunId,
    JobDefinition Job,
    RunKind Kind,
    RunTrigger Trigger,
    DateTimeOffset QueuedUtc,
    Action<RunProgressEvent>? Report = null);

/// <summary>Resultado de una ejecución: su registro y los avisos y errores con ruta y motivo.</summary>
/// <param name="Record">Registro (estado final, estadísticas, snapshot y código de error).</param>
/// <param name="Issues">Incidencias (<see cref="RunDetail.Issues"/>); los avisos que no caben en el snapshot ya vienen resumidos.</param>
internal sealed record RunOutcome(RunRecord Record, IReadOnlyList<RunIssue> Issues);

/// <summary>
/// Estado de las ejecuciones para <c>GetStatusAsync</c> y <c>StatusChanged</c> (RF-15, RF-51): cola, ejecución en curso y
/// última ejecución terminada de cada trabajo. Lo implementa <see cref="RunQueue"/>.
/// </summary>
internal interface IRunStatusSource
{
    /// <summary>Último progreso conocido de la ejecución en curso; <see langword="null"/> si no hay ninguna.</summary>
    RunProgressEvent? CurrentRun { get; }

    /// <summary>Ejecuciones en cola, en orden de llegada; no incluye la que está en curso.</summary>
    IReadOnlyList<QueuedRun> Queue { get; }

    /// <summary>Última ejecución terminada del trabajo desde que arrancó el servicio (el historial persistente es HU-21).</summary>
    RunRecord? LastRun(Guid jobId);

    /// <summary>Fin de la última ejecución correcta del trabajo desde que arrancó el servicio.</summary>
    DateTimeOffset? LastSuccessUtc(Guid jobId);

    /// <summary>La cola, la ejecución en curso o una última ejecución han cambiado (se lanza fuera de los cerrojos).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Punto de extensión del paso 10 de §8.4 (HU-21 historial en SQLite, HU-22 Visor de eventos): se le llama con cada ejecución
/// terminada, antes de emitir <c>RunCompleted</c>. Un fallo de un manejador se registra y no afecta a la ejecución ni a los
/// demás manejadores.
/// </summary>
internal interface IRunCompletionHandler
{
    /// <summary>La ejecución ha terminado en cualquier estado final.</summary>
    Task OnRunCompletedAsync(RunOutcome outcome, CancellationToken ct);
}
