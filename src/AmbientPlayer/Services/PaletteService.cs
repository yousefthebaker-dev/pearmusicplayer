using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmbientPlayer.Utilities;

namespace AmbientPlayer.Services;

/// <summary>
/// Extracts 5-6 background-ready colours from album art for the mesh
/// gradient. Median-cut clustering in CIE L*a*b* space, weighted toward
/// saturated pixels but degrading gracefully on monochrome covers.
/// See AMBIENT_PLAYER_SPEC.md section 6.
/// </summary>
public sealed class PaletteService
{
    private const int DownscaleSize = 32;
    private const int TargetColorCount = 6;
    private const int MinColorCount = 3;
    private const double MinClusterDeltaE = 10.0;
    private const double MonochromeSaturationCeiling = 0.12;

    /// <summary>Decodes and downscales the image, then clusters it into background-ready colours.</summary>
    public IReadOnlyList<Color> ExtractPalette(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0) return DefaultPalette();

        try
        {
            var pixels = DecodeAndDownscale(imageBytes, DownscaleSize);
            return pixels.Count == 0 ? DefaultPalette() : BuildPalette(pixels);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PaletteService] extraction failed, using default palette: {ex.Message}");
            return DefaultPalette();
        }
    }

    /// <summary>Neutral colours used when there is no art at all to derive a palette from.</summary>
    public static IReadOnlyList<Color> DefaultPalette() =>
    [
        Color.FromRgb(0x1c, 0x1e, 0x26),
        Color.FromRgb(0x2a, 0x2d, 0x39),
        Color.FromRgb(0x14, 0x16, 0x1c),
        Color.FromRgb(0x30, 0x33, 0x40),
        Color.FromRgb(0x20, 0x22, 0x2c),
    ];

    // ------------------------------------------------------------------
    // Decoding
    // ------------------------------------------------------------------

    private static List<(byte R, byte G, byte B)> DecodeAndDownscale(byte[] imageBytes, int size)
    {
        using var stream = new MemoryStream(imageBytes);
        // The 125px SMTC fallback thumbnail is plenty for a 32x32 palette
        // source, so palette quality never depends on the iTunes lookup
        // succeeding.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var scaleX = (double)size / frame.PixelWidth;
        var scaleY = (double)size / frame.PixelHeight;
        var transformed = new TransformedBitmap(frame, new ScaleTransform(scaleX, scaleY));
        var converted = new FormatConvertedBitmap(transformed, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        var pixels = new List<(byte, byte, byte)>(width * height);
        for (var i = 0; i < buffer.Length; i += 4)
        {
            var a = buffer[i + 3];
            if (a < 16) continue; // skip near-transparent padding pixels
            var b = buffer[i];
            var g = buffer[i + 1];
            var r = buffer[i + 2];
            pixels.Add((r, g, b));
        }
        return pixels;
    }

    // ------------------------------------------------------------------
    // Clustering
    // ------------------------------------------------------------------

    private static List<Color> BuildPalette(List<(byte R, byte G, byte B)> pixels)
    {
        var maxSaturation = 0.0;
        foreach (var (r, g, b) in pixels)
        {
            maxSaturation = Math.Max(maxSaturation, ColorLab.Saturation(r, g, b));
        }

        // Monochrome covers are common in this library (the reference
        // screenshot is a black-and-white cover). Median-cut on Lab already
        // tends to split along L when a*/b* barely vary, so simply not
        // boosting saturated pixels is enough to make this "spread by
        // luminance instead" for a near-grey cover, with no special case.
        var isMonochrome = maxSaturation < MonochromeSaturationCeiling;

        var samples = new List<LabColor>(pixels.Count * 2);
        foreach (var (r, g, b) in pixels)
        {
            var lab = ColorLab.FromRgb(r, g, b);
            samples.Add(lab);

            if (!isMonochrome)
            {
                // Weight toward saturated colours by oversampling them, so
                // cluster means gravitate towards vivid pixels instead of a
                // muddy in-between average.
                var saturation = ColorLab.Saturation(r, g, b);
                var extraCopies = (int)Math.Round(saturation * 3.0);
                for (var i = 0; i < extraCopies; i++) samples.Add(lab);
            }
        }

        var buckets = MedianCut(samples, TargetColorCount);

        var candidates = buckets
            .Select(bucket => (Color: Average(bucket), Population: bucket.Count))
            .OrderByDescending(c => c.Population)
            .ToList();

        var accepted = new List<LabColor>();
        foreach (var candidate in candidates)
        {
            // Reject clusters within a small Lab distance of one another -
            // otherwise a near-monochrome cover produces a palette that
            // looks diverse on paper but collapses to a flat wash on screen.
            if (accepted.TrueForAll(existing => LabColor.DeltaE(existing, candidate.Color) >= MinClusterDeltaE))
            {
                accepted.Add(candidate.Color);
            }
        }

        if (accepted.Count == 0)
        {
            accepted.Add(samples.Count > 0 ? Average(samples) : ColorLab.FromRgb(40, 40, 40));
        }

        // Guarantee enough blobs for the mesh gradient to animate even on a
        // genuinely flat/solid-colour cover.
        var seed = accepted[0];
        var attempt = 0;
        while (accepted.Count < MinColorCount)
        {
            attempt++;
            var direction = attempt % 2 == 0 ? 1 : -1;
            var magnitude = 12.0 * ((attempt + 1) / 2);
            var jittered = new LabColor(Math.Clamp(seed.L + direction * magnitude, 4.0, 92.0), seed.A, seed.B);
            accepted.Add(jittered);
        }

        // Force real contrast regardless of how flat the source cover is -
        // darkening every colour by the same proportional formula preserves
        // a narrow source range as a narrow (and visually flat/low-contrast)
        // background range. Apple's own renderer clearly does something
        // equivalent: a warm, low-contrast sepia cover still produces a
        // background with a near-black region and a bright, saturated one.
        StretchLightness(accepted, floorL: 1.0, ceilL: 75.0);

        return accepted
            .Select(lab => ColorLab.ToRgb(ColorLab.ForBackground(lab)))
            .ToList();
    }

    /// <summary>
    /// Remaps lightness so the darkest accepted colour lands near
    /// <paramref name="floorL"/> and the lightest near <paramref name="ceilL"/>,
    /// preserving relative order. Falls back to an even spread by rank when
    /// the source colours are nearly identical in lightness (a genuinely
    /// flat/solid cover), since a near-zero range would otherwise amplify
    /// into noise rather than a clean stretch.
    /// </summary>
    private static void StretchLightness(List<LabColor> colors, double floorL, double ceilL)
    {
        if (colors.Count == 0) return;

        var minL = colors.Min(c => c.L);
        var maxL = colors.Max(c => c.L);
        var range = maxL - minL;

        if (range < 8.0)
        {
            var rankOrder = Enumerable.Range(0, colors.Count).OrderBy(i => colors[i].L).ToList();
            var step = colors.Count > 1 ? (ceilL - floorL) / (colors.Count - 1) : 0.0;
            for (var rank = 0; rank < rankOrder.Count; rank++)
            {
                var i = rankOrder[rank];
                colors[i] = new LabColor(floorL + step * rank, colors[i].A, colors[i].B);
            }
            return;
        }

        for (var i = 0; i < colors.Count; i++)
        {
            var t = (colors[i].L - minL) / range;
            colors[i] = new LabColor(floorL + t * (ceilL - floorL), colors[i].A, colors[i].B);
        }
    }

    private static List<List<LabColor>> MedianCut(List<LabColor> samples, int targetCount)
    {
        var buckets = new List<List<LabColor>> { samples };

        while (buckets.Count < targetCount)
        {
            var splitIndex = -1;
            var splitAxis = 0;
            var bestRange = 0.0;

            for (var i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].Count < 2) continue;
                var (axis, range) = LargestAxisRange(buckets[i]);
                if (range > bestRange)
                {
                    bestRange = range;
                    splitIndex = i;
                    splitAxis = axis;
                }
            }

            if (splitIndex < 0) break; // nothing left worth splitting

            var bucket = buckets[splitIndex];
            bucket.Sort((x, y) => AxisValue(x, splitAxis).CompareTo(AxisValue(y, splitAxis)));

            var mid = bucket.Count / 2;
            var lower = bucket.GetRange(0, mid);
            var upper = bucket.GetRange(mid, bucket.Count - mid);

            buckets[splitIndex] = lower;
            buckets.Add(upper);
        }

        return buckets;
    }

    private static (int Axis, double Range) LargestAxisRange(List<LabColor> bucket)
    {
        var minL = double.MaxValue; var maxL = double.MinValue;
        var minA = double.MaxValue; var maxA = double.MinValue;
        var minB = double.MaxValue; var maxB = double.MinValue;

        foreach (var c in bucket)
        {
            if (c.L < minL) minL = c.L;
            if (c.L > maxL) maxL = c.L;
            if (c.A < minA) minA = c.A;
            if (c.A > maxA) maxA = c.A;
            if (c.B < minB) minB = c.B;
            if (c.B > maxB) maxB = c.B;
        }

        var rangeL = maxL - minL;
        var rangeA = maxA - minA;
        var rangeB = maxB - minB;

        if (rangeL >= rangeA && rangeL >= rangeB) return (0, rangeL);
        return rangeA >= rangeB ? (1, rangeA) : (2, rangeB);
    }

    private static double AxisValue(LabColor c, int axis) => axis switch
    {
        0 => c.L,
        1 => c.A,
        _ => c.B,
    };

    private static LabColor Average(List<LabColor> samples)
    {
        double sumL = 0, sumA = 0, sumB = 0;
        foreach (var c in samples)
        {
            sumL += c.L;
            sumA += c.A;
            sumB += c.B;
        }
        var n = samples.Count;
        return new LabColor(sumL / n, sumA / n, sumB / n);
    }
}
