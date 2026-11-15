using Backbone.App.Ipc;
using System.Runtime.InteropServices;
using Backbone.App.Tray;

namespace Backbone.App.Shell;

/// <summary>Crea y guarda en caché los <c>HICON</c> de <see cref="TrayIconRenderer"/> al tamaño de icono pequeño del sistema.</summary>
internal sealed class TrayIconFactory : IDisposable
{
    private readonly Dictionary<(TrayIconKind Kind, int Frame), nint> _icons = [];
    private readonly int _size;

    public TrayIconFactory()
    {
        var dpi = NativeMethods.GetDpiForSystem();
        _size = Math.Max(16, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmIcon, dpi));
    }

    /// <summary>Icono para el estado y fotograma (el fotograma solo importa en <see cref="TrayIconKind.Copying"/>).</summary>
    public nint Get(TrayIconKind kind, int frame = 0)
    {
        var key = (Kind: kind, Frame: kind == TrayIconKind.Copying ? frame % TrayIconRenderer.CopyingFrames : 0);
        if (_icons.TryGetValue(key, out var icon))
        {
            return icon;
        }

        icon = Create(TrayIconRenderer.Render(key.Kind, key.Frame, _size));
        _icons[key] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            NativeMethods.DestroyIcon(icon);
        }

        _icons.Clear();
    }

    private nint Create(byte[] bgra)
    {
        var maskBytes = new byte[(((_size + 15) / 16) * 2) * _size]; // 1 bpp, filas alineadas a 16 bits; con alfa la máscara no se usa.
        var colorHandle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        var maskHandle = GCHandle.Alloc(maskBytes, GCHandleType.Pinned);
        try
        {
            var color = NativeMethods.CreateBitmap(_size, _size, 1, 32, colorHandle.AddrOfPinnedObject());
            var mask = NativeMethods.CreateBitmap(_size, _size, 1, 1, maskHandle.AddrOfPinnedObject());
            try
            {
                var info = new NativeMethods.IconInfo { FIcon = 1, HbmColor = color, HbmMask = mask };
                var icon = NativeMethods.CreateIconIndirect(ref info);
                return icon == 0 ? throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()) : icon;
            }
            finally
            {
                NativeMethods.DeleteObject(color);
                NativeMethods.DeleteObject(mask);
            }
        }
        finally
        {
            colorHandle.Free();
            maskHandle.Free();
        }
    }
}
