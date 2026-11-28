using System.ComponentModel;
using System.Diagnostics;

namespace Backbone.App.Ipc;

/// <summary>Lanza <c>sc start BackboneService</c> elevado mediante <c>runas</c> (UAC) (§9.3).</summary>
internal sealed class ElevatedServiceStarter : IServiceStarter
{
    /// <summary>
    /// Construye el arranque de <c>sc.exe</c> con ruta absoluta del directorio del sistema, para que un <c>sc.exe</c> plantado en
    /// el directorio actual, el PATH o App Paths no se eleve con un UAC de aspecto legítimo.
    /// </summary>
    /// <returns>La información de arranque.</returns>
    internal static ProcessStartInfo CreateStartInfo()
    {
        var systemDirectory = Environment.SystemDirectory;
        return new ProcessStartInfo(Path.Combine(systemDirectory, "sc.exe"), "start " + ServiceProcessVerifier.DefaultServiceName)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = systemDirectory,
        };
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        var info = CreateStartInfo();
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Win32Exception)
        {
            // El usuario canceló el aviso de UAC (error 1223) o no hay permiso: se queda el banner.
            return false;
        }
    }
}
