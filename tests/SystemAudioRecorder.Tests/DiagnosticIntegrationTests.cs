using System.Text.Json;
using SystemAudioRecorder.Audio;
using SystemAudioRecorder.Core;
using SystemAudioRecorder.Core.Diagnostics;
using SystemAudioRecorder.Diagnostics;

namespace SystemAudioRecorder.Tests;

public sealed class DiagnosticIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SystemAudioRecorder.DiagnosticsTests", Guid.NewGuid().ToString("N"));

    public DiagnosticIntegrationTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void SessionMarkerDistinguishesCleanFromUnconfirmedExitAndHoldsExclusiveWriter()
    {
        var first = Guid.NewGuid().ToString("N");
        using (var journal = new SessionJournal(directory, first, "1.0.2"))
        {
            Assert.Null(journal.Previous);
            Assert.Throws<IOException>(() => new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2"));
        }
        using (var restarted = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2"))
        {
            Assert.NotNull(restarted.Previous);
            Assert.Equal(first, restarted.Previous.SessionId);
            Assert.False(restarted.Previous.CleanExit);
            restarted.MarkCleanExit();
        }
        using var cleanRestart = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2");
        Assert.True(cleanRestart.Previous!.CleanExit);
    }

    [Theory]
    [InlineData("{\"partiallyWritten\":")]
    [InlineData("{\"SessionId\":\"invalid\"}")]
    public void DamagedSessionMarkerIsReportedNotInterpretedAsClean(string content)
    {
        File.WriteAllText(Path.Combine(directory, "session-state.json"), content);
        using var journal = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2");
        Assert.Null(journal.Previous);
        Assert.NotNull(journal.PreviousReadError);
    }

    [Fact]
    public async Task NormalLifecyclePersistsStartExitAndCleanSessionMarker()
    {
        var runtime = new DiagnosticRuntime(directory);
        await runtime.InitializeAsync();
        Assert.Null(runtime.Warning);
        Assert.Null(runtime.PreviousSessionNotice);
        Assert.True(await runtime.EndSessionAsync(0));
        var entries = ReadLogs();
        Assert.Contains(entries, row => Event(row) == "app.start" &&
            row.GetProperty("message").GetString()!.Contains("processArchitecture="));
        Assert.Contains(entries, row => Event(row) == "app.exit");
        using var journal = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2");
        Assert.True(journal.Previous!.CleanExit);
    }

    [Fact]
    public async Task FatalReportingPersistsExceptionButNeverMarksSessionClean()
    {
        var runtime = new DiagnosticRuntime(directory);
        await runtime.InitializeAsync();
        runtime.ReportUnhandled("fatal.synthetic", new InvalidOperationException("synthetic failure",
            new IOException("synthetic inner")), fatal: true);
        Assert.True(await runtime.EndSessionAsync(0));
        var fatal = Assert.Single(ReadLogs(), row => Event(row) == "fatal.synthetic");
        var exception = fatal.GetProperty("exception").GetString()!;
        Assert.Contains("InvalidOperationException", exception);
        Assert.Contains("synthetic inner", exception);
        var restarted = new DiagnosticRuntime(directory);
        await restarted.InitializeAsync();
        Assert.Contains("did not confirm", restarted.PreviousSessionNotice!);
        Assert.True(await restarted.EndSessionAsync(0));
        Assert.Contains(ReadLogs(), row => Event(row) == "session.previous_unclean");
    }

    [Fact]
    public async Task UnobservedTaskReportingIsAnErrorNotAForcedFatalExit()
    {
        var runtime = new DiagnosticRuntime(directory);
        await runtime.InitializeAsync();
        runtime.ReportUnhandled("task.unobserved", new AggregateException(new IOException("synthetic")), fatal: false);
        Assert.True(await runtime.EndSessionAsync(0));
        using var journal = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2");
        Assert.True(journal.Previous!.CleanExit);
        Assert.Contains(ReadLogs(), row => Event(row) == "task.unobserved");
    }

    [Fact]
    public async Task MissingMarkerCannotDiagnoseAnOlderAppSessionRetroactively()
    {
        var runtime = new DiagnosticRuntime(directory);
        await runtime.InitializeAsync();
        Assert.Null(runtime.PreviousSessionNotice);
        Assert.True(await runtime.EndSessionAsync(0));
        Assert.Contains(ReadLogs(), row => Event(row) == "session.no_previous_marker");
    }

    [Fact]
    public async Task ActiveOperationPreventsClaimingACleanExit()
    {
        var runtime = new DiagnosticRuntime(directory);
        await runtime.InitializeAsync();
        runtime.SetExport("synthetic-active-export");
        Assert.True(await runtime.EndSessionAsync(0));
        using var journal = new SessionJournal(directory, Guid.NewGuid().ToString("N"), "1.0.2");
        Assert.False(journal.Previous!.CleanExit);
    }

    [Fact]
    public async Task FailedDiagnosticsAreVisibleButDoNotPreventSyntheticAudioExport()
    {
        var blocked = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blocked, "blocked");
        var runtime = new DiagnosticRuntime(blocked);
        await runtime.InitializeAsync();
        Assert.NotNull(runtime.Warning);
        var pcm = Path.Combine(directory, "synthetic.pcm");
        var mp3 = Path.Combine(directory, "synthetic.mp3");
        File.WriteAllBytes(pcm, new byte[AudioFormat.BytesPerSecond]);
        Mp3Exporter.Export(pcm, mp3, CancellationToken.None, log: runtime.Log);
        Assert.True(new FileInfo(mp3).Length > 0);
        Assert.True(File.Exists(pcm));
        Assert.False(await runtime.EndSessionAsync(0));
    }

    [Fact]
    public async Task ExportLifecycleCorrelatesSuccessCancellationAndFailureWithoutLoggingPaths()
    {
        var id = Guid.NewGuid().ToString("N");
        var pcm = Path.Combine(directory, $"2026-09-19_00-00-00_{id}.pcm");
        var destination = Path.Combine(directory, "personal-destination-name.mp3");
        File.WriteAllBytes(pcm, new byte[AudioFormat.BytesPerSecond]);
        await using var log = new AsyncDiagnosticLog(directory, Guid.NewGuid().ToString("N"));
        Assert.True(await log.Ready);
        Mp3Exporter.Export(pcm, destination, CancellationToken.None, log: log, exportId: "success");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Mp3Exporter.Export(pcm, destination, cancellation.Token, log: log, exportId: "cancel"));
        Assert.Throws<ArgumentException>(() =>
            Mp3Exporter.Export(pcm, pcm, CancellationToken.None, log: log, exportId: "failure"));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(3)));
        var entries = ReadLogs();
        Assert.Contains(entries, row => Event(row) == "export.completed" && ExportId(row) == "success");
        Assert.Contains(entries, row => Event(row) == "export.cancelled" && ExportId(row) == "cancel");
        Assert.Contains(entries, row => Event(row) == "export.failed" && ExportId(row) == "failure");
        foreach (var row in entries.Where(row => Event(row).StartsWith("export.", StringComparison.Ordinal)))
        {
            Assert.Equal(id, row.GetProperty("recordingId").GetString());
            Assert.DoesNotContain(directory, row.GetRawText());
            Assert.DoesNotContain("personal-destination-name", row.GetRawText());
        }
        Assert.True(File.Exists(pcm));
    }

    [Fact]
    public async Task InvalidDeviceStartupFailureHasCorrelationAndDoesNotCreateRecoveryAudio()
    {
        await using var log = new AsyncDiagnosticLog(directory, Guid.NewGuid().ToString("N"));
        Assert.True(await log.Ready);
        var recorder = new LoopbackRecorder(log);
        var store = new RecoveryStore(Path.Combine(directory, "unused-recovery"));
        // An intentionally nonexistent endpoint is rejected before any capture client starts.
        await Assert.ThrowsAnyAsync<Exception>(() => recorder.StartAsync("synthetic-nonexistent-render-endpoint", store));
        var result = await recorder.Completion;
        Assert.NotNull(result.Error);
        Assert.Null(result.Path);
        Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(3)));
        var entries = ReadLogs();
        Assert.Contains(entries, row => Event(row) == "capture.start_requested");
        Assert.Contains(entries, row => Event(row) == "capture.failed" &&
            row.GetProperty("recordingId").GetString() == recorder.RecordingId);
        Assert.Contains(entries, row => Event(row) == "capture.finished");
    }

    [Fact]
    public async Task ConfiguredTimerIsLoggedAndCancelledWhenCaptureCannotStart()
    {
        await using var log = new AsyncDiagnosticLog(directory, Guid.NewGuid().ToString("N"));
        Assert.True(await log.Ready);
        var recorder = new LoopbackRecorder(log, TimeSpan.FromSeconds(5));
        var store = new RecoveryStore(Path.Combine(directory, "unused-recovery"));
        await Assert.ThrowsAnyAsync<Exception>(() => recorder.StartAsync("synthetic-nonexistent-render-endpoint", store));
        await recorder.Completion;
        Assert.True(recorder.StopTimer.Completed);
        Assert.False(recorder.StopTimer.Started);
        Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(3)));
        var entries = ReadLogs();
        Assert.Contains(entries, row => Event(row) == "timer.configured" &&
            row.GetProperty("recordingId").GetString() == recorder.RecordingId &&
            row.GetProperty("message").GetString()!.Contains("durationSeconds=5"));
        Assert.Contains(entries, row => Event(row) == "timer.cancelled");
        Assert.DoesNotContain(entries, row => Event(row) is "timer.started" or "timer.expired");
    }

    [Fact]
    public void IdentifiersCorrelateRecoveryWithoutRevealingPathsOrDeviceIdentity()
    {
        var id = Guid.NewGuid();
        var store = new RecoveryStore(directory);
        string path;
        using (var stream = store.Create(id)) path = stream.Name;
        Assert.Equal(id.ToString("N"), DiagnosticIds.Recording(path));
        Assert.Equal(DiagnosticIds.Endpoint("session1", "device"), DiagnosticIds.Endpoint("session1", "device"));
        Assert.NotEqual(DiagnosticIds.Endpoint("session1", "device"), DiagnosticIds.Endpoint("session2", "device"));
        Assert.DoesNotContain("private-name", DiagnosticIds.Recording(@"C:\private-name\private-name.pcm"));
    }

    [Fact]
    public void HealthAggregatesJitterGapsDiscontinuitiesAndTimingWithoutAudioContent()
    {
        var health = new CaptureHealth();
        health.Packet(480, 1000, 0, 10000, false, false);
        health.Packet(480, 1480, 100500, 1200000, true, false);
        health.Packet(480, 2440, 300000, 1300000, false, false);
        health.Packet(480, 0, 500000, 1400000, false, true);
        health.Drain(4);
        health.WriteElapsed(8);
        health.FlushElapsed(12);
        Assert.Equal(4, health.Packets);
        Assert.Equal(1920, health.InputFrames);
        Assert.Equal(480, health.SilentInputFrames);
        Assert.Equal(480, health.DeviceGapFrames);
        Assert.Equal(1, health.Discontinuities);
        Assert.Equal(1, health.DeliveryGapsOver100Ms);
        Assert.InRange(health.MaxQpcDeviceOffsetFrames, 2.39, 2.41);
        Assert.Equal(4, health.MaxPacketsPerDrain);
        Assert.Contains("maxWriteMs=8.00", health.Summary());
        Assert.Contains("maxFlushMs=12.00", health.Summary());
    }

    [Fact]
    public void TimelineReportsInsertedSilenceSkippedOverlapAndPausedFrames()
    {
        using var stream = new MemoryStream();
        var timeline = new RecordingTimeline(stream, 0);
        timeline.WritePacket(new byte[480 * AudioFormat.FrameBytes], 0, 0);
        timeline.WritePacket(new byte[480 * AudioFormat.FrameBytes], 0, 0);
        timeline.Pause(TimeSpan.TicksPerSecond);
        timeline.WritePacket(new byte[480 * AudioFormat.FrameBytes], TimeSpan.TicksPerSecond, 480);
        Assert.Equal(480, timeline.OverlapFramesSkipped);
        Assert.Equal(48000 - 480, timeline.SilenceFramesWritten);
        Assert.Equal(480, timeline.PausedFramesExcluded);
        Assert.Equal(AudioFormat.BytesPerSecond, stream.Length);
    }

    private List<JsonElement> ReadLogs() => Directory.EnumerateFiles(directory, "recorder*.log")
        .SelectMany(ReadSharedLines).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }).ToList();

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string Event(JsonElement row) => row.GetProperty("eventName").GetString()!;
    private static string? ExportId(JsonElement row) => row.GetProperty("exportId").GetString();

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}
