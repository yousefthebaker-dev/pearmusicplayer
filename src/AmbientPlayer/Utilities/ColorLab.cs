using System.Windows.Media;

namespace AmbientPlayer.Utilities;

/// <summary>A colour in CIE L*a*b* space (D65 white point), plus the sRGB it came from.</summary>
public readonly record struct LabColor(double L, double A, double B)
{
    public static double DeltaE(LabColor x, LabColor y)
    {
        var dl = x.L - y.L;
        var da = x.A - y.A;
        var db = x.B - y.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    public static LabColor Lerp(LabColor x, LabColor y, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return new LabColor(
            x.L + (y.L - x.L) * t,
            x.A + (y.A - x.A) * t,
            x.B + (y.B - x.B) * t);
    }
}

/// <summary>
/// sRGB &lt;-&gt; CIE L*a*b* conversion and simple colour operations used by
/// <see cref="Services.PaletteService"/>: clustering in a perceptual space
/// makes "reject near-duplicate colours" and "spread by luminance" behave
/// sanely, which plain RGB distance does not.
/// </summary>
public static class ColorLab
{
    // D65 reference white, 2 degree observer.
    private const double RefX = 95.047;
    private const double RefY = 100.000;
    private const double RefZ = 108.883;

    public static LabColor FromRgb(byte r, byte g, byte b)
    {
        var (x, y, z) = RgbToXyz(r, g, b);
        return XyzToLab(x, y, z);
    }

    public static Color ToRgb(LabColor lab)
    {
        var (x, y, z) = LabToXyz(lab);
        var (r, g, b) = XyzToRgb(x, y, z);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>HSL-style saturation in [0,1], used to decide whether a cover is effectively monochrome.</summary>
    public static double Saturation(byte r, byte g, byte b)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var lightness = (max + min) / 2.0;
        if (max == min) return 0.0; // achromatic
        var delta = max - min;
        return lightness > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
    }

    /// <summary>Relative luminance in [0,1] (Rec. 709 coefficients on linearised channels).</summary>
    public static double Luminance(byte r, byte g, byte b)
    {
        double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    /// <summary>
    /// Desaturates slightly for using an album colour behind white text.
    /// Lightness is left alone here deliberately - PaletteService's contrast
    /// stretch already sets it to the right value, and re-darkening it here
    /// would undo that stretch and flatten the contrast right back out.
    /// </summary>
    public static LabColor ForBackground(LabColor lab, double chromaScale = 0.85)
    {
        return new LabColor(lab.L, lab.A * chromaScale, lab.B * chromaScale);
    }

    private static (double x, double y, double z) RgbToXyz(byte r, byte g, byte b)
    {
        double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var rl = Lin(r);
        var gl = Lin(g);
        var bl = Lin(b);

        var x = (rl * 0.4124 + gl * 0.3576 + bl * 0.1805) * 100.0;
        var y = (rl * 0.2126 + gl * 0.7152 + bl * 0.0722) * 100.0;
        var z = (rl * 0.0193 + gl * 0.1192 + bl * 0.9505) * 100.0;
        return (x, y, z);
    }

    private static LabColor XyzToLab(double x, double y, double z)
    {
        double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);

        var fx = F(x / RefX);
        var fy = F(y / RefY);
        var fz = F(z / RefZ);

        var l = (116.0 * fy) - 16.0;
        var a = 500.0 * (fx - fy);
        var b = 200.0 * (fy - fz);
        return new LabColor(l, a, b);
    }

    private static (double x, double y, double z) LabToXyz(LabColor lab)
    {
        var fy = (lab.L + 16.0) / 116.0;
        var fx = fy + lab.A / 500.0;
        var fz = fy - lab.B / 200.0;

        double FInv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116.0) / 7.787;

        var x = RefX * FInv(fx);
        var y = RefY * FInv(fy);
        var z = RefZ * FInv(fz);
        return (x, y, z);
    }

    private static (byte r, byte g, byte b) XyzToRgb(double x, double y, double z)
    {
        x /= 100.0;
        y /= 100.0;
        z /= 100.0;

        var rl = x * 3.2406 + y * -1.5372 + z * -0.4986;
        var gl = x * -0.9689 + y * 1.8758 + z * 0.0415;
        var bl = x * 0.0557 + y * -0.2040 + z * 1.0570;

        byte Gamma(double c)
        {
            c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(c * 255.0), 0.0, 255.0);
        }

        return (Gamma(rl), Gamma(gl), Gamma(bl));
    }
}
