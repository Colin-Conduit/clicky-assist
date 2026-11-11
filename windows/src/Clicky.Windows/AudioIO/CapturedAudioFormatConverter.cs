using System.Buffers.Binary;
using Clicky.Core.Transcription;
using NAudio.Dsp;
using NAudio.Wave;

namespace Clicky.Windows.AudioIO;

/// <summary>
/// Turns one WASAPI capture buffer into two things:
/// mono float samples at the device rate (what the level meter reads, like the Mac's
/// updateAudioPowerLevel), and 16 kHz mono signed 16-bit little-endian PCM (what AssemblyAI expects).
///
/// WASAPI shared mode delivers the endpoint's mix format, which the driver defines and which
/// follows the device's sample rate and channel count in the Sound control panel. It is almost
/// always 32-bit IEEE float (48 kHz, 4 channels on the array microphone this was tested with),
/// but nothing guarantees that, and PCM at 16, 24 or 32 bits is possible. So the format is read
/// once per session and every buffer is decoded sample by sample instead of assuming float.
///
/// One instance per capture session: the WDL resampler keeps filter state and leftover input
/// between buffers, so reusing it across sessions would leak the tail of the previous recording
/// into the next one.
/// </summary>
internal sealed class CapturedAudioFormatConverter
{
    private const int MonoChannelCount = 1;
    private const int BytesPerPcm16Sample = 2;

    /// <summary>
    /// Extra room in the resampler's output buffer beyond the exact rate ratio. The resampler may
    /// emit a few more frames than input times ratio because it also drains input it held back
    /// from the previous buffer for interpolation.
    /// </summary>
    private const int ResamplerOutputMarginInFrames = 64;

    private enum CapturedSampleEncoding
    {
        UnsignedPcm8,
        SignedPcm16,
        SignedPcm24,
        SignedPcm32,
        IeeeFloat32,
        IeeeFloat64,
    }

    private readonly CapturedSampleEncoding capturedSampleEncoding;
    private readonly int capturedChannelCount;
    private readonly int bytesPerCapturedSample;
    private readonly int bytesPerCapturedFrame;
    private readonly int deviceSampleRateInHertz;

    /// <summary>Null when the device already runs at 16 kHz, so samples pass straight through.</summary>
    private readonly WdlResampler? targetSampleRateResampler;

    private float[] monoSampleBuffer = [];
    private float[] resampledSampleBuffer = [];

    /// <param name="captureWaveFormat">
    /// The format from NAudio's WasapiCapture.WaveFormat. NAudio already flattens
    /// WAVEFORMATEXTENSIBLE into plain Pcm or IeeeFloat for the common cases, so only the
    /// encoding tag and bit depth are needed here.
    /// </param>
    public CapturedAudioFormatConverter(WaveFormat captureWaveFormat)
    {
        capturedSampleEncoding = DetermineSampleEncoding(captureWaveFormat);
        capturedChannelCount = captureWaveFormat.Channels;
        bytesPerCapturedSample = captureWaveFormat.BitsPerSample / 8;

        // Same frame size NAudio uses when it copies packets out of IAudioCaptureClient.
        bytesPerCapturedFrame = capturedChannelCount * bytesPerCapturedSample;
        deviceSampleRateInHertz = captureWaveFormat.SampleRate;

        if (capturedChannelCount < 1 || bytesPerCapturedFrame < 1 || deviceSampleRateInHertz < 1)
        {
            throw new NotSupportedException($"The microphone reported an unusable audio format ({captureWaveFormat}).");
        }

        if (deviceSampleRateInHertz != AssemblyAIStreamingConfiguration.TargetSampleRateInHertz)
        {
            targetSampleRateResampler = new WdlResampler();

            // Same settings as NAudio's WdlResamplingSampleProvider: linear interpolation plus two
            // passes of the IIR low-pass filter, which is what keeps 48 kHz content above 8 kHz
            // from aliasing into the 16 kHz speech band.
            targetSampleRateResampler.SetMode(true, 2, false);
            targetSampleRateResampler.SetFilterParms();

            // Input driven: each call says how many input frames it has, rather than how many
            // output frames it wants, because the capture thread pushes whatever WASAPI delivered.
            targetSampleRateResampler.SetFeedMode(true);
            targetSampleRateResampler.SetRates(deviceSampleRateInHertz, AssemblyAIStreamingConfiguration.TargetSampleRateInHertz);
        }
    }

