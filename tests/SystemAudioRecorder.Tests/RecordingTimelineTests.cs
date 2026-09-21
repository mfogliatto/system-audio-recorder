using System.Buffers.Binary;
using SystemAudioRecorder.Core;

namespace SystemAudioRecorder.Tests;

public class RecordingTimelineTests
{
    private static long Seconds(double value) => (long)(value * TimeSpan.TicksPerSecond);
    private static byte[] Audio(double seconds, short value = 1234)
    {
        var bytes = new byte[(int)(seconds * AudioFormat.SampleRate) * AudioFormat.FrameBytes];
        for (var i = 0; i < bytes.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value);
        return bytes;
    }

    [Fact]
    public void SilenceHasRecordedDurationEvenWithoutPackets()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, Seconds(100));
        timeline.CommitSilence(Seconds(101));
        timeline.Stop(Seconds(102));
        Assert.Equal(AudioFormat.BytesPerSecond * 2, output.Length);
        Assert.All(output.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void PausedAudioAndTimeAreExcluded()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(1), 0, 0);
        timeline.Pause(Seconds(1));
        timeline.WritePacket(Audio(1, 9999), Seconds(5), 5 * AudioFormat.SampleRate);
        timeline.CommitSilence(Seconds(9));
        timeline.Resume(Seconds(10));
        timeline.WritePacket(Audio(1), Seconds(10), 10 * AudioFormat.SampleRate);
        timeline.Stop(Seconds(11));
        Assert.Equal(AudioFormat.BytesPerSecond * 2, output.Length);
        Assert.Equal(Audio(2), output.ToArray());
    }

    [Fact]
    public void ResumeTrimsPacketsCrossingPauseBoundary()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.Pause(Seconds(1));
        timeline.Resume(Seconds(10));
        timeline.WritePacket(Audio(1), Seconds(9.5), 456000);
        timeline.Stop(Seconds(10.5));
        Assert.Equal(AudioFormat.BytesPerSecond * 1.5, output.Length);
        Assert.Equal(Audio(.5), output.ToArray()[AudioFormat.BytesPerSecond..]);
    }

    [Fact]
    public void PacketGapBecomesSilenceAndNotCompressedTime()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, Seconds(20));
        timeline.WritePacket(Audio(.5), Seconds(20), 0);
        timeline.WritePacket(Audio(.5), Seconds(21.5), 72000);
        timeline.Stop(Seconds(22));
        Assert.Equal(AudioFormat.BytesPerSecond * 2, output.Length);
        Assert.All(output.ToArray()[96000..288000], b => Assert.Equal(0, b));
    }

    [Fact]
    public void DuplicatePacketDoesNotDuplicateAudio()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(1), 0, 0);
        timeline.WritePacket(Audio(1), 0, 0);
        Assert.Equal(AudioFormat.BytesPerSecond, output.Length);
    }

    [Fact]
    public void ContinuousToneIsBitExactDespitePacketTimestampJitter()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        var source = Tone(2);
        const int packetFrames = 480;
        for (var frame = 0; frame < AudioFormat.SampleRate * 2; frame += packetFrames)
        {
            var packet = frame / packetFrames;
            var jitter = packet == 0 ? 0 : packet % 2 == 0 ? 3 : -3;
            var ticks = (long)Math.Round((frame + jitter) *
                ((double)TimeSpan.TicksPerSecond / AudioFormat.SampleRate));
            timeline.WritePacket(source.AsSpan(frame * AudioFormat.FrameBytes,
                packetFrames * AudioFormat.FrameBytes), ticks, frame);
        }
        Assert.Equal(source, output.ToArray());
    }

    private static byte[] Tone(int seconds)
    {
        var bytes = new byte[seconds * AudioFormat.BytesPerSecond];
        for (var frame = 0; frame < seconds * AudioFormat.SampleRate; frame++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 997 * frame / AudioFormat.SampleRate) * 16000);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(frame * AudioFormat.FrameBytes), value);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(frame * AudioFormat.FrameBytes + 2), value);
        }
        return bytes;
    }

    [Theory]
    [InlineData(-0.0003)]
    [InlineData(0.0003)]
    public void ContinuousSamplesAreNotResampledToMatchWallClockDrift(double drift)
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, Seconds(100));
        var source = Tone(3);
        const int packetFrames = 480;
        for (var frame = 0; frame < AudioFormat.SampleRate * 3; frame += packetFrames)
        {
            var ticks = Seconds(100 + frame * (1 + drift) / AudioFormat.SampleRate);
            timeline.WritePacket(source.AsSpan(frame * AudioFormat.FrameBytes,
                packetFrames * AudioFormat.FrameBytes), ticks, frame);
        }
        Assert.Equal(source, output.ToArray());
    }

    [Fact]
    public void DevicePositionGapInsertsOnlyMissingFramesDespiteTimestampJitter()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(.5), 0, 0);
        timeline.WritePacket(Audio(.5), Seconds(.751), 36000);
        Assert.Equal(AudioFormat.BytesPerSecond * 1.25, output.Length);
        Assert.All(output.ToArray()[96000..144000], b => Assert.Equal(0, b));
        Assert.Equal(Audio(.5), output.ToArray()[144000..]);
    }

    [Fact]
    public void DriverDiscontinuityReanchorsAfterDevicePositionReset()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(.5), 0, 48000);
        timeline.WritePacket(Audio(.5), Seconds(1), 0, discontinuity: true);
        Assert.Equal(AudioFormat.BytesPerSecond * 1.5, output.Length);
        Assert.All(output.ToArray()[96000..192000], b => Assert.Equal(0, b));
        Assert.Equal(Audio(.5), output.ToArray()[192000..]);
    }

    [Fact]
    public void UnexplainedBackwardsPositionIsReportedInsteadOfDroppingAudio()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(.5), 0, 48000);
        Assert.Throws<InvalidOperationException>(() =>
            timeline.WritePacket(Audio(.5), Seconds(.5), 0));
    }

    [Fact]
    public void SilenceThenAudioReanchorsEvenIfDeviceClockStopped()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket(Audio(.5), 0, 0);
        timeline.CommitSilence(Seconds(2));
        timeline.WritePacket(Audio(.5), Seconds(2.5), 24000);
        Assert.Equal(AudioFormat.BytesPerSecond * 3, output.Length);
        Assert.All(output.ToArray()[96000..480000], b => Assert.Equal(0, b));
        Assert.Equal(Audio(.5), output.ToArray()[480000..]);
    }

    [Fact]
    public void EmptyPacketDoesNotSetTheClockAnchor()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.WritePacket([], 0, 0);
        timeline.WritePacket(Audio(.5), Seconds(1), 100000);
        Assert.Equal(AudioFormat.BytesPerSecond * 1.5, output.Length);
    }

    [Fact]
    public void StopWhilePausedDoesNotAppendPausedTime()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.Pause(Seconds(1));
        timeline.Stop(Seconds(20));
        timeline.Stop(Seconds(30));
        timeline.WritePacket(Audio(1), Seconds(30), 30 * AudioFormat.SampleRate);
        Assert.Equal(AudioFormat.BytesPerSecond, output.Length);
        Assert.Equal(RecordingState.Stopped, timeline.State);
    }

    [Fact]
    public void InvalidStateChangesAreRejected()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        Assert.Throws<InvalidOperationException>(() => timeline.Resume(0));
        timeline.Pause(0);
        Assert.Throws<InvalidOperationException>(() => timeline.Pause(0));
        timeline.Stop(0);
        Assert.Throws<InvalidOperationException>(() => timeline.Resume(0));
    }

    [Fact]
    public void PartialFramesAreRejected()
    {
        using var output = new MemoryStream();
        var timeline = new RecordingTimeline(output, 0);
        Assert.Throws<ArgumentException>(() => timeline.WritePacket(new byte[3], 0, 0));
        Assert.Throws<ArgumentException>(() => AudioFormat.Peak(new byte[3]));
    }

    [Fact]
    public void SilenceWritesStayBoundedForLongRecordings()
    {
        using var output = new CountingStream();
        var timeline = new RecordingTimeline(output, 0);
        timeline.Stop(Seconds(60 * 60 * 24));
        Assert.Equal(24L * 60 * 60 * AudioFormat.BytesPerSecond, output.Bytes);
        Assert.InRange(output.LargestWrite, 1, AudioFormat.BytesPerSecond / 10);
    }

    [Theory]
    [InlineData(short.MinValue, 1f)]
    [InlineData(0, 0f)]
    [InlineData(16384, .5f)]
    public void PeakUsesRealSignedSamples(short value, float expected)
    {
        Assert.Equal(expected, AudioFormat.Peak(Audio(.01, value)));
    }

    private sealed class CountingStream : Stream
    {
        public long Bytes { get; private set; }
        public int LargestWrite { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Bytes += buffer.Length;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
