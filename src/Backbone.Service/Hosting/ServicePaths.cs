namespace Backbone.Service.Hosting;

/// <summary>Rutas de datos del servicio (§8.3).</summary>
internal sealed class ServicePaths(string root)
{
    /// <summary><c>%ProgramData%\Backbone</c>, resuelto con la carpeta conocida (no con variables de entorno).</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Backbone");

    public string Root { get; } = Path.GetFullPath(root);

    public string ConfigDirectory => Path.Combine(Root, "config");

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary><c>config\jobs.json</c>: definición de los trabajos (§8.2, HU-07).</summary>
    public string JobsFile => Path.Combine(ConfigDirectory, "jobs.json");

    /// <summary><c>credentials\</c>: un archivo cifrado por recurso (§8.3, HU-04).</summary>
    public string CredentialsDirectory => Path.Combine(Root, "credentials");
}
