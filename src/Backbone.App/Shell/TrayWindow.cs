using Backbone.App.Ipc;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Backbone.App.Tray;

namespace Backbone.App.Shell;

/// <summary>
/// Icono del área de notificación (<c>Shell_NotifyIconW</c>, versión 4) con su ventana oculta de mensajes y el menú contextual
/// nativo. La ventana es de nivel superior (no <c>HWND_MESSAGE</c>) porque solo esas reciben <c>TaskbarCreated</c>, que sirve para
/// volver a crear el icono si el Explorador se reinicia. Los menús nativos son accesibles con Narrador y teclado (tecla Menú,
/// Mayús+F10, Intro en el icono). Solo se usa desde el hilo de UI. Hay una única instancia por proceso.
/// </summary>
internal sealed unsafe class TrayWindow : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WmApp + 1;
    private const string ClassName = "Backbone.TrayWindow";
    private const uint IconId = 1;

    private static TrayWindow? s_current;
    private static uint s_taskbarCreated;

    private readonly nint _hwnd;
    private bool _added;
    private bool _disposed;

    public TrayWindow()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray window per process.");
        }

        s_taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var instance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WndClassEx
        {
            CbSize = (uint)sizeof(NativeMethods.WndClassEx),
            LpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
            HInstance = instance,
            LpszClassName = Marshal.StringToHGlobalUni(ClassName), // vive todo el proceso: la clase no se desregistra.
        };

        // Si la clase ya existe (no debería) CreateWindowEx la reutiliza.
        NativeMethods.RegisterClassEx(ref wc);
        s_current = this;
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExToolWindow, ClassName, ClassName, NativeMethods.WsPopup, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_hwnd == 0)
        {
            s_current = null;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Clic o Intro sobre el icono (<c>NIN_SELECT</c> / <c>NIN_KEYSELECT</c>).</summary>
    public event Action? Selected;

    /// <summary>Menú contextual pedido (botón derecho, tecla Menú o Mayús+F10), con las coordenadas de pantalla.</summary>
    public event Action<int, int>? ContextMenuRequested;

    /// <summary>El Explorador se reinició: hay que volver a llamar a <see cref="SetIcon"/> con el estado actual.</summary>
    public event Action? TaskbarRecreated;

    /// <summary>Manejador de la ventana oculta; propietaria de los menús.</summary>
    public nint Handle => _hwnd;

    /// <summary>Crea el icono si hace falta y actualiza imagen y texto emergente (máximo 127 caracteres).</summary>
    public bool SetIcon(nint icon, string tip)
    {
        var data = NewData(icon, tip);
        if (!_added)
        {
            if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data))
            {
                return false;
            }

            _added = true;
            data.UVersion = NativeMethods.NotifyIconVersion4;
            NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref data);
            return true;
        }

        return NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }

    /// <summary>Muestra el menú nativo en las coordenadas dadas y devuelve la orden elegida (<see cref="TrayCommand.None"/> si se descarta).</summary>
    public TrayCommand ShowMenu(IReadOnlyList<(TrayCommand Command, string Text, bool Enabled)> items, int x, int y)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            return TrayCommand.None;
        }

        try
        {
            foreach (var (command, text, enabled) in items)
            {
                if (command == TrayCommand.Exit)
                {
                    NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
                }

                NativeMethods.AppendMenu(
                    menu,
                    NativeMethods.MfString | (enabled ? 0 : NativeMethods.MfGrayed),
                    (nuint)command,
                    text);
            }

            // Necesario para que el menú se cierre al hacer clic fuera (documentado en TrackPopupMenu).
            NativeMethods.SetForegroundWindow(_hwnd);
            var chosen = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton | NativeMethods.TpmBottomAlign,
                x,
                y,
                0,
                _hwnd,
                0);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WmNull, 0, 0);
            return Enum.IsDefined((TrayCommand)chosen) ? (TrayCommand)chosen : TrayCommand.None;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = NewData(0, string.Empty);
            NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            _added = false;
        }

        NativeMethods.DestroyWindow(_hwnd);
        s_current = null;
    }

    private NativeMethods.NotifyIconData NewData(nint icon, string tip)
    {
        var data = new NativeMethods.NotifyIconData
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconData),
            HWnd = _hwnd,
            UId = IconId,
            UFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
            UCallbackMessage = CallbackMessage,
            HIcon = icon,
        };
        var max = Math.Min(tip.Length, 127);
        for (var i = 0; i < max; i++)
        {
            data.SzTip[i] = tip[i];
        }

        return data;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        var self = s_current;
        if (self is not null)
        {
            if (message == CallbackMessage)
            {
                // Versión 4: LOWORD(lParam) = evento, wParam = coordenadas (GET_X/Y_LPARAM).
                var evt = (uint)((ulong)lParam & 0xFFFF);
                if (evt == NativeMethods.WmContextMenu)
                {
                    var x = (short)((ulong)wParam & 0xFFFF);
                    var y = (short)(((ulong)wParam >> 16) & 0xFFFF);
                    self.ContextMenuRequested?.Invoke(x, y);
                }
                else if (evt is NativeMethods.NinSelect or NativeMethods.NinKeySelect)
                {
                    self.Selected?.Invoke();
                }

                return 0;
            }

            if (message == s_taskbarCreated && s_taskbarCreated != 0)
            {
                self._added = false;
                self.TaskbarRecreated?.Invoke();
                return 0;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
