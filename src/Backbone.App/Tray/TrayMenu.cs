using Backbone.App.Ipc;

namespace Backbone.App.Tray;

/// <summary>Órdenes del menú contextual de la bandeja (RF-50). El valor es el identificador del elemento nativo.</summary>
internal enum TrayCommand
{
    None = 0,
    RunAllNow = 1,
    Pause = 2,
    OpenApp = 3,
    Exit = 4,
}

/// <summary>Elemento del menú: orden y si está habilitado.</summary>
internal readonly record struct TrayMenuItem(TrayCommand Command, bool Enabled);

/// <summary>Contenido del menú según el estado.</summary>
internal static class TrayMenu
{
    /// <summary>
    /// Elementos en orden: Copiar todo ahora, Pausar, Abrir Backbone, Salir.
    /// "Pausar" es F2 (RF-12): se muestra siempre deshabilitado mientras <paramref name="pauseAvailable"/> sea falso.
    /// "Copiar todo ahora" solo está habilitado con conexión y algún trabajo que lanzar.
    /// </summary>
    public static IReadOnlyList<TrayMenuItem> Build(TrayPresentation p, bool pauseAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(p);
        return
        [
            new(TrayCommand.RunAllNow, p.Connection == ConnectionState.Connected && p.RunnableJobs > 0),
            new(TrayCommand.Pause, pauseAvailable && p.Connection == ConnectionState.Connected),
            new(TrayCommand.OpenApp, true),
            new(TrayCommand.Exit, true),
        ];
    }
}
