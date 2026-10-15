using Aperture.Media.Analysis;

namespace Aperture.Media.Tests.Analysis;

/// <summary>
/// Builds episode soundtracks from parts, at the fingerprinting rate: music (a theme with a
/// melody, harmony and percussion, the same every time for the same seed) and dialogue-like
/// sound (formant-shaped noise in syllables and pauses, different for every seed). Each
/// "release" re-encodes the result: another level, a little noise, a softened top end and a
/// shift by part of a frame, as two encodes of the same programme differ.
/// </summary>
internal static class SyntheticAudio
{
    public const int Rate = AudioPrint.SampleRate;

    /// <summary>A recurring piece of music. The same seed always gives the same music.</summary>
    public static short[] Music(int seed, double seconds)
    {
        var random = new Random(seed);
        var samples = new double[(int)(seconds * Rate)];
        double[] scale = [220, 247, 262, 294, 330, 349, 392, 440, 494, 523, 587, 659];
        var t = 0;
        while (t < samples.Length)
        {
            var noteLength = (int)(Rate * (0.18 + (random.NextDouble() * 0.35)));
            var root = scale[random.Next(scale.Length)];
            var third = root * (random.Next(2) == 0 ? 1.26 : 1.19);
            var bass = root / 2;
            var drum = random.Next(3) == 0;
            for (var i = 0; i < noteLength && t + i < samples.Length; i++)
            {
                var time = (double)i / Rate;
                var envelope = Math.Exp(-time * 3) * Math.Min(1, i / 40.0);
                var tone = (Math.Sin(2 * Math.PI * root * time) * 0.5)
                    + (Math.Sin(2 * Math.PI * root * 2 * time) * 0.2)
                    + (Math.Sin(2 * Math.PI * third * time) * 0.3)
                    + (Math.Sin(2 * Math.PI * bass * time) * 0.4);
                var hit = drum && i < Rate / 20 ? (random.NextDouble() - 0.5) * 0.8 * (1 - (i / (Rate / 20.0))) : 0;
                samples[t + i] = (tone * envelope * 0.6) + hit;
            }

            t += noteLength;
        }

        return ToShort(samples, 9000);
    }

    /// <summary>Dialogue-like sound that never repeats between seeds.</summary>
    public static short[] Speech(int seed, double seconds)
    {
        var random = new Random(seed * 7919 + 17);
        var samples = new double[(int)(seconds * Rate)];
        var t = 0;
        while (t < samples.Length)
        {
            var pause = random.Next(4) == 0;
            var length = (int)(Rate * (pause ? 0.08 + (random.NextDouble() * 0.25) : 0.09 + (random.NextDouble() * 0.22)));
            if (!pause)
            {
                // Two formant resonators over noise and a voiced buzz.
                var f1 = 300 + (random.NextDouble() * 600);
                var f2 = 900 + (random.NextDouble() * 1400);
                var pitch = 90 + (random.NextDouble() * 140);
                double y1 = 0, y2 = 0, z1 = 0, z2 = 0;
                var (a1, a2, b) = Resonator(f1, 90);
                var (c1, c2, d) = Resonator(f2, 120);
                for (var i = 0; i < length && t + i < samples.Length; i++)
                {
                    var time = (double)i / Rate;
                    var source = ((random.NextDouble() - 0.5) * 0.6) + (Math.Sin(2 * Math.PI * pitch * time) > 0.6 ? 0.8 : -0.1);
                    var y = (b * source) + (a1 * y1) - (a2 * y2);
                    y2 = y1;
                    y1 = y;
                    var z = (d * source) + (c1 * z1) - (c2 * z2);
                    z2 = z1;
                    z1 = z;
                    var envelope = Math.Sin(Math.PI * i / length);
                    samples[t + i] = (y + (z * 0.6)) * envelope;
                }
            }

            t += length;
        }

        // Quiet room tone under the dialogue, as real recordings have.
        var noise = new Random(seed * 31 + 3);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] += (noise.NextDouble() - 0.5) * 0.02;
        }

        return ToShort(samples, 7000);
    }

    /// <summary>Digital silence.</summary>
    public static short[] Silence(double seconds) => new short[(int)(seconds * Rate)];

    public static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>The same programme as another encode would deliver it.</summary>
    public static short[] Release(short[] source, int seed, double gain = 0.8)
    {
        var random = new Random(seed);
        var shift = random.Next(AudioPrint.Hop);
        var output = new short[source.Length + shift];
        double previous = 0;
        for (var i = 0; i < source.Length; i++)
        {
            // A gentle low-pass, a level change and a little noise.
            var smoothed = (source[i] * 0.7) + (previous * 0.3);
            previous = source[i];
            var value = (smoothed * gain) + ((random.NextDouble() - 0.5) * 120);
            output[i + shift] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        return output;
    }

    public static TimeSpan Length(short[] samples) => TimeSpan.FromSeconds((double)samples.Length / Rate);

    private static (double A1, double A2, double B) Resonator(double frequency, double bandwidth)
    {
        var r = Math.Exp(-Math.PI * bandwidth / Rate);
        var a1 = 2 * r * Math.Cos(2 * Math.PI * frequency / Rate);
        var a2 = r * r;
        return (a1, a2, 1 - r);
    }

    private static short[] ToShort(double[] samples, double peak)
    {
        var max = samples.Length == 0 ? 1 : samples.Max(Math.Abs);
        var scale = max > 0 ? peak / max : 0;
        return samples.Select(s => (short)Math.Clamp(s * scale, short.MinValue, short.MaxValue)).ToArray();
    }
}
