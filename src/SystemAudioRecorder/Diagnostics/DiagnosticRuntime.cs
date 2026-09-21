using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SystemAudioRecorder.Audio;
using SystemAudioRecorder.Core;
using SystemAudioRecorder.Core.Diagnostics;

namespace SystemAudioRecorder.Diagnostics;

public sealed class DiagnosticRuntime
{
    private SessionJournal? journal;
    private Timer? healthTimer;
    private LoopbackRecorder? recording;
    private string? exportId;
    private string? sessionWarning;
    private long uiHeartbeat = Stopwatch.GetTimestamp();
    private int sampling;
    private int fatalReported;
    private int samplingFailed;
    private double? previousCpuMs;
    private long previousHealthTime = Stopwatch.GetTimestamp();
    public AsyncDiagnosticLog Log { get; }
    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    public string? PreviousSessionNotice { get; private set; }
    public string? Warning => Log.Status.Failure ?? Volatile.Read(ref sessionWarning);
    public string? RecordingId => Volatile.Read(ref recording)?.RecordingId;
    public string? ExportId => Volatile.Read(ref exportId);

    public DiagnosticRuntime(string directory)
    {
        Log = new AsyncDiagnosticLog(directory, Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        Log.Write(DiagnosticLevel.Info, "app.start",
            $"version={Version} runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} osArchitecture={RuntimeInformation.OSArchitecture} processArchitecture={RuntimeInformation.ProcessArchitecture} processId={Environment.ProcessId}",
            durable: true);
        try
        {
            if (await Log.Ready.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                journal = new SessionJournal(Log.DirectoryPath, Log.SessionId, Version);
                if (journal.PreviousReadError != null)
                {
                    PreviousSessionNotice = journal.PreviousReadError;
                    Log.Write(DiagnosticLevel.Warning, "session.previous_unknown", PreviousSessionNotice, durable: true);
                }
                else if (journal.Previous is { } previous)
                {
                    Log.Write(previous.CleanExit ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
                        previous.CleanExit ? "session.previous_clean" : "session.previous_unclean",
                        $"previousSessionId={previous.SessionId} startedUtc={previous.StartedUtc:O} version={previous.Version} cleanExit={previous.CleanExit}",
                        durable: true);
                    if (!previous.CleanExit)
                        PreviousSessionNotice = "The previous logged session did not confirm a clean exit. Recovery audio is kept.";
                }
                else
                {
                    Log.Write(DiagnosticLevel.Info, "session.no_previous_marker",
                        "No earlier diagnostic session marker; exits from older app versions cannot be diagnosed retroactively.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            sessionWarning = "Session-exit detection is unavailable: " + ex.GetType().Name;
            Log.Write(DiagnosticLevel.Error, "session.marker_failed", sessionWarning, exception: ex, durable: true);
        }
        healthTimer = new Timer(_ => SampleHealth(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void UiHeartbeat() => Interlocked.Exchange(ref uiHeartbeat, Stopwatch.GetTimestamp());
    public void SetRecording(LoopbackRecorder? value) => Volatile.Write(ref recording, value);
    public void SetExport(string? value) => Volatile.Write(ref exportId, value);

    private void SampleHealth()
    {
        if (Interlocked.Exchange(ref sampling, 1) != 0) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(previousHealthTime, now).TotalMilliseconds;
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            var cpuSampleReady = previousCpuMs.HasValue;
            var cpuPercent = (cpuMs - (previousCpuMs ?? cpuMs)) / Math.Max(1, elapsedMs) / Environment.ProcessorCount * 100;
            previousCpuMs = cpuMs;
            previousHealthTime = now;
            var status = Log.Status;
            var current = Volatile.Read(ref recording);
            var uiAge = Stopwatch.GetElapsedTime(Interlocked.Read(ref uiHeartbeat), now).TotalMilliseconds;
            var workerAge = current?.WorkerHeartbeatAgeMs ?? 0;
            ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
            var memory = GC.GetGCMemoryInfo();
            Log.Write(uiAge > 5000 || workerAge > 5000 ? DiagnosticLevel.Warning : DiagnosticLevel.Info,
                "app.health", FormattableString.Invariant(
                    $"version={Version} workingSetBytes={process.WorkingSet64} privateBytes={process.PrivateMemorySize64} managedBytes={GC.GetTotalMemory(false)} gcHeapBytes={memory.HeapSizeBytes} gcMemoryLoadBytes={memory.MemoryLoadBytes} gcHighMemoryThresholdBytes={memory.HighMemoryLoadThresholdBytes} gcGen2Collections={GC.CollectionCount(2)} handles={process.HandleCount} threads={process.Threads.Count} cpuPercent={cpuPercent:F2} cpuSampleReady={cpuSampleReady} threadPoolPending={ThreadPool.PendingWorkItemCount} availableWorkers={availableWorkers} uiHeartbeatAgeMs={uiAge:F0} captureWorkerAgeMs={workerAge:F0} captureStage={current?.Stage ?? "idle"} captureCommandQueue={current?.PendingCommands ?? 0} logPending={status.PendingEvents} logHighWatermark={status.QueueHighWatermark} logDropped={status.DroppedEvents}"),
                RecordingId, ExportId);
        }
        catch (Exception ex)
        {
            // An optional sampler must not take down the recorder; expose failure once.
            if (Interlocked.Exchange(ref samplingFailed, 1) == 0)
            {
                sessionWarning = "Resource health sampling failed; other diagnostics may still be available.";
                Log.Write(DiagnosticLevel.Error, "app.health_failed", exception: ex);
            }
        }
        finally { Volatile.Write(ref sampling, 0); }
    }

    public void ReportUnhandled(string source, Exception exception, bool fatal)
    {
        var firstFatal = fatal && Interlocked.Exchange(ref fatalReported, 1) == 0;
        var accepted = Log.Write(fatal ? DiagnosticLevel.Fatal : DiagnosticLevel.Error, source,
            $"terminating={fatal}", RecordingId, ExportId, exception, durable: true);
        if (firstFatal)
        {
            var flushed = Log.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (!accepted || !flushed) sessionWarning = "Fatal diagnostics could not be flushed before termination.";
        }
    }

    public async Task<bool> EndSessionAsync(int exitCode)
    {
        var samplerStopped = true;
        if (healthTimer != null)
        {
            try
            {
                await healthTimer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                samplerStopped = false;
                sessionWarning = "Resource sampler did not finish within the shutdown deadline.";
                Log.Write(DiagnosticLevel.Warning, "app.health_shutdown_timeout", durable: true);
            }
        }
        var clean = samplerStopped && Volatile.Read(ref fatalReported) == 0 && recording == null && exportId == null && exitCode == 0;
        var exitAccepted = Log.Write(clean ? DiagnosticLevel.Info : DiagnosticLevel.Warning, "app.exit",
            $"exitCode={exitCode} cleanCandidate={clean}", RecordingId, ExportId, durable: true);
        var flushed = await Log.ShutdownAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false) && exitAccepted;
        try
        {
            if (clean && flushed) journal?.MarkCleanExit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sessionWarning = "Could not persist the clean-exit marker: " + ex.GetType().Name;
            flushed = false;
        }
        finally { journal?.Dispose(); }
        return flushed;
    }
}
