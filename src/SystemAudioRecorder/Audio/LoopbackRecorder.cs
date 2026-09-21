using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SystemAudioRecorder.Core;
using SystemAudioRecorder.Core.Diagnostics;

namespace SystemAudioRecorder.Audio;

public sealed record CaptureResult(string? Path, Exception? Error, string StopReason = "unknown");

public sealed class LoopbackRecorder(AsyncDiagnosticLog? log = null, TimeSpan? stopAfter = null)
{
    private readonly RecordingStopSchedule stopSchedule = new(stopAfter);
    public StopTimerSnapshot StopTimer => stopSchedule.Snapshot;
    private enum CommandKind { Pause, Resume, Stop }
    private sealed record Command(CommandKind Kind, string Reason, long QueuedAt, TaskCompletionSource Completion);
    private readonly ConcurrentQueue<Command> commands = new();
    private readonly object commandGate = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureResult> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long recordedBytes;
    private int discontinuities;
    private float peak;
    private int hasStarted;
    private bool ended;
    private readonly Guid recordingId = Guid.NewGuid();
    private int pendingCommands;
    private int commandHighWatermark;
    private long workerHeartbeat = Stopwatch.GetTimestamp();
    private string stage = "created";
    public string RecordingId => recordingId.ToString("N");
    public string Stage => Volatile.Read(ref stage);
    public int PendingCommands => Volatile.Read(ref pendingCommands);
    public double WorkerHeartbeatAgeMs => Stopwatch.GetElapsedTime(Interlocked.Read(ref workerHeartbeat)).TotalMilliseconds;
    public Task<CaptureResult> Completion => finished.Task;
    public TimeSpan Duration => AudioFormat.Duration(Interlocked.Read(ref recordedBytes));
    public float TakePeak() => Interlocked.Exchange(ref peak, 0);
    public int Discontinuities => Volatile.Read(ref discontinuities);