    public string CaptureFormatDescription =>
        $"{capturedSampleEncoding}, {capturedChannelCount} channel(s), {deviceSampleRateInHertz} Hz";

    /// <summary>
    /// Decodes the buffer and averages all channels into one mono channel in the -1..1 range.
    /// The returned span reuses an internal array and is only valid until the next call.
    /// </summary>
    public ReadOnlySpan<float> ConvertToMonoSamples(byte[] capturedBytes, int capturedByteCount)
    {
        int capturedFrameCount = capturedByteCount / bytesPerCapturedFrame;
        EnsureCapacity(ref monoSampleBuffer, capturedFrameCount);

        ReadOnlySpan<byte> capturedByteSpan = capturedBytes.AsSpan(0, capturedFrameCount * bytesPerCapturedFrame);

        for (int frameIndex = 0; frameIndex < capturedFrameCount; frameIndex++)
        {
            int frameByteOffset = frameIndex * bytesPerCapturedFrame;
            float summedChannelSamples = 0;

            for (int channelIndex = 0; channelIndex < capturedChannelCount; channelIndex++)
            {
                int sampleByteOffset = frameByteOffset + (channelIndex * bytesPerCapturedSample);
                summedChannelSamples += ReadSampleAsFloat(capturedByteSpan.Slice(sampleByteOffset, bytesPerCapturedSample));
            }

            monoSampleBuffer[frameIndex] = summedChannelSamples / capturedChannelCount;
        }

        return monoSampleBuffer.AsSpan(0, capturedFrameCount);
    }

    /// <summary>
    /// Resamples mono device-rate samples to 16 kHz, clamps them to -1..1 and packs them as
    /// signed 16-bit little-endian PCM. Returns a new array each time because the caller hands it
    /// to another thread (the AssemblyAI session queues it).
    /// </summary>
    public byte[] ConvertMonoSamplesToTargetPcm16(ReadOnlySpan<float> monoSamples)
    {
        ReadOnlySpan<float> targetRateSamples = targetSampleRateResampler is null
            ? monoSamples
            : ResampleToTargetSampleRate(targetSampleRateResampler, monoSamples);

        byte[] pcm16AudioBytes = new byte[targetRateSamples.Length * BytesPerPcm16Sample];

        for (int sampleIndex = 0; sampleIndex < targetRateSamples.Length; sampleIndex++)
        {
            // The resampler's filter can overshoot slightly on loud transients, so clamp before
            // scaling or the short conversion would wrap around and click.
            float clampedSample = Math.Clamp(targetRateSamples[sampleIndex], -1f, 1f);
            short pcm16Sample = (short)MathF.Round(clampedSample * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16AudioBytes.AsSpan(sampleIndex * BytesPerPcm16Sample), pcm16Sample);
        }

        return pcm16AudioBytes;
    }

