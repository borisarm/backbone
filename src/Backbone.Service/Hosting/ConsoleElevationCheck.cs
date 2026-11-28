using System.Security.Principal;

namespace Backbone.Service.Hosting;

/// <summary>
/// Comprobación previa del modo <c>--console</c> (herramienta de desarrollo): la carpeta de datos solo es accesible para SYSTEM y
/// Administradores (RNF-07), así que una consola sin elevar no puede crearla. Se avisa antes de construir el host y sin tocar
/// la carpeta, en lugar de fallar más tarde con un error de Win32 (1307) y un evento en el Visor.
/// </summary>
internal static class ConsoleElevationCheck
{
    /// <summary>Mensaje de consola (no es interfaz de la app, no va a <c>.resw</c>).</summary>
    public const string NotElevatedMessage =
        "Backbone.Service --console necesita una consola elevada (Ejecutar como administrador): la carpeta de datos solo es accesible para SYSTEM y Administradores (RNF-07).";

    /// <summary>Código de salida cuando se rechaza el arranque.</summary>
    public const int ExitCode = 1;

    /// <summary>
    /// <see langword="true"/> si el proceso puede arrancar: no es modo consola, o lo es y <paramref name="isElevated"/> devuelve
    /// <see langword="true"/>. Si no, escribe el mensaje en <paramref name="error"/>.
    /// </summary>
    public static bool CanStart(bool consoleMode, Func<bool> isElevated, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(isElevated);
        ArgumentNullException.ThrowIfNull(error);

        if (!consoleMode || isElevated())
        {
            return true;
        }

        error.WriteLine(NotElevatedMessage);
        return false;
    }

    /// <summary>
    /// El token del proceso tiene el grupo Administradores habilitado. <c>IsInRole</c> usa <c>CheckTokenMembership</c>: un
    /// administrador sin elevar (grupo "solo denegar") no cuenta.
    /// </summary>
    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }
}
