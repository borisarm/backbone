namespace Backbone.Contracts;

/// <summary>
/// Versión semántica del contrato IPC (<c>Mayor.Menor</c>, §7.1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Un cambio incompatible hacia atrás (quitar o renombrar un método, propiedad o valor de enumerado,
/// cambiar un tipo o el significado de un campo) sube la versión <b>mayor</b> y el nombre del pipe
/// (<see cref="BackboneIpc.PipeName"/>).</item>
/// <item>Añadir métodos, propiedades opcionales o valores de enumerado sube la <b>menor</b>.</item>
/// </list>
/// La app compara la versión que devuelve <see cref="IBackboneService.GetServiceInfoAsync"/> con
/// <see cref="IsCompatibleWith"/> y muestra un aviso si la versión mayor no coincide (§9.3).
/// </remarks>
public static class ContractVersion
{
    /// <summary>Versión mayor actual.</summary>
    public const int Major = 1;

    /// <summary>Versión menor actual.</summary>
    public const int Minor = 2;

    /// <summary>Versión actual en texto, <c>"Mayor.Menor"</c>.</summary>
    public const string Value = "1.2";

    /// <summary>
    /// Indica si una versión de contrato recibida del otro extremo es compatible con esta (misma versión mayor).
    /// </summary>
    /// <param name="version">Versión en formato <c>Mayor.Menor</c>.</param>
    /// <returns><see langword="true"/> si se puede interpretar y su versión mayor coincide con <see cref="Major"/>.</returns>
    public static bool IsCompatibleWith(string? version) =>
        TryParse(version, out var major, out _) && major == Major;

    /// <summary>Interpreta una versión con formato <c>Mayor.Menor</c> (dos enteros no negativos).</summary>
    /// <param name="version">Texto que se interpreta.</param>
    /// <param name="major">Versión mayor, si se ha podido interpretar.</param>
    /// <param name="minor">Versión menor, si se ha podido interpretar.</param>
    /// <returns><see langword="true"/> si el formato es válido.</returns>
    public static bool TryParse(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        var parts = version.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minor);
    }
}
