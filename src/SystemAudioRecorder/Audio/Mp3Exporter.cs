using System.IO;
using System.Diagnostics;
using NAudio.Wave;
using SystemAudioRecorder.Core;
using SystemAudioRecorder.Core.Diagnostics;

namespace SystemAudioRecorder.Audio;

public static class Mp3Exporter
{
    public const int BitRate = 320000;

    public static void Export(string pcmPath, string destination, CancellationToken cancellation,
        IProgress<double>? progress = null, AsyncDiagnosticLog? log = null, string? exportId = null)
    {
        exportId ??= Guid.NewGuid().ToString("N");
        var recordingId = DiagnosticIds.Recording(pcmPath);
        var started = Stopwatch.GetTimestamp();
        log?.Write(DiagnosticLevel.Info, "export.started", $"encoder=MediaFoundation bitrate={BitRate}",
            recordingId, exportId, durable: true);
        try
        {
            var bytes = ExportCore(pcmPath, destination, cancellation, progress, log, recordingId, exportId);
            log?.Write(DiagnosticLevel.Info, "export.completed", FormattableString.Invariant(
                $"outputBytes={bytes} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}"),
                recordingId, exportId, durable: true);
        }
        catch (OperationCanceledException ex)
        {
            log?.Write(DiagnosticLevel.Info, "export.cancelled", FormattableString.Invariant(
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} recoveryRetained=true"),
                recordingId, exportId, ex, durable: true);
            throw;
        }
        catch (Exception ex)
        {
            log?.Write(DiagnosticLevel.Error, "export.failed", FormattableString.Invariant(
                $"elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} recoveryRetained=true"),
                recordingId, exportId, ex, durable: true);
            throw;
        }
    }

    private static long ExportCore(string pcmPath, string destination, CancellationToken cancellation,
        IProgress<double>? progress, AsyncDiagnosticLog? log, string recordingId, string exportId)
    {
        var fullDestination = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(fullDestination), ".mp3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a destination with the .mp3 extension.", nameof(destination));
        if (string.Equals(Path.GetFullPath(pcmPath), fullDestination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The destination cannot replace the recovery file.", nameof(destination));
        var temporary = Path.Combine(Path.GetDirectoryName(fullDestination)!,
            $".{Path.GetFileNameWithoutExtension(fullDestination)}.{Guid.NewGuid():N}.tmp.mp3");
        Exception? exportError = null;
        try
        {
            using var input = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length < AudioFormat.FrameBytes)
                throw new InvalidOperationException("This recording contains no audio frames.");
            log?.Write(DiagnosticLevel.Info, "export.input", FormattableString.Invariant(
                $"inputBytes={input.Length} frames={input.Length / AudioFormat.FrameBytes} trailingBytesIgnored={input.Length % AudioFormat.FrameBytes} durationSeconds={AudioFormat.Duration(input.Length).TotalSeconds:F3}"),
                recordingId, exportId);
            var provider = new PcmFileProvider(input, cancellation, progress, log, recordingId, exportId);
            log?.Write(DiagnosticLevel.Info, "export.encode_start", recordingId: recordingId, exportId: exportId);
            MediaFoundationEncoder.EncodeToMp3(provider, temporary, BitRate);
            cancellation.ThrowIfCancellationRequested();
            var outputBytes = new FileInfo(temporary).Length;
            if (outputBytes == 0)
                throw new IOException("The MP3 encoder produced an empty file.");
            log?.Write(DiagnosticLevel.Info, "export.destination_commit", recordingId: recordingId, exportId: exportId);
            File.Move(temporary, fullDestination, overwrite: true);
            return outputBytes;
        }
        catch (Exception ex)
        {
            exportError = ex;
            throw;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Could not remove the partial export at {temporary}. " +
                    $"Original export result: {exportError?.Message ?? "MP3 saved"}.", cleanupError);
            }
        }
    }

    private sealed class PcmFileProvider(FileStream stream, CancellationToken cancellation,
        IProgress<double>? progress, AsyncDiagnosticLog? log, string recordingId, string exportId) : IWaveProvider
    {
        private readonly long length = stream.Length / AudioFormat.FrameBytes * AudioFormat.FrameBytes;
        private readonly Stopwatch progressClock = Stopwatch.StartNew();
        private readonly Stopwatch healthClock = Stopwatch.StartNew();
        public WaveFormat WaveFormat { get; } =
            new(AudioFormat.SampleRate, AudioFormat.BitsPerSample, AudioFormat.Channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = length - stream.Position;
            var requested = (int)Math.Min(remaining, count / AudioFormat.FrameBytes * AudioFormat.FrameBytes);
            if (requested == 0) return 0;
            stream.ReadExactly(buffer.AsSpan(offset, requested));
            if (healthClock.Elapsed.TotalSeconds >= 10)
            {
                log?.Write(DiagnosticLevel.Info, "export.progress",
                    $"readBytes={stream.Position} totalBytes={length}", recordingId, exportId);
                healthClock.Restart();
            }
            if (progressClock.ElapsedMilliseconds >= 100 || stream.Position == length)
            {
                progress?.Report((double)stream.Position / length);
                progressClock.Restart();
            }
            return requested;
        }
    }
}