    public Task StartAsync(string deviceId, RecoveryStore store)
    {
        if (Interlocked.Exchange(ref hasStarted, 1) != 0)
            throw new InvalidOperationException("Each recorder can only be started once.");
        Trace("capture.start_requested", $"endpointRef={DiagnosticIds.Endpoint(log?.SessionId ?? "", deviceId)}", durable: true);
        Trace("timer.configured", FormattableString.Invariant(
            $"enabled={stopAfter.HasValue} durationSeconds={stopAfter?.TotalSeconds ?? 0:F0} mode=elapsed_including_pauses"), durable: true);
        _ = Task.Factory.StartNew(() => Run(deviceId, store), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return started.Task;
    }

    public Task PauseAsync() => Send(CommandKind.Pause, "user_pause");
    public Task ResumeAsync() => Send(CommandKind.Resume, "user_resume");
    public Task StopAsync(string reason = "user_stop")
    {
        var stopReason = reason switch
        {
            "user_stop" => RecordingStopReason.UserStop,
            "window_close" => RecordingStopReason.WindowClose,
            _ => throw new ArgumentException("Unsupported stop reason.", nameof(reason))
        };
        return Send(CommandKind.Stop, reason, stopReason);
    }

    private Task Send(CommandKind kind, string reason, RecordingStopReason? stopReason = null)
    {
        lock (commandGate)
        {
            if (ended)
                return kind == CommandKind.Stop ? Task.CompletedTask :
                    Task.FromException(new InvalidOperationException("Capture has already stopped."));
            if (stopSchedule.StopReason != null)
                return kind == CommandKind.Stop ? finished.Task :
                    Task.FromException(new InvalidOperationException("Capture is stopping."));
            if (pendingCommands >= 16)
            {
                Trace("capture.command_rejected", $"command={kind} reason=queue_full", DiagnosticLevel.Warning);
                return Task.FromException(new InvalidOperationException("The recorder is busy processing commands."));
            }
            if (stopReason.HasValue && !stopSchedule.TryRequestStop(stopReason.Value))
                return finished.Task;
            var command = new Command(kind, reason, Stopwatch.GetTimestamp(),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            var pending = Interlocked.Increment(ref pendingCommands);
            commandHighWatermark = Math.Max(commandHighWatermark, pending);
            commands.Enqueue(command);
            Trace("capture.command_requested", $"command={kind} reason={reason} pending={pending}", durable: true);
            return command.Completion.Task;
        }
    }

    private static long NowTicks() =>
        (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    private void Run(string deviceId, RecoveryStore store)
    {
        string? path = null;
        Exception? error = null;
        Command? executing = null;
        RecordingTimeline? timeline = null;
        var health = new CaptureHealth();
        var runStarted = Stopwatch.GetTimestamp();
        var stopReason = "capture_failure";
        string? failureStage = null;
        long lastDevicePosition = 0;
        long lastQpcTicks = 0;
        var lastPacketFrames = 0;
        AudioClientBufferFlags lastPacketFlags = 0;
        try
        {
            SetStage("device.open");
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected playback device is no longer available.");
            using var client = device.AudioClient;
            var native = client.MixFormat;
            Trace("capture.native_format",
                $"sampleRate={native.SampleRate} bitsPerSample={native.BitsPerSample} channels={native.Channels} encoding={native.Encoding} blockAlign={native.BlockAlign}");
            var format = new WaveFormat(AudioFormat.SampleRate, AudioFormat.BitsPerSample, AudioFormat.Channels);
            // The Windows audio engine handles native float, surround downmix and sample-rate conversion.
            SetStage("device.initialize");
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality, 1_000_000, 0, format, Guid.Empty);
            var capture = client.AudioCaptureClient;
            var buffer = new byte[checked(client.BufferSize * format.BlockAlign)];
            Trace("capture.configured",
                $"sampleRate={format.SampleRate} bitsPerSample={format.BitsPerSample} channels={format.Channels} bufferFrames={client.BufferSize} bufferBytes={buffer.Length} mode=shared_loopback conversion=AutoConvertPcm_SrcDefaultQuality pollMs=10");
            SetStage("recovery.create");
            using var output = store.Create(recordingId);
            path = output.Name;
            Trace("recovery.created", "format=s16le_stereo_48000", durable: true);
            timeline = new RecordingTimeline(output, NowTicks());
            SetStage("device.start");
            client.Start();
            try
            {
                stopSchedule.Start();
                if (stopAfter.HasValue)
                    Trace("timer.started", FormattableString.Invariant(
                        $"durationSeconds={stopAfter.Value.TotalSeconds:F0} mode=elapsed_including_pauses"), durable: true);
                started.TrySetResult();
                Trace("capture.started", FormattableString.Invariant(
                    $"startupMs={Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds:F2}"), durable: true);
                var lastFlush = NowTicks();
                var lastPacketReceived = lastFlush;
                var lastHealth = lastFlush;
                var stopping = false;
                var firstPacket = true;
                while (!stopping)
                {
                    SetStage("capture.drain");
                    var packetsDrained = 0;
                    // Drain before commands, including pause/stop, so queued audio is not lost.
                    while (true)
                    {
                        if (CheckTimerExpiry(timeline.State)) break;
                        SetStage("capture.get_next_packet");
                        if (capture.GetNextPacketSize() == 0) break;
                        SetStage("capture.packet");
                        var pointer = capture.GetBuffer(out var frames, out var flags, out var devicePosition, out var timestamp);
                        var count = 0;
                        try
                        {
                            if (frames == 0) continue;
                            lastDevicePosition = devicePosition;
                            lastQpcTicks = timestamp;
                            lastPacketFrames = frames;
                            lastPacketFlags = flags;
                            count = checked(frames * format.BlockAlign);
                            if (count > buffer.Length)
                                throw new InvalidOperationException("The audio driver returned an oversized packet.");
                            if ((flags & AudioClientBufferFlags.TimestampError) != 0)
                                throw new InvalidOperationException("The audio driver returned an invalid timestamp. Recording stopped to protect timing.");
                            if (timestamp > NowTicks() + TimeSpan.TicksPerSecond)
                                throw new InvalidOperationException("The audio driver returned a timestamp in the future.");
                            if (!firstPacket && (flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                                Interlocked.Increment(ref discontinuities);
                            firstPacket = false;
                            if ((flags & AudioClientBufferFlags.Silent) != 0)
                                Array.Clear(buffer, 0, count);
                            else
                                Marshal.Copy(pointer, buffer, 0, count);
                        }
                        finally { capture.ReleaseBuffer(frames); }
                        lastPacketReceived = NowTicks();
                        health.Packet(frames, devicePosition, timestamp, lastPacketReceived,
                            (flags & AudioClientBufferFlags.Silent) != 0,
                            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                        packetsDrained++;
                        SetStage("recovery.write");
                        var writeStarted = Stopwatch.GetTimestamp();
                        timeline.WritePacket(buffer.AsSpan(0, count), timestamp, devicePosition,
                            (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                        health.WriteElapsed(Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds);
                        if (timeline.State == RecordingState.Recording)
                            Interlocked.Exchange(ref peak, Math.Max(peak, AudioFormat.Peak(buffer.AsSpan(0, count))));
                    }
                    health.Drain(packetsDrained);
                    if (CheckTimerExpiry(timeline.State))
                    {
                        timeline.Stop(NowTicks());
                        stopReason = "timer_elapsed";
                        stopping = true;
                        Trace("capture.command_completed",
                            $"command=Stop reason=timer_elapsed state={timeline.State} frames={timeline.FramesWritten} bytes={timeline.FramesWritten * AudioFormat.FrameBytes}",
                            durable: true);
                    }
                    while (commands.TryDequeue(out executing))
                    {
                        Interlocked.Decrement(ref pendingCommands);
                        SetStage("capture.command");
                        if (stopping)
                        {
                            Trace("capture.command_superseded", $"command={executing.Kind} reason=timer_elapsed");
                            executing.Completion.TrySetException(new InvalidOperationException("Capture stopped because the timer expired."));
                            executing = null;
                            continue;
                        }
                        switch (executing.Kind)
                        {
                            case CommandKind.Pause: timeline.Pause(NowTicks()); break;
                            case CommandKind.Resume: timeline.Resume(NowTicks()); break;
                            case CommandKind.Stop:
                                timeline.Stop(NowTicks());
                                stopReason = executing.Reason;
                                stopping = true;
                                break;
                        }
                        Flush(output, health, durable: false);
                        Interlocked.Exchange(ref recordedBytes, timeline.FramesWritten * format.BlockAlign);
                        Trace("capture.command_completed", FormattableString.Invariant(
                            $"command={executing.Kind} reason={executing.Reason} state={timeline.State} elapsedMs={Stopwatch.GetElapsedTime(executing.QueuedAt).TotalMilliseconds:F2} frames={timeline.FramesWritten} bytes={recordedBytes}"),
                            durable: true);
                        executing.Completion.TrySetResult();
                        executing = null;
                    }
                    // A silent endpoint may produce no packets. Keep a small latency allowance
                    // before committing zeros so the recording still contains real-time silence.
                    var now = NowTicks();
                    var silenceDelay = TimeSpan.TicksPerMillisecond * 250;
                    if (now - lastPacketReceived >= silenceDelay)
                    {
                        SetStage("recovery.silence");
                        timeline.CommitSilence(now - silenceDelay);
                    }
                    Interlocked.Exchange(ref recordedBytes, timeline.FramesWritten * format.BlockAlign);
                    if (NowTicks() - lastFlush >= TimeSpan.TicksPerSecond)
                    {
                        Flush(output, health, durable: false);
                        SetStage("device.check");
                        if (device.State != DeviceState.Active)
                            throw new InvalidOperationException("The playback device was disconnected or disabled.");
                        lastFlush = NowTicks();
                    }
                    if (now - lastHealth >= TimeSpan.TicksPerSecond * 10)
                    {
                        TraceHealth(health, timeline, now - lastPacketReceived);
                        lastHealth = now;
                    }
                    SetStage("capture.wait");
                    if (!stopping) Thread.Sleep(10);
                }
                Flush(output, health, durable: true);
            }
            catch
            {
                failureStage = Stage;
                throw;
            }
            finally
            {
                SetStage("device.stop");
                try { client.Stop(); }
                catch (Exception cleanupError) when (failureStage != null)
                {
                    log?.Write(DiagnosticLevel.Error, "capture.stop_cleanup_failed",
                        "An earlier capture failure is being propagated.", RecordingId,
                        exception: cleanupError, durable: true);
                }
            }
        }
        catch (Exception ex)
        {
            // All worker failures are delivered to the UI; the on-disk PCM is never removed here.
            error = ex;
            log?.Write(DiagnosticLevel.Error, "capture.failed",
                $"stage={failureStage ?? Stage} command={executing?.Kind.ToString() ?? "none"} recoveryCreated={path != null} lastDevicePosition={lastDevicePosition} lastQpcTicks={lastQpcTicks} lastPacketFrames={lastPacketFrames} lastPacketFlags={lastPacketFlags}",
                RecordingId, exception: ex, durable: true);
            started.TrySetException(ex);
            executing?.Completion.TrySetException(ex);
        }
        finally
        {
            stopSchedule.Complete();
            var timerResult = stopSchedule.Snapshot;
            if (timerResult.Duration.HasValue && timerResult.StopReason != RecordingStopReason.TimerElapsed)
                Trace("timer.cancelled", FormattableString.Invariant(
                    $"reason={stopReason} started={timerResult.Started} elapsedSeconds={timerResult.Elapsed.TotalSeconds:F3} remainingSeconds={timerResult.Remaining?.TotalSeconds ?? 0:F3}"),
                    durable: true);
            lock (commandGate)
            {
                ended = true;
                while (commands.TryDequeue(out var pending))
                {
                    Interlocked.Decrement(ref pendingCommands);
                    pending.Completion.TrySetException(error ?? new InvalidOperationException("Capture has stopped."));
                }
            }
            if (timeline != null) TraceHealth(health, timeline, 0);
            Trace("capture.finished", FormattableString.Invariant(
                $"reason={stopReason} success={error == null} recoveryCreated={path != null} elapsedMs={Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds:F2} frames={timeline?.FramesWritten ?? 0} bytes={(timeline?.FramesWritten ?? 0) * AudioFormat.FrameBytes} durationSeconds={AudioFormat.Duration((timeline?.FramesWritten ?? 0) * AudioFormat.FrameBytes).TotalSeconds:F3}"),
                error == null ? DiagnosticLevel.Info : DiagnosticLevel.Error, durable: true);
            SetStage("finished");
            finished.TrySetResult(new CaptureResult(path, error, stopReason));
        }
    }

    private bool CheckTimerExpiry(RecordingState state)
    {
        if (stopSchedule.TryExpire())
        {
            var timer = stopSchedule.Snapshot;
            Trace("timer.expired", FormattableString.Invariant(
                $"durationSeconds={timer.Duration!.Value.TotalSeconds:F0} elapsedSeconds={timer.Elapsed.TotalSeconds:F3} lateMs={(timer.Elapsed - timer.Duration.Value).TotalMilliseconds:F2} recordingState={state}"),
                durable: true);
        }
        return stopSchedule.StopReason == RecordingStopReason.TimerElapsed;
    }

    private void SetStage(string value)
    {
        Volatile.Write(ref stage, value);
        Interlocked.Exchange(ref workerHeartbeat, Stopwatch.GetTimestamp());
    }

    private void Flush(FileStream output, CaptureHealth health, bool durable)
    {
        SetStage(durable ? "recovery.durable_flush" : "recovery.flush");
        var start = Stopwatch.GetTimestamp();
        output.Flush(durable);
        health.FlushElapsed(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private void TraceHealth(CaptureHealth health, RecordingTimeline timeline, long packetAgeTicks) =>
        Trace("capture.health", health.Summary() + FormattableString.Invariant(
            $" state={timeline.State} outputFrames={timeline.FramesWritten} outputBytes={timeline.FramesWritten * AudioFormat.FrameBytes} insertedSilenceFrames={timeline.SilenceFramesWritten} overlapFramesSkipped={timeline.OverlapFramesSkipped} pausedFramesExcluded={timeline.PausedFramesExcluded} lastPacketAgeMs={packetAgeTicks / (double)TimeSpan.TicksPerMillisecond:F2} commandQueue={PendingCommands} commandQueueHighWatermark={commandHighWatermark}"));

    private void Trace(string name, string message, DiagnosticLevel level = DiagnosticLevel.Info, bool durable = false) =>
        log?.Write(level, name, message, RecordingId, durable: durable);
}
