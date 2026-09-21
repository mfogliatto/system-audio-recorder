using System.Buffers.Binary;

namespace SystemAudioRecorder.Core;

public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int FrameBytes = Channels * BitsPerSample / 8;
    public const int BytesPerSecond = SampleRate * FrameBytes;

    public static TimeSpan Duration(long bytes) =>
        TimeSpan.FromSeconds((double)(bytes / FrameBytes) / SampleRate);

    public static float Peak(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % FrameBytes != 0)
            throw new ArgumentException("PCM must contain complete stereo frames.", nameof(pcm));
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[i..])));
        return peak / 32768f;
    }
}
