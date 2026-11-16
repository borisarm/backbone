using Backbone.App.Tray;

namespace Backbone.App.Tests.Tray;

/// <summary>HU-18 criterio 1: los cuatro iconos son distinguibles (color y forma) y el de copiar está animado.</summary>
[Trait("Story", "HU-18")]
public sealed class TrayIconRendererTests
{
    private const int Size = 32;

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int x, int y)
    {
        var i = ((y * Size) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }

    [Theory]
    [InlineData(TrayIconKind.Ok)]
    [InlineData(TrayIconKind.Warning)]
    [InlineData(TrayIconKind.Error)]
    [InlineData(TrayIconKind.Copying)]
    public void Each_icon_is_a_filled_disc_with_transparent_corners_and_the_state_colour(TrayIconKind kind)
    {
        var image = Render(kind);

        Assert.Equal(Size * Size * 4, image.Length);
        Assert.Equal(0, Pixel(image, 0, 0).A);
        Assert.Equal(0, Pixel(image, Size - 1, Size - 1).A);
        // Un punto del disco fuera del símbolo (borde izquierdo del círculo) tiene el color de relleno, opaco.
        var edge = Pixel(image, 4, Size / 2);
        var fill = TrayIconRenderer.FillColor(kind);
        Assert.Equal(255, edge.A);
        Assert.Equal((fill.B, fill.G, fill.R), (edge.B, edge.G, edge.R));
    }

    [Fact]
    public void The_four_fill_colours_are_different()
    {
        var colours = Enum.GetValues<TrayIconKind>().Select(TrayIconRenderer.FillColor).Distinct().Count();

        Assert.Equal(4, colours);
    }

    [Fact]
    public void The_four_images_are_different_so_state_does_not_depend_on_colour_alone()
    {
        var images = Enum.GetValues<TrayIconKind>().Select(k => Convert.ToBase64String(Render(k))).Distinct().Count();

        Assert.Equal(4, images);
    }

    [Fact]
    public void The_copying_icon_changes_between_frames_and_loops()
    {
        var frames = Enumerable.Range(0, TrayIconRenderer.CopyingFrames).Select(f => Convert.ToBase64String(Render(TrayIconKind.Copying, f))).ToList();

        Assert.Equal(TrayIconRenderer.CopyingFrames, frames.Distinct().Count());
        Assert.Equal(frames[0], Convert.ToBase64String(Render(TrayIconKind.Copying, TrayIconRenderer.CopyingFrames)));
    }

    [Fact]
    public void A_static_icon_ignores_the_frame()
    {
        Assert.Equal(Render(TrayIconKind.Ok, 0), Render(TrayIconKind.Ok, 3));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Renders_at_every_dpi_size(int size)
    {
        var image = TrayIconRenderer.Render(TrayIconKind.Error, 0, size);

        Assert.Equal(size * size * 4, image.Length);
        Assert.Contains(image.Where((_, i) => i % 4 == 3), a => a == 255);
    }

    private static byte[] Render(TrayIconKind kind, int frame = 0) => TrayIconRenderer.Render(kind, frame, Size);
}
