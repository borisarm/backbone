using System.Globalization;
using Backbone.App.Ipc;

namespace Backbone.App.Tray;

/// <summary>
/// Texto emergente del icono: estado y última copia correcta (RF-50, HU-18). Las cadenas vienen de los recursos
/// mediante <c>getString</c> (claves <c>Tray_*</c> de <c>Resources.resw</c>).
/// </summary>
internal sealed class TrayTooltipFormatter(Func<string, string> getString, TimeProvider timeProvider, CultureInfo? culture = null)
{
    /// <summary>Máximo de caracteres de <c>NOTIFYICONDATA.szTip</c> sin el terminador.</summary>
    public const int MaxLength = 127;

    private readonly CultureInfo _culture = culture ?? CultureInfo.CurrentUICulture;

    /// <summary>Clave del texto de estado (sin el prefijo de pausa).</summary>
    public static string StateKey(TrayPresentation p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Connection switch
        {
            ConnectionState.Connecting => "Tray_State_Connecting",
            ConnectionState.Reconnecting => "Tray_State_Reconnecting",
            ConnectionState.ServiceStopped => "Tray_State_ServiceStopped",
            ConnectionState.Unauthorized => "Tray_State_Unauthorized",
            ConnectionState.IncompatibleContract => "Tray_State_IncompatibleContract",
            _ => p.Kind switch
            {
                TrayIconKind.Ok => "Tray_State_Ok",
                TrayIconKind.Warning => "Tray_State_Warning",
                TrayIconKind.Error => "Tray_State_Error",
                _ => "Tray_State_Copying",
            },
        };
    }

    /// <summary>Texto emergente: primera línea el estado, segunda la última copia correcta.</summary>
    public string Format(TrayPresentation p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var state = getString(StateKey(p));
        if (p.Paused)
        {
            state = string.Format(_culture, getString("Tray_State_PausedFormat"), state);
        }

        var text = string.Format(_culture, getString("Tray_TooltipFormat"), state, FormatLastSuccess(p.LastSuccessUtc));
        return text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength - 1), "…");
    }

    private string FormatLastSuccess(DateTimeOffset? utc)
    {
        if (utc is null)
        {
            return getString("Tray_LastSuccess_None");
        }

        var local = TimeZoneInfo.ConvertTime(utc.Value, timeProvider.LocalTimeZone);
        var today = timeProvider.GetLocalNow().Date;
        return local.Date == today
            ? string.Format(_culture, getString("Tray_LastSuccess_TodayFormat"), local.ToString("t", _culture))
            : string.Format(_culture, getString("Tray_LastSuccess_DateFormat"), local.ToString("g", _culture));
    }
}
