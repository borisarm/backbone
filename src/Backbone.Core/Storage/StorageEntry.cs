namespace Backbone.Core.Storage;

/// <summary>Elemento de una carpeta del repositorio.</summary>
/// <param name="Name">Nombre (sin carpeta).</param>
/// <param name="IsDirectory">Si es una carpeta.</param>
/// <param name="Length">Tamaño en bytes; 0 para carpetas.</param>
/// <param name="LastWriteUtc">Última modificación según el almacenamiento, en UTC.</param>
public sealed record StorageEntry(string Name, bool IsDirectory, long Length, DateTime LastWriteUtc);
