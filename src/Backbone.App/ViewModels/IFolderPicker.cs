namespace Backbone.App.ViewModels;

/// <summary>Selector de carpetas (en la app, <c>FolderPicker</c> de WinUI; en las pruebas, un doble).</summary>
internal interface IFolderPicker
{
    /// <summary>Pide una carpeta al usuario.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>La ruta elegida, o <see langword="null"/> si cancela.</returns>
    Task<string?> PickFolderAsync(CancellationToken ct = default);
}
