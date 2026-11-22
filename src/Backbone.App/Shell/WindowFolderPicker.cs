using Backbone.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;

namespace Backbone.App.Shell;

/// <summary>
/// Selector de carpetas de Windows App SDK (<c>Microsoft.Windows.Storage.Pickers.FolderPicker</c>, 1.8): recibe el
/// <c>WindowId</c> de la ventana y devuelve la ruta directamente. No funciona si la app se ejecuta elevada.
/// </summary>
internal sealed class WindowFolderPicker(Window window) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window));
        var picker = new FolderPicker(windowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
