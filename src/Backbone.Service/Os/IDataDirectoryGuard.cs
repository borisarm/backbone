namespace Backbone.Service.Os;

/// <summary>Resultado de comprobar la carpeta de datos.</summary>
internal enum DataDirectoryCheckResult
{
    /// <summary>Ya cumplía RNF-07.</summary>
    Secure = 0,

    /// <summary>No cumplía y se ha corregido en el sitio (creada o DACL).</summary>
    Corrected = 1,

    /// <summary>
    /// Su propietario no era de confianza: se ha apartado como <c>Backbone.untrusted-&lt;guid&gt;</c> (sin borrar nada) y se
    /// ha creado una nueva con la seguridad de RNF-07.
    /// </summary>
    Replaced = 2,
}

/// <summary>Resultado de <see cref="IDataDirectoryGuard.EnsureSecure"/>.</summary>
/// <param name="Result">Si estaba bien, se ha corregido o se ha apartado y sustituido.</param>
/// <param name="MovedAsidePaths">
/// Con <see cref="DataDirectoryCheckResult.Replaced"/>, rutas completas con que se ha apartado la raíz anterior
/// (normalmente una; más si alguien la volvió a crear entre intentos). Vacía en el resto de casos.
/// </param>
internal sealed record DataDirectoryCheck(DataDirectoryCheckResult Result, IReadOnlyList<string> MovedAsidePaths)
{
    public static DataDirectoryCheck Secure { get; } = new(DataDirectoryCheckResult.Secure, []);
}

/// <summary>
/// HT-03 I-1: antes de leer ninguna configuración, comprueba <c>%ProgramData%\Backbone</c> y la corrige o se niega a usarla.
/// </summary>
internal interface IDataDirectoryGuard
{
    /// <summary>Comprueba y, si se puede, corrige la carpeta de datos.</summary>
    /// <param name="root">Carpeta raíz de datos.</param>
    /// <returns>Si estaba bien, se ha corregido o se ha apartado (con la ruta apartada).</returns>
    /// <exception cref="DataDirectoryInsecureException">No es segura y no se puede corregir: el servicio no debe arrancar.</exception>
    DataDirectoryCheck EnsureSecure(string root);
}

/// <summary>La carpeta de datos no es segura y no se puede corregir sin riesgo.</summary>
internal sealed class DataDirectoryInsecureException : Exception
{
    public DataDirectoryInsecureException()
    {
    }

    public DataDirectoryInsecureException(string message)
        : base(message)
    {
    }

    public DataDirectoryInsecureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
