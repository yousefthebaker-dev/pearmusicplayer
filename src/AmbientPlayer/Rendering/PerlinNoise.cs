namespace AmbientPlayer.Rendering;

/// <summary>
/// Classic Ken Perlin "improved noise" (2002), 3D gradient noise, plus a
/// fractal-sum (fBm) helper for a richer, more natural-looking field than a
/// single octave gives. The permutation table is a seeded Fisher-Yates
/// shuffle rather than Perlin's original hardcoded table - any permutation
/// of 0-255 produces statistically equivalent noise, and generating it
/// avoids transcribing a 256-entry magic table by hand.
/// </summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];

    public PerlinNoise(int seed)
    {
        var table = new int[256];
        for (var i = 0; i < 256; i++) table[i] = i;

        var rng = new Random(seed);
        for (var i = 255; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (table[i], table[j]) = (table[j], table[i]);
        }

        for (var i = 0; i < 512; i++) _perm[i] = table[i & 255];
    }

    /// <summary>Fractal sum of several noise octaves, normalised to roughly [-1, 1].</summary>
    public double Fbm(double x, double y, double z, int octaves = 3, double persistence = 0.5)
    {
        double total = 0, amplitude = 1, frequency = 1, maxAmplitude = 0;
        for (var i = 0; i < octaves; i++)
        {
            total += Noise(x * frequency, y * frequency, z * frequency) * amplitude;
            maxAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= 2;
        }
        return maxAmplitude > 0 ? total / maxAmplitude : 0;
    }

    public double Noise(double x, double y, double z)
    {
        var xi = (int)Math.Floor(x) & 255;
        var yi = (int)Math.Floor(y) & 255;
        var zi = (int)Math.Floor(z) & 255;

        x -= Math.Floor(x);
        y -= Math.Floor(y);
        z -= Math.Floor(z);

        var u = Fade(x);
        var v = Fade(y);
        var w = Fade(z);

        var a = _perm[xi] + yi;
        var aa = _perm[a] + zi;
        var ab = _perm[a + 1] + zi;
        var b = _perm[xi + 1] + yi;
        var ba = _perm[b] + zi;
        var bb = _perm[b + 1] + zi;

        return Lerp(w,
            Lerp(v,
                Lerp(u, Grad(_perm[aa], x, y, z), Grad(_perm[ba], x - 1, y, z)),
                Lerp(u, Grad(_perm[ab], x, y - 1, z), Grad(_perm[bb], x - 1, y - 1, z))),
            Lerp(v,
                Lerp(u, Grad(_perm[aa + 1], x, y, z - 1), Grad(_perm[ba + 1], x - 1, y, z - 1)),
                Lerp(u, Grad(_perm[ab + 1], x, y - 1, z - 1), Grad(_perm[bb + 1], x - 1, y - 1, z - 1))));
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    private static double Grad(int hash, double x, double y, double z)
    {
        var h = hash & 15;
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : h == 12 || h == 14 ? x : z;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
