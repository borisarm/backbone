namespace Backbone.Service.Os;

/// <summary>Resultado de comprobar la carpeta de datos.</summary>
internal enum DataDirectoryCheckResult
{
    /// <summary>Ya cumplía RNF-07.</summary>
    Secure = 0,

    /// <summary>No cumplía y se ha corregido (creada, propietario o DACL).</summary>
    Corrected = 1,
}

/// <summary>
/// HT-03 I-1: antes de leer ninguna configuración, comprueba <c>%ProgramData%\Backbone</c> y la corrige o se niega a usarla.
/// </summary>
internal interface IDataDirectoryGuard
{
    /// <summary>Comprueba y, si se puede, corrige la carpeta de datos.</summary>
    /// <param name="root">Carpeta raíz de datos.</param>
    /// <returns>Si estaba bien o se ha corregido.</returns>
    /// <exception cref="DataDirectoryInsecureException">No es segura y no se puede corregir: el servicio no debe arrancar.</exception>
    DataDirectoryCheckResult EnsureSecure(string root);
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
