namespace SystemAudioRecorder.Core;

public enum RecordingState { Recording, Paused, Stopped }

// One capture worker owns this timeline and its output stream.
public sealed class RecordingTimeline(Stream output, long startTicks)
{
    private readonly byte[] silence = new byte[AudioFormat.BytesPerSecond / 10];
    private long segmentStart = startTicks;
    private long segmentBase;
    private long? deviceAnchor;
    private long outputAnchor;
    private long lastDevicePosition;
    public long FramesWritten { get; private set; }
    public long SilenceFramesWritten { get; private set; }
    public long OverlapFramesSkipped { get; private set; }
    public long PausedFramesExcluded { get; private set; }
    public RecordingState State { get; private set; } = RecordingState.Recording;

    public void WritePacket(ReadOnlySpan<byte> pcm, long packetTicks, long devicePosition,
        bool discontinuity = false)
    {
        if (pcm.Length % AudioFormat.FrameBytes != 0)
            throw new ArgumentException("PCM must contain complete stereo frames.", nameof(pcm));
        ArgumentOutOfRangeException.ThrowIfNegative(devicePosition);
        if (State == RecordingState.Paused) PausedFramesExcluded += pcm.Length / AudioFormat.FrameBytes;
        if (State != RecordingState.Recording || pcm.IsEmpty) return;
        if (deviceAnchor == null || discontinuity)
        {
            deviceAnchor = devicePosition;
            outputAnchor = FrameAt(packetTicks);
        }
        else if (devicePosition < lastDevicePosition)
        {
            throw new InvalidOperationException("The audio device position moved backwards without a discontinuity.");
        }
        // QPC timestamps can jitter or drift relative to the audio clock. Use sample
        // positions inside a continuous stream; re-anchor only across real boundaries.
        var position = checked(outputAnchor + devicePosition - deviceAnchor.Value);
        lastDevicePosition = devicePosition;
        var frames = pcm.Length / AudioFormat.FrameBytes;
        var skip = Math.Min(frames, Math.Max(0, FramesWritten - position));
        OverlapFramesSkipped += skip;
        if (skip == frames) return;
        PadTo(position);
        output.Write(pcm[(checked((int)skip) * AudioFormat.FrameBytes)..]);
        FramesWritten += frames - skip;
    }

    public void CommitSilence(long throughTicks)
    {
        if (State != RecordingState.Recording) return;
        var target = FrameAt(throughTicks);
        if (target <= FramesWritten) return;
        PadTo(target);
        deviceAnchor = null;
    }

    public void Pause(long nowTicks)
    {
        if (State != RecordingState.Recording)
            throw new InvalidOperationException("Only an active recording can be paused.");
        CommitSilence(nowTicks);
        State = RecordingState.Paused;
    }

    public void Resume(long nowTicks)
    {
        if (State != RecordingState.Paused)
            throw new InvalidOperationException("Only a paused recording can be resumed.");
        segmentBase = FramesWritten;
        segmentStart = nowTicks;
        deviceAnchor = null;
        State = RecordingState.Recording;
    }

    public void Stop(long nowTicks)
    {
        if (State == RecordingState.Stopped) return;
        CommitSilence(nowTicks);
        State = RecordingState.Stopped;
    }

    private long FrameAt(long ticks) =>
        segmentBase + (long)Math.Round((ticks - segmentStart) *
            ((double)AudioFormat.SampleRate / TimeSpan.TicksPerSecond));

    private void PadTo(long target)
    {
        while (FramesWritten < target)
        {
            var frames = (int)Math.Min(target - FramesWritten, silence.Length / AudioFormat.FrameBytes);
            output.Write(silence.AsSpan(0, frames * AudioFormat.FrameBytes));
            FramesWritten += frames;
            SilenceFramesWritten += frames;
        }
    }
}