    private ReadOnlySpan<float> ResampleToTargetSampleRate(WdlResampler resampler, ReadOnlySpan<float> monoSamples)
    {
        double outputFramesPerInputFrame = (double)AssemblyAIStreamingConfiguration.TargetSampleRateInHertz / deviceSampleRateInHertz;
        int maximumOutputFrameCount = (int)Math.Ceiling(monoSamples.Length * outputFramesPerInputFrame) + ResamplerOutputMarginInFrames;
        EnsureCapacity(ref resampledSampleBuffer, maximumOutputFrameCount);

        int consumedInputFrameCount = 0;
        int producedOutputFrameCount = 0;

        // In feed mode ResamplePrepare accepts everything in one call. The loop only matters if
        // it ever hands back a smaller count, in which case the rest goes in on the next pass.
        while (consumedInputFrameCount < monoSamples.Length)
        {
            int remainingInputFrameCount = monoSamples.Length - consumedInputFrameCount;
            int acceptedInputFrameCount = resampler.ResamplePrepare(
                remainingInputFrameCount,
                MonoChannelCount,
                out float[] resamplerInputBuffer,
                out int resamplerInputOffset);

            if (acceptedInputFrameCount <= 0)
            {
                break;
            }

            monoSamples
                .Slice(consumedInputFrameCount, acceptedInputFrameCount)
                .CopyTo(resamplerInputBuffer.AsSpan(resamplerInputOffset, acceptedInputFrameCount));

            // Pass exactly the accepted count: a smaller count makes WDL treat this as the end of
            // the stream and pad with silence, which would put a gap in every buffer.
            int remainingOutputCapacity = resampledSampleBuffer.Length - producedOutputFrameCount;
            producedOutputFrameCount += resampler.ResampleOut(
                resampledSampleBuffer,
                producedOutputFrameCount,
                acceptedInputFrameCount,
                remainingOutputCapacity,
                MonoChannelCount);

            consumedInputFrameCount += acceptedInputFrameCount;
        }

        return resampledSampleBuffer.AsSpan(0, producedOutputFrameCount);
    }

    private float ReadSampleAsFloat(ReadOnlySpan<byte> sampleBytes)
    {
        switch (capturedSampleEncoding)
        {
            case CapturedSampleEncoding.UnsignedPcm8:
                // 8-bit WAV PCM is unsigned with silence at 128.
                return (sampleBytes[0] - 128) / 128f;

            case CapturedSampleEncoding.SignedPcm16:
                return BinaryPrimitives.ReadInt16LittleEndian(sampleBytes) / 32768f;

            case CapturedSampleEncoding.SignedPcm24:
            {
                // Three little-endian bytes. Casting the top byte to sbyte carries the sign into
                // the upper bits of the int.
                int signedTwentyFourBitSample = sampleBytes[0] | (sampleBytes[1] << 8) | ((sbyte)sampleBytes[2] << 16);
                return signedTwentyFourBitSample / 8388608f;
            }

            case CapturedSampleEncoding.SignedPcm32:
                // Also covers 24 valid bits in a 32-bit container: WASAPI left-justifies them, so
                // dividing by the full 32-bit range gives the right scale.
                return BinaryPrimitives.ReadInt32LittleEndian(sampleBytes) / 2147483648f;

            case CapturedSampleEncoding.IeeeFloat32:
                return BinaryPrimitives.ReadSingleLittleEndian(sampleBytes);

            case CapturedSampleEncoding.IeeeFloat64:
                return (float)BinaryPrimitives.ReadDoubleLittleEndian(sampleBytes);

            default:
                throw new InvalidOperationException($"Unhandled captured sample encoding {capturedSampleEncoding}.");
        }
    }

    private static CapturedSampleEncoding DetermineSampleEncoding(WaveFormat captureWaveFormat)
    {
        if (captureWaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            switch (captureWaveFormat.BitsPerSample)
            {
                case 32:
                    return CapturedSampleEncoding.IeeeFloat32;
                case 64:
                    return CapturedSampleEncoding.IeeeFloat64;
            }
        }
        else if (captureWaveFormat.Encoding == WaveFormatEncoding.Pcm)
        {
            switch (captureWaveFormat.BitsPerSample)
            {
                case 8:
                    return CapturedSampleEncoding.UnsignedPcm8;
                case 16:
                    return CapturedSampleEncoding.SignedPcm16;
                case 24:
                    return CapturedSampleEncoding.SignedPcm24;
                case 32:
                    return CapturedSampleEncoding.SignedPcm32;
            }
        }

        throw new NotSupportedException($"The microphone's shared-mode format is not supported ({captureWaveFormat}).");
    }

    private static void EnsureCapacity(ref float[] sampleBuffer, int requiredSampleCount)
    {
        if (sampleBuffer.Length < requiredSampleCount)
        {
            sampleBuffer = new float[requiredSampleCount];
        }
    }
}
