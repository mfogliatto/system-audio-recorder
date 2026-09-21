using System.Buffers.Binary;
using NAudio.Wave;
using SystemAudioRecorder.Audio;
using SystemAudioRecorder.Core;

namespace SystemAudioRecorder.Tests;

public sealed class RecoveryAndExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SystemAudioRecorder.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> createdFiles = [];

    private string MakePcm(bool silent = false, bool partialFrame = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.pcm");
        createdFiles.Add(path);
        using var file = File.Create(path);
        var data = new byte[AudioFormat.BytesPerSecond * 2 + (partialFrame ? 1 : 0)];
        if (!silent)
        {
            for (var frame = 0; frame < AudioFormat.SampleRate * 2; frame++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * 440 * frame / AudioFormat.SampleRate) * 12000);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(frame * 4), value);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(frame * 4 + 2), value);
            }
        }
        file.Write(data);
        return path;
    }

    private string Destination()
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.mp3");
        createdFiles.Add(path);
        return path;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SyntheticAudioEncodesAndDecodesAsStereoMp3(bool silent, bool partialFrame)
    {
        var pcm = MakePcm(silent, partialFrame);
        var destination = Destination();
        Mp3Exporter.Export(pcm, destination, CancellationToken.None);
        Assert.True(File.Exists(pcm));
        using var decoded = new MediaFoundationReader(destination);
        Assert.Equal(2, decoded.WaveFormat.Channels);
        Assert.Equal(48000, decoded.WaveFormat.SampleRate);
        Assert.InRange(decoded.TotalTime.TotalSeconds, 1.95, 2.15);
        var buffer = new byte[16384];
        long bytes = 0;
        var peak = 0f;
        int read;
        while ((read = decoded.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytes += read;
            peak = Math.Max(peak, AudioFormat.Peak(buffer.AsSpan(0, read)));
        }
        Assert.InRange(AudioFormat.Duration(bytes).TotalSeconds, 1.95, 2.15);
        if (silent) Assert.InRange(peak, 0, .001f);
        else Assert.InRange(peak, .2f, .6f);
        using var mp3 = new Mp3FileReaderBase(destination, _ => new DummyDecompressor());
        Assert.Equal(320000, mp3.Mp3WaveFormat.AverageBytesPerSecond * 8);
    }

    [Fact]
    public void CancellationKeepsPcmAndExistingDestination()
    {
        var pcm = MakePcm();
        var destination = Destination();
        File.WriteAllText(destination, "existing destination");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Mp3Exporter.Export(pcm, destination, cancellation.Token));
        Assert.Equal("existing destination", File.ReadAllText(destination));
        Assert.True(File.Exists(pcm));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp.mp3"));
    }

    [Fact]
    public void CancellationDuringEncodingKeepsPcmAndCleansTemporaryOutput()
    {
        var pcm = MakePcm();
        var destination = Destination();
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Mp3Exporter.Export(pcm, destination,
            cancellation.Token, new CancelOnProgress(cancellation)));
        Assert.True(File.Exists(pcm));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp.mp3"));
    }

    [Fact]
    public void FailedReplacementLeavesExistingFileAndRecoveryUntouched()
    {
        var pcm = MakePcm();
        var destination = Destination();
        File.WriteAllText(destination, "keep");
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => Mp3Exporter.Export(pcm, destination, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal("keep", File.ReadAllText(destination));
        Assert.True(File.Exists(pcm));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp.mp3"));
    }

    [Fact]
    public void DestinationMustBeMp3AndCannotOverwriteRecovery()
    {
        var pcm = MakePcm();
        var length = new FileInfo(pcm).Length;
        Assert.Throws<ArgumentException>(() => Mp3Exporter.Export(pcm, pcm, CancellationToken.None));
        Assert.Equal(length, new FileInfo(pcm).Length);
    }

    [Fact]
    public void FailedExportRetainsPcm()
    {
        var pcm = MakePcm();
        Assert.ThrowsAny<Exception>(() => Mp3Exporter.Export(pcm,
            Path.Combine(directory, "does-not-exist", "recording.mp3"), CancellationToken.None));
        Assert.True(File.Exists(pcm));
    }

    [Fact]
    public void EmptyRecordingDoesNotOverwriteDestination()
    {
        var pcm = MakePcm();
        File.WriteAllBytes(pcm, []);
        var destination = Destination();
        File.WriteAllText(destination, "keep");
        Assert.Throws<InvalidOperationException>(() => Mp3Exporter.Export(pcm, destination, CancellationToken.None));
        Assert.Equal("keep", File.ReadAllText(destination));
    }

    [Fact]
    public void RecoverySurvivesNewStoreInstanceAndCanBeExplicitlyDiscarded()
    {
        var store = new RecoveryStore(directory);
        string path;
        using (var file = store.Create())
        {
            path = file.Name;
            createdFiles.Add(path);
            file.Write(new byte[AudioFormat.BytesPerSecond]);
        }
        var afterRestart = new RecoveryStore(directory);
        var recovered = Assert.Single(afterRestart.List());
        Assert.Equal(TimeSpan.FromSeconds(1), recovered.Duration);
        afterRestart.Delete(recovered);
        Assert.Empty(afterRestart.List());
    }

    [Fact]
    public void RecoveryCannotDiscardFilesOutsideItsDirectory()
    {
        var store = new RecoveryStore(directory);
        Assert.Throws<InvalidOperationException>(() =>
            store.Delete(new RecoveryRecording(Path.Combine(directory, "..", "outside.pcm"), 0)));
    }

    [Fact]
    public void WindowsResamplerConvertsSyntheticFloatSurroundToTargetPcm()
    {
        var samples = new float[4800 * 6];
        Array.Fill(samples, .125f);
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var source = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 6));
        using var converted = new MediaFoundationResampler(source, new WaveFormat(48000, 16, 2));
        var result = new byte[AudioFormat.BytesPerSecond];
        var count = converted.Read(result, 0, result.Length);
        Assert.InRange(count, 18000, 20000);
        Assert.InRange(AudioFormat.Peak(result.AsSpan(0, count)), .01f, 1f);
    }

    public void Dispose()
    {
        foreach (var path in createdFiles) File.Delete(path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }

    private sealed class DummyDecompressor : IMp3FrameDecompressor
    {
        public WaveFormat OutputFormat { get; } = new(48000, 16, 2);
        public int DecompressFrame(Mp3Frame frame, byte[] dest, int destOffset) => throw new NotSupportedException();
        public void Reset() { }
        public void Dispose() { }
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    {
        public void Report(double value) => cancellation.Cancel();
    }
}
