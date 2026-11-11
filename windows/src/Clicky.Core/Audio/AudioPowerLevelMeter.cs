namespace Clicky.Core.Audio;

/// <summary>
/// Converts microphone samples into the 0..1 level that drives the listening waveform.
/// Same math as BuddyDictationManager.updateAudioPowerLevel on the Mac: RMS boosted by
/// 10.2 and clamped, then a fast-attack, slow-release smoother (new level or 72% of the
/// previous level, whichever is higher).
/// </summary>
public sealed class AudioPowerLevelMeter
{
    public const double RootMeanSquareBoostFactor = 10.2;
    public const double ReleaseSmoothingFactor = 0.72;

    private double currentAudioPowerLevel;

    public double CurrentAudioPowerLevel => Volatile.Read(ref currentAudioPowerLevel);

    /// <summary>Applies one buffer of mono samples in the -1..1 range and returns the smoothed level.</summary>
    public double ApplySamples(ReadOnlySpan<float> monoSamples)
    {
        if (monoSamples.Length == 0)
        {
            return CurrentAudioPowerLevel;
        }

        double boostedAudioPowerLevel = ComputeBoostedLevel(monoSamples);
        double smoothedAudioPowerLevel = Math.Max(boostedAudioPowerLevel, CurrentAudioPowerLevel * ReleaseSmoothingFactor);
        Volatile.Write(ref currentAudioPowerLevel, smoothedAudioPowerLevel);
        return smoothedAudioPowerLevel;
    }

    public static double ComputeBoostedLevel(ReadOnlySpan<float> monoSamples)
    {
        if (monoSamples.Length == 0)
        {
            return 0;
        }

        double summedSquares = 0;
        foreach (float monoSample in monoSamples)
        {
            summedSquares += monoSample * monoSample;
        }

        double rootMeanSquare = Math.Sqrt(summedSquares / monoSamples.Length);
        return Math.Clamp(rootMeanSquare * RootMeanSquareBoostFactor, 0, 1);
    }

    public void Reset()
    {
        Volatile.Write(ref currentAudioPowerLevel, 0);
    }
}
