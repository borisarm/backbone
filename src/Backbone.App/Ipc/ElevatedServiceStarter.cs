using System.ComponentModel;
using System.Diagnostics;

namespace Backbone.App.Ipc;

/// <summary>Lanza <c>sc start BackboneService</c> elevado mediante <c>runas</c> (UAC) (§9.3).</summary>
internal sealed class ElevatedServiceStarter : IServiceStarter
{
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("sc.exe", "start " + ServiceProcessVerifier.DefaultServiceName)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
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
