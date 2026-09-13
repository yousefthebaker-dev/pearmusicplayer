using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AmbientPlayer.Utilities;

namespace AmbientPlayer.Rendering;

/// <summary>
/// The animated mesh-gradient background: several soft radial "blobs" whose
/// centres drift on independent slow sine cycles, colour-transitioning
/// smoothly (in Lab space) whenever a new palette is set. Blob positions are
/// a pure function of wall-clock time since this control was created, so a
/// track change never resets the drift - continuity is the whole effect.
///
/// No shader: this is exactly the "layer several RadialGradientBrush
/// rectangles with animated Center points, over a base fill" approach
/// AMBIENT_PLAYER_SPEC.md section 7 calls out as genuinely sufficient for v1.
/// </summary>
public sealed class MeshGradientBackground : Grid
{
    private const int BlobCount = 6;
    private const double DriftRadius = 0.22; // normalised units - large, slow, visible drift
    private const double BlobRadius = 0.95; // large, heavily-overlapping blobs read as one shifting field rather than distinct pools
    private const double MinPeriodSeconds = 20.0;
    private const double MaxPeriodSeconds = 60.0;
    private const double PaletteTransitionSeconds = 2.0;
    private const int NoiseTileSize = 64;
    // Noise pixels are full-range grey (0-255) composited at this alpha, so
    // the effective per-pixel nudge averages out to roughly the spec's
    // ~1.5/255 offset (127 * 4/255 ~= 2) rather than a visible texture.
    private const byte NoiseAlpha = 4;

    // Fixed layout for up to six blobs, spread roughly evenly with one near
    // centre. Fewer blobs (a palette with fewer accepted colours) just uses
    // a prefix of this list.
    private static readonly (double X, double Y)[] BlobLayout =
    [
        (0.22, 0.28), (0.78, 0.22), (0.50, 0.50),
        (0.15, 0.78), (0.85, 0.75), (0.50, 0.12),
    ];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<BlobState> _blobs = [];
    // Fixed seed: this spreads each blob's period/phase deterministically so
    // a restart doesn't reshuffle the drift character - it's not meant to
    // look "random" to the viewer, just uncoordinated between blobs.
    private readonly Random _rng = new(20240101);

    private IReadOnlyList<LabColor> _fromPalette = [];
    private IReadOnlyList<LabColor> _toPalette = [];
    private double _transitionStartSeconds;
    private bool _hasPalette;

    public MeshGradientBackground()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        var baseFill = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)) };
        Children.Add(baseFill);

        var blobCount = Math.Min(BlobCount, BlobLayout.Length);
        for (var i = 0; i < blobCount; i++)
        {
            var (baseX, baseY) = BlobLayout[i];

            var core = new GradientStop(Colors.Transparent, 0.0);
            var edge = new GradientStop(Colors.Transparent, 1.0);
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(baseX, baseY),
                Center = new Point(baseX, baseY),
                RadiusX = BlobRadius,
                RadiusY = BlobRadius,
            };
            brush.GradientStops.Add(core);
            brush.GradientStops.Add(edge);
            // Deliberately not frozen - Center and stop colours are mutated
            // every frame and on every palette transition.

            var rect = new Rectangle { Fill = brush };
            Children.Add(rect);

            _blobs.Add(new BlobState(rect, brush, core, edge, baseX, baseY,
                PeriodX: MinPeriodSeconds + _rng.NextDouble() * (MaxPeriodSeconds - MinPeriodSeconds),
                PeriodY: MinPeriodSeconds + _rng.NextDouble() * (MaxPeriodSeconds - MinPeriodSeconds),
                PhaseX: _rng.NextDouble() * Math.PI * 2,
                PhaseY: _rng.NextDouble() * Math.PI * 2));
        }

        Children.Add(new Rectangle { Fill = BuildNoiseBrush(), IsHitTestVisible = false });

        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>
    /// Starts a ~2s Lab-space transition from whatever the background is
    /// currently showing to <paramref name="palette"/>. Safe to call again
    /// mid-transition - the current blended state becomes the new start
    /// point, so back-to-back track changes never jump.
    /// </summary>
    public void SetPalette(IReadOnlyList<Color> palette)
    {
        if (palette.Count == 0 || _blobs.Count == 0) return;

        var currentLab = new List<LabColor>(_blobs.Count);
        for (var i = 0; i < _blobs.Count; i++)
        {
            currentLab.Add(_hasPalette ? BlendedColor(i) : ColorLab.FromRgb(palette[i % palette.Count].R, palette[i % palette.Count].G, palette[i % palette.Count].B));
        }

        _fromPalette = currentLab;
        _toPalette = palette.Select(c => ColorLab.FromRgb(c.R, c.G, c.B)).ToList();
        _transitionStartSeconds = _clock.Elapsed.TotalSeconds;
        _hasPalette = true;
    }

    private double TransitionT() =>
        Math.Clamp((_clock.Elapsed.TotalSeconds - _transitionStartSeconds) / PaletteTransitionSeconds, 0.0, 1.0);

    private LabColor BlendedColor(int blobIndex)
    {
        var from = _fromPalette.Count > 0 ? _fromPalette[blobIndex % _fromPalette.Count] : default;
        var to = _toPalette.Count > 0 ? _toPalette[blobIndex % _toPalette.Count] : from;
        return LabColor.Lerp(from, to, TransitionT());
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = _clock.Elapsed.TotalSeconds;

        for (var i = 0; i < _blobs.Count; i++)
        {
            var blob = _blobs[i];
            var x = blob.BaseX + DriftRadius * Math.Sin(2 * Math.PI * t / blob.PeriodX + blob.PhaseX);
            var y = blob.BaseY + DriftRadius * Math.Sin(2 * Math.PI * t / blob.PeriodY + blob.PhaseY);
            var point = new Point(x, y);
            blob.Brush.Center = point;
            blob.Brush.GradientOrigin = point;

            if (_hasPalette)
            {
                var color = ColorLab.ToRgb(BlendedColor(i));
                blob.CoreStop.Color = color;
                blob.EdgeStop.Color = Color.FromArgb(0, color.R, color.G, color.B);
            }
        }
    }

    /// <summary>
    /// A tiled, near-invisible noise texture applied as the final layer.
    /// Large smooth gradients band badly at 8-bit colour, and it is very
    /// visible on the near-monochrome palettes this library tends to
    /// produce; this fakes the "per-pixel hash offset" dithering described
    /// in the spec without needing a pixel shader.
    /// </summary>
    private static ImageBrush BuildNoiseBrush()
    {
        var bitmap = new WriteableBitmap(NoiseTileSize, NoiseTileSize, 96, 96, PixelFormats.Bgra32, null);
        var rng = new Random(7); // fixed seed - a static, non-animated grain tile
        var pixels = new byte[NoiseTileSize * NoiseTileSize * 4];

        for (var i = 0; i < NoiseTileSize * NoiseTileSize; i++)
        {
            var v = (byte)rng.Next(0, 256);
            var idx = i * 4;
            pixels[idx] = v;       // B
            pixels[idx + 1] = v;   // G
            pixels[idx + 2] = v;   // R
            pixels[idx + 3] = NoiseAlpha;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, NoiseTileSize, NoiseTileSize), pixels, NoiseTileSize * 4, 0);
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, NoiseTileSize, NoiseTileSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private sealed record BlobState(
        Rectangle Element,
        RadialGradientBrush Brush,
        GradientStop CoreStop,
        GradientStop EdgeStop,
        double BaseX,
        double BaseY,
        double PeriodX,
        double PeriodY,
        double PhaseX,
        double PhaseY);
}
