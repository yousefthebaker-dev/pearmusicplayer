using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmbientPlayer.Utilities;

namespace AmbientPlayer.Rendering;

/// <summary>
/// The background: a slowly-evolving Perlin noise field at a medium spatial
/// scale, colour-mapped through the current palette (dark end to light end)
/// and softened with a slight Gaussian blur. Rendered at low resolution -
/// noise this smooth doesn't need per-pixel detail - and upscaled with
/// bilinear filtering, which does most of the softening even before the
/// blur is added.
///
/// Continuity across track changes comes from advancing the noise's time
/// coordinate from wall-clock time since this control was created; it is
/// never reset.
/// </summary>
public sealed class MeshGradientBackground : Grid
{
    private const int TextureSize = 64;
    private const double SpatialFrequency = 0.65; // fewer, larger cycles across the texture
    private const double TimeSpeed = 0.09; // noise-space units per second
    private const double ContrastAmount = 2.1; // >1 pushes noise values toward the ramp's extremes
    private const double PaletteTransitionSeconds = 2.0;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(16); // ~60 Hz

    private static readonly List<LabColor> NeutralRamp =
    [
        ColorLab.FromRgb(0x14, 0x16, 0x1c),
        ColorLab.FromRgb(0x2a, 0x2d, 0x39),
    ];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly PerlinNoise _noise = new(20240101);
    private readonly WriteableBitmap _bitmap;
    private readonly DispatcherTimer _timer;
    private readonly byte[] _pixels = new byte[TextureSize * TextureSize * 4];

    private IReadOnlyList<LabColor> _fromRamp = [];
    private IReadOnlyList<LabColor> _toRamp = [];
    private double _transitionStartSeconds;
    private bool _hasPalette;

    public MeshGradientBackground()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _bitmap = new WriteableBitmap(TextureSize, TextureSize, 96, 96, PixelFormats.Bgra32, null);

        var image = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            // "Slight" blur on top of the low-res upscale - not a shader,
            // just WPF's own (Gaussian-kernel) BlurEffect.
            Effect = new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian },
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        Children.Add(image);

        RenderTexture(); // paint an initial frame immediately rather than waiting for the first tick

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateInterval };
        _timer.Tick += (_, _) => RenderTexture();

        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Starts a ~2s Lab-space transition from whatever the background is
    /// currently showing to <paramref name="palette"/>. Safe to call again
    /// mid-transition - the current blended ramp becomes the new start
    /// point, so back-to-back track changes never jump.
    /// </summary>
    public void SetPalette(IReadOnlyList<Color> palette)
    {
        if (palette.Count == 0) return;

        // Sorted dark-to-light: the noise value maps linearly across this
        // ramp, so ordering it by lightness is what makes low noise read as
        // shadow and high noise read as the brightest album colour.
        var newRamp = palette
            .Select(c => ColorLab.FromRgb(c.R, c.G, c.B))
            .OrderBy(lab => lab.L)
            .ToList();

        _fromRamp = _hasPalette ? CurrentRamp() : newRamp;
        _toRamp = newRamp;
        _transitionStartSeconds = _clock.Elapsed.TotalSeconds;
        _hasPalette = true;
    }

    private double TransitionT() =>
        Math.Clamp((_clock.Elapsed.TotalSeconds - _transitionStartSeconds) / PaletteTransitionSeconds, 0.0, 1.0);

    private List<LabColor> CurrentRamp()
    {
        var t = TransitionT();
        var count = Math.Max(_fromRamp.Count, _toRamp.Count);
        var ramp = new List<LabColor>(count);
        for (var i = 0; i < count; i++)
        {
            var from = _fromRamp.Count > 0 ? _fromRamp[i % _fromRamp.Count] : default;
            var to = _toRamp.Count > 0 ? _toRamp[i % _toRamp.Count] : from;
            ramp.Add(LabColor.Lerp(from, to, t));
        }
        return ramp;
    }

    private void RenderTexture()
    {
        var ramp = _hasPalette ? CurrentRamp() : NeutralRamp;
        if (ramp.Count == 0) return;

        var t = _clock.Elapsed.TotalSeconds * TimeSpeed;

        for (var py = 0; py < TextureSize; py++)
        {
            var ny = (double)py / TextureSize * SpatialFrequency;
            for (var px = 0; px < TextureSize; px++)
            {
                var nx = (double)px / TextureSize * SpatialFrequency;
                var n = _noise.Fbm(nx, ny, t, octaves: 3, persistence: 0.5);
                var normalised = Math.Clamp((n + 1.0) * 0.5, 0.0, 1.0);
                var contrasted = ApplyContrast(normalised, ContrastAmount);

                var color = ColorLab.ToRgb(SampleRamp(ramp, contrasted));

                var idx = (py * TextureSize + px) * 4;
                _pixels[idx] = color.B;
                _pixels[idx + 1] = color.G;
                _pixels[idx + 2] = color.R;
                _pixels[idx + 3] = 255;
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, TextureSize, TextureSize), _pixels, TextureSize * 4, 0);
    }

    /// <summary>
    /// Pushes a [0,1] value away from the midpoint by <paramref name="amount"/>
    /// (a simple S-curve around 0.5, clamped at the ends) so more of the
    /// field lands near the ramp's dark/light extremes and the transition
    /// band between them narrows - a photo "contrast" slider, in effect.
    /// </summary>
    private static double ApplyContrast(double t, double amount) =>
        Math.Clamp(0.5 + (t - 0.5) * amount, 0.0, 1.0);

    private static LabColor SampleRamp(List<LabColor> ramp, double t)
    {
        if (ramp.Count == 1) return ramp[0];

        var scaled = Math.Clamp(t, 0.0, 1.0) * (ramp.Count - 1);
        var index = (int)Math.Floor(scaled);
        if (index >= ramp.Count - 1) return ramp[^1];

        var frac = scaled - index;
        return LabColor.Lerp(ramp[index], ramp[index + 1], frac);
    }
}
