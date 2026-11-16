using Backbone.App.Ipc;

namespace Backbone.App.Tray;

/// <summary>Aspecto del icono de la bandeja (RF-50).</summary>
public enum TrayIconKind
{
    /// <summary>Todo correcto (verde).</summary>
    Ok = 0,

    /// <summary>Con avisos, desactualizado o conectando (amarillo).</summary>
    Warning = 1,

    /// <summary>Error o servicio no disponible (rojo).</summary>
    Error = 2,

    /// <summary>Copiando (azul, animado).</summary>
    Copying = 3,
}

/// <summary>Estado resumido que muestra la bandeja: icono, texto emergente y menú.</summary>
/// <param name="Kind">Icono.</param>
/// <param name="Connection">Estado de la conexión con el servicio.</param>
/// <param name="LastSuccessUtc">Fin de la última copia correcta de cualquier trabajo; <see langword="null"/> si no hay o se desconoce.</param>
/// <param name="Paused">Hay una pausa global activa (F2).</param>
/// <param name="RunnableJobs">Trabajos activados sin ejecución en curso ni en cola (los que "Copiar todo ahora" lanzaría).</param>
internal sealed record TrayPresentation(
    TrayIconKind Kind,
    ConnectionState Connection,
    DateTimeOffset? LastSuccessUtc,
    bool Paused,
    int RunnableJobs)
{
    /// <summary>Estado inicial, antes de conocer nada del servicio.</summary>
    public static TrayPresentation Initial { get; } = new(TrayIconKind.Warning, ConnectionState.Connecting, null, false, 0);
}
