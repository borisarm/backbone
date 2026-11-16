using System.Globalization;
using Backbone.App.Ipc;
using Backbone.App.Tray;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.App.Tests.Tray;

/// <summary>HU-18 criterio 1: el texto emergente resume el estado y la última copia correcta.</summary>
[Trait("Story", "HU-18")]
public sealed class TrayTooltipFormatterTests
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static readonly Dictionary<string, string> Strings = new()
    {
        ["Tray_TooltipFormat"] = "Backbone: {0}\n{1}",
        ["Tray_State_Ok"] = "al día",
        ["Tray_State_Copying"] = "copiando",
        ["Tray_State_ServiceStopped"] = "servicio detenido",
        ["Tray_State_PausedFormat"] = "{0} (en pausa)",
        ["Tray_LastSuccess_None"] = "Sin copia correcta",
        ["Tray_LastSuccess_TodayFormat"] = "Última: hoy {0}",
        ["Tray_LastSuccess_DateFormat"] = "Última: {0}",
    };

    private static TrayTooltipFormatter Create(FakeTimeProvider time) => new(key => Strings[key], time, Es);

    private static FakeTimeProvider TimeAt(DateTimeOffset now)
    {
        var time = new FakeTimeProvider(now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return time;
    }

    [Fact]
    public void Shows_state_and_todays_last_success_time()
    {
        var time = TimeAt(new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero));
        var p = new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero), false, 1);

        Assert.Equal("Backbone: al día\nÚltima: hoy 9:30", Create(time).Format(p));
    }

    [Fact]
    public void Shows_the_date_when_the_last_success_was_not_today()
    {
        var time = TimeAt(new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero));
        var p = new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero), false, 1);

        var text = Create(time).Format(p);

        Assert.Contains("13/11/2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hoy", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Uses_the_local_time_zone_to_decide_what_today_is()
    {
        // 23:30 UTC del día 14 es ya el día 15 a las 00:30 en UTC+1.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1"));
        var p = new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, new DateTimeOffset(2026, 11, 14, 23, 30, 0, TimeSpan.Zero), false, 1);

        Assert.Contains("hoy 0:30", Create(time).Format(p), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_any_success_says_so()
    {
        var p = new TrayPresentation(TrayIconKind.Copying, ConnectionState.Connected, null, false, 0);

        Assert.Equal("Backbone: copiando\nSin copia correcta", Create(TimeAt(DateTimeOffset.UnixEpoch)).Format(p));
    }

    [Fact]
    public void Connection_state_overrides_the_kind_text_and_pause_is_appended()
    {
        var stopped = new TrayPresentation(TrayIconKind.Error, ConnectionState.ServiceStopped, null, false, 0);
        var paused = new TrayPresentation(TrayIconKind.Ok, ConnectionState.Connected, null, true, 1);
        var formatter = Create(TimeAt(DateTimeOffset.UnixEpoch));

        Assert.StartsWith("Backbone: servicio detenido", formatter.Format(stopped), StringComparison.Ordinal);
        Assert.StartsWith("Backbone: al día (en pausa)", formatter.Format(paused), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_never_exceeds_the_notify_icon_limit()
    {
        var formatter = new TrayTooltipFormatter(key => new string('x', 200), TimeAt(DateTimeOffset.UnixEpoch), Es);

        var text = formatter.Format(TrayPresentation.Initial);

        Assert.Equal(TrayTooltipFormatter.MaxLength, text.Length);
    }

    [Fact]
    public void Every_state_key_exists_in_both_resource_files()
    {
        var keys = Enum.GetValues<ConnectionState>()
            .SelectMany(c => Enum.GetValues<TrayIconKind>().Select(k => TrayTooltipFormatter.StateKey(new(k, c, null, false, 0))))
            .Distinct()
            .Concat(["Tray_TooltipFormat", "Tray_State_PausedFormat", "Tray_LastSuccess_None", "Tray_LastSuccess_TodayFormat", "Tray_LastSuccess_DateFormat",
                "Tray_Menu_RunAllNow", "Tray_Menu_Pause", "Tray_Menu_Open", "Tray_Menu_Exit", "App_Title"])
            .ToList();

        foreach (var culture in new[] { "es-ES", "en-US" })
        {
            var names = ResourceNames(culture);
            foreach (var key in keys)
            {
                Assert.Contains(key, names);
            }
        }
    }

    private static HashSet<string> ResourceNames(string culture)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Backbone.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "src", "Backbone.App", "Strings", culture, "Resources.resw");
        return System.Xml.Linq.XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
    }
}
