namespace Backbone.Core.Tests.Support;

/// <summary>Carpeta temporal única que se borra al liberar.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "backbone-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Algún archivo sigue abierto; lo limpiará el sistema.
        }
    }
}
