namespace Backbone.App.Tray;

/// <summary>
/// Dibuja los iconos de la bandeja en memoria (BGRA, sin alfa premultiplicado, filas de arriba abajo), de modo que no hacen falta
/// archivos de imagen y se adaptan a cualquier tamaño/DPI. El estado no depende solo del color: cada icono lleva un símbolo
/// (marca, exclamación, aspa o puntos giratorios). Los colores son fijos a propósito: el icono de la bandeja no tiene temas.
/// </summary>
internal static class TrayIconRenderer
{
    /// <summary>Fotogramas de la animación de "copiando".</summary>
    public const int CopyingFrames = 8;

    private const int Supersample = 3;
    private const double HalfStroke = 0.055;

    /// <summary>Color de relleno (B, G, R) por icono.</summary>
    public static (byte B, byte G, byte R) FillColor(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Ok => (0x3E, 0x8E, 0x1E),
        TrayIconKind.Warning => (0x00, 0xB6, 0xF2),
        TrayIconKind.Error => (0x38, 0x34, 0xD1),
        _ => (0xBD, 0x6C, 0x0F),
    };

    /// <summary>Genera la imagen <paramref name="size"/> × <paramref name="size"/> del icono y fotograma indicados.</summary>
    public static byte[] Render(TrayIconKind kind, int frame, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        var fill = FillColor(kind);
        byte glyph = kind == TrayIconKind.Warning ? (byte)0x20 : (byte)0xFF;
        var segments = Segments(kind);
        var dots = kind == TrayIconKind.Copying ? Dots(frame) : [];
        var pixels = new byte[size * size * 4];
        const double samples = Supersample * Supersample;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double inDisc = 0, inGlyph = 0;
                for (var sy = 0; sy < Supersample; sy++)
                {
                    for (var sx = 0; sx < Supersample; sx++)
                    {
                        var u = (x + ((sx + 0.5) / Supersample)) / size;
                        var v = (y + ((sy + 0.5) / Supersample)) / size;
                        if (Math.Sqrt(((u - 0.5) * (u - 0.5)) + ((v - 0.5) * (v - 0.5))) <= 0.47)
                        {
                            inDisc++;
                            if (IsGlyph(u, v, segments, dots))
                            {
                                inGlyph++;
                            }
                        }
                    }
                }

                if (inDisc == 0)
                {
                    continue;
                }

                var g = inGlyph / inDisc;
                var i = ((y * size) + x) * 4;
                pixels[i] = (byte)Math.Round((fill.B * (1 - g)) + (glyph * g));
                pixels[i + 1] = (byte)Math.Round((fill.G * (1 - g)) + (glyph * g));
                pixels[i + 2] = (byte)Math.Round((fill.R * (1 - g)) + (glyph * g));
                pixels[i + 3] = (byte)Math.Round(inDisc / samples * 255);
            }
        }

        return pixels;
    }

    private static bool IsGlyph(double u, double v, (double X1, double Y1, double X2, double Y2)[] segments, (double X, double Y)[] dots)
    {
        foreach (var s in segments)
        {
            if (DistanceToSegment(u, v, s.X1, s.Y1, s.X2, s.Y2) <= HalfStroke)
            {
                return true;
            }
        }

        foreach (var d in dots)
        {
            if (Math.Sqrt(((u - d.X) * (u - d.X)) + ((v - d.Y) * (v - d.Y))) <= 0.085)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        var len2 = (dx * dx) + (dy * dy);
        var t = len2 == 0 ? 0 : Math.Clamp((((px - x1) * dx) + ((py - y1) * dy)) / len2, 0, 1);
        double cx = x1 + (t * dx), cy = y1 + (t * dy);
        return Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
    }

    private static (double X1, double Y1, double X2, double Y2)[] Segments(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Ok => [(0.27, 0.52, 0.43, 0.68), (0.43, 0.68, 0.73, 0.33)],
        TrayIconKind.Warning => [(0.5, 0.23, 0.5, 0.57), (0.5, 0.74, 0.5, 0.745)],
        TrayIconKind.Error => [(0.33, 0.33, 0.67, 0.67), (0.67, 0.33, 0.33, 0.67)],
        _ => [],
    };

    private static (double X, double Y)[] Dots(int frame)
    {
        // Tres puntos a 120°: girar 120° en total deja la imagen igual, así que el bucle de fotogramas es continuo.
        var offset = (frame % CopyingFrames) * (2 * Math.PI / 3 / CopyingFrames);
        var result = new (double X, double Y)[3];
        for (var k = 0; k < 3; k++)
        {
            var a = offset + (k * 2 * Math.PI / 3);
            result[k] = (0.5 + (0.24 * Math.Cos(a)), 0.5 + (0.24 * Math.Sin(a)));
        }

        return result;
    }
}
