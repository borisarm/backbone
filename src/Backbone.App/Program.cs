using Backbone.App.Ipc;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Backbone.App;

/// <summary>
/// Punto de entrada propio (<c>DISABLE_XAML_GENERATED_MAIN</c>) para decidir la instancia única antes de crear ninguna ventana (§9.1):
/// una segunda ejecución redirige su activación a la primera (que se trae al frente) y solo entonces termina.
/// </summary>
internal static class Program
{
    private const string InstanceKey = "Backbone.App.SingleInstance";
    private const uint Infinite = 0xFFFFFFFF;

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var key = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!key.IsCurrent)
        {
            // La segunda instancia espera a que la primera haya recibido la activación; si no se pudiera, sigue sin cerrar en silencio.
            return RedirectActivationTo(key) ? 0 : 1;
        }

        key.Activated += static (_, _) => App.ActivateExistingInstance();

        Application.Start(static callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Redirige la activación a la instancia que posee la clave. Se hace en otro hilo y se espera sin bloquear el STA con
    /// <c>CoWaitForMultipleObjects</c> (recomendación de Microsoft Learn para evitar interbloqueos).
    /// </summary>
    private static bool RedirectActivationTo(AppInstance key)
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Esta segunda instancia tiene el foco (el usuario acaba de lanzarla): se lo cede a la primera para que pueda mostrarse delante.
        NativeMethods.AllowSetForegroundWindow(key.ProcessId);

        var done = NativeMethods.CreateEvent(0, true, false, null);
        if (done == 0)
        {
            return false;
        }

        var ok = false;
        try
        {
            // La redirección se lanza desde otro hilo y se señala el evento al terminar (sin bloquear el STA).
            _ = Task.Run(() => key.RedirectActivationToAsync(args).AsTask()).ContinueWith(
                task =>
                {
                    ok = task.IsCompletedSuccessfully;
                    NativeMethods.SetEvent(done);
                },
                TaskScheduler.Default);
            _ = NativeMethods.CoWaitForMultipleObjects(0, Infinite, 1, [done], out _);
        }
        finally
        {
            NativeMethods.CloseHandle(done);
        }

        return ok;
    }
}
