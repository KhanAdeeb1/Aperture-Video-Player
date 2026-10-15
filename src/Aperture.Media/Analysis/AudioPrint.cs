using System.Numerics;

namespace Aperture.Media.Analysis;

/// <summary>
/// A compact fingerprint of a file's sound: one 32-bit value for every 46 ms, describing how
/// the energy in 33 frequency bands between 300 and 2000 Hz rises and falls (the method
/// published by Haitsma and Kalker for audio identification). The same music or dialogue
/// gives nearly the same bits in two files even when they were encoded differently, at other
/// volumes or sample rates, which is what lets Aperture find an intro that repeats across
/// episodes without assuming where it is.
/// </summary>
public sealed class AudioPrint
{
    /// <summary>Sound is decoded to mono at this rate for fingerprinting.</summary>
    public const int SampleRate = 5512;

    /// <summary>Samples per analysis window (0.37 s).</summary>
    public const int WindowSize = 2048;

    /// <summary>Samples between windows (46 ms).</summary>
    public const int Hop = 256;

    private const int Bands = 33;
    private const double LowestHz = 300;
    private const double HighestHz = 2000;

    /// <summary>Windows quieter than this (RMS of 16-bit samples, about −55 dBFS) carry no usable information.</summary>
    private const double QuietRms = 58;

    private static readonly int[] BandEdges = MakeBandEdges();
    private static readonly double[] Window = MakeWindow();

    public AudioPrint(uint[] hashes, bool[] quiet, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(quiet);
        if (hashes.Length != quiet.Length)
        {
            throw new ArgumentException("Every hash needs a quiet flag.", nameof(quiet));
        }

        Hashes = hashes;
        Quiet = quiet;
        Start = start;
    }

    /// <summary>Seconds between consecutive frames.</summary>
    public static double FrameSeconds => (double)Hop / SampleRate;

    /// <summary>One value per frame.</summary>
    public uint[] Hashes { get; }

    /// <summary>True for frames too quiet (or too early) to compare.</summary>
    public bool[] Quiet { get; }

    /// <summary>Where in the file frame 0 is.</summary>
    public TimeSpan Start { get; }

    public int Count => Hashes.Length;

    /// <summary>The time a frame describes (the middle of its window).</summary>
    public TimeSpan TimeAt(int frame) => Start + TimeSpan.FromSeconds(((frame * (double)Hop) + (WindowSize / 2.0)) / SampleRate);

    /// <summary>The frame nearest to <paramref name="time"/>, clamped to the print.</summary>
    public int FrameAt(TimeSpan time)
    {
        var frame = (int)Math.Round((((time - Start).TotalSeconds * SampleRate) - (WindowSize / 2.0)) / Hop);
        return Math.Clamp(frame, 0, Count);
    }

    /// <summary>Number of frames covering <paramref name="length"/>.</summary>
    public static int Frames(TimeSpan length) => (int)Math.Round(length.TotalSeconds / FrameSeconds);

    /// <summary>Fingerprints mono samples at <see cref="SampleRate"/> that start at <paramref name="start"/> in the file.</summary>
    public static AudioPrint Compute(ReadOnlySpan<short> samples, TimeSpan start)
    {
        var frames = samples.Length < WindowSize ? 0 : ((samples.Length - WindowSize) / Hop) + 1;
        var hashes = new uint[frames];
        var quiet = new bool[frames];
        var fft = new Complex[WindowSize];
        var previous = new double[Bands];
        var current = new double[Bands];

        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * Hop;
            double sumSquares = 0;
            for (var i = 0; i < WindowSize; i++)
            {
                double sample = samples[offset + i];
                sumSquares += sample * sample;
                fft[i] = new Complex(sample * Window[i], 0);
            }

            Fft.Transform(fft);
            for (var band = 0; band < Bands; band++)
            {
                double energy = 0;
                for (var bin = BandEdges[band]; bin < BandEdges[band + 1]; bin++)
                {
                    var value = fft[bin];
                    energy += (value.Real * value.Real) + (value.Imaginary * value.Imaginary);
                }

                current[band] = energy;
            }

            var tooQuiet = Math.Sqrt(sumSquares / WindowSize) < QuietRms;
            if (frame == 0)
            {
                quiet[0] = true;
            }
            else
            {
                uint hash = 0;
                for (var bit = 0; bit < Bands - 1; bit++)
                {
                    var difference = current[bit] - current[bit + 1] - (previous[bit] - previous[bit + 1]);
                    if (difference > 0)
                    {
                        hash |= 1u << bit;
                    }
                }

                hashes[frame] = hash;
                quiet[frame] = tooQuiet;
            }

            (previous, current) = (current, previous);
        }

        return new AudioPrint(hashes, quiet, start);
    }

    private static int[] MakeBandEdges()
    {
        var edges = new int[Bands + 1];
        for (var i = 0; i <= Bands; i++)
        {
            var hz = LowestHz * Math.Pow(HighestHz / LowestHz, (double)i / Bands);
            edges[i] = (int)Math.Round(hz * WindowSize / SampleRate);
        }

        // Every band covers at least one frequency bin.
        for (var i = 1; i <= Bands; i++)
        {
            edges[i] = Math.Max(edges[i], edges[i - 1] + 1);
        }

        return edges;
    }

    private static double[] MakeWindow()
    {
        var window = new double[WindowSize];
        for (var i = 0; i < WindowSize; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (WindowSize - 1)));
        }

        return window;
    }
}

/// <summary>An in-place radix-2 fast Fourier transform for power-of-two lengths.</summary>
internal static class Fft
{
    private static readonly Dictionary<int, (int[] Reverse, Complex[] Twiddles)> Tables = [];
    private static readonly Lock TablesGate = new();

    public static void Transform(Complex[] data)
    {
        var n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException("The length must be a power of two.", nameof(data));
        }

        var (reverse, twiddles) = TablesFor(n);
        for (var i = 0; i < n; i++)
        {
            var j = reverse[i];
            if (j > i)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size >> 1;
            var step = n / size;
            for (var start = 0; start < n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var t = twiddles[k * step] * data[start + k + half];
                    var u = data[start + k];
                    data[start + k] = u + t;
                    data[start + k + half] = u - t;
                }
            }
        }
    }

    private static (int[] Reverse, Complex[] Twiddles) TablesFor(int n)
    {
        lock (TablesGate)
        {
            if (Tables.TryGetValue(n, out var tables))
            {
                return tables;
            }

            var bits = BitOperations.Log2((uint)n);
            var reverse = new int[n];
            for (var i = 0; i < n; i++)
            {
                var r = 0;
                for (var b = 0; b < bits; b++)
                {
                    r |= ((i >> b) & 1) << (bits - 1 - b);
                }

                reverse[i] = r;
            }

            var twiddles = new Complex[n / 2];
            for (var k = 0; k < n / 2; k++)
            {
                twiddles[k] = Complex.FromPolarCoordinates(1, -2 * Math.PI * k / n);
            }

            tables = (reverse, twiddles);
            Tables[n] = tables;
            return tables;
        }
    }
}
