using System.Globalization;
using System.Text.Json;
using SystemAudioRecorder.Core.Diagnostics;

namespace SystemAudioRecorder.Tests;

public sealed class DiagnosticLogTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RotationBoundsFilesAndPreservesJsonAndCorrelation(int retained)
    {
        using var scope = new LogDirectory();
        Directory.CreateDirectory(scope.Path);
        var unrelated = Path.Combine(scope.Path, "recorder.notes.log");
        File.WriteAllText(unrelated, "leave me alone");
        await using var log = new AsyncDiagnosticLog(scope.Path, "session-one",
            new() { MaxFileBytes = 2048, RetainedFiles = retained });
        Assert.True(await log.Ready);
        for (var index = 0; index < 25; index++)
        {
            Assert.True(log.Write(DiagnosticLevel.Info, "recording.started", new string('a', 700),
                "recording-one", "export-one", durable: true));
            Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        var files = scope.Logs();
        Assert.Equal(retained, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2048));
        Assert.Equal("leave me alone", File.ReadAllText(unrelated));
        Assert.All(scope.Records(), entry =>
        {
            Assert.Equal("session-one", entry.GetProperty("sessionId").GetString());
            Assert.Equal("recording-one", entry.GetProperty("recordingId").GetString());
            Assert.Equal("export-one", entry.GetProperty("exportId").GetString());
            Assert.Equal("Info", entry.GetProperty("level").GetString());
            Assert.True(entry.GetProperty("threadId").GetInt32() > 0);
            var timestamp = DateTimeOffset.Parse(entry.GetProperty("timestampUtc").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        });
        Assert.Equal(25, log.Status.AcceptedEvents);
        Assert.Equal(25, log.Status.WrittenEvents);
    }

    [Fact]
    public async Task ExceptionsRetainTypesInnerChainsStacksAndMaskHomePaths()
    {
        using var scope = new LogDirectory();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session");
        Assert.True(await log.Ready);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exception = MakeException(home);
        Assert.True(log.Write(DiagnosticLevel.Error, "export.failed", $"Cannot open {home}\\private.wav",
            exception: exception));
        Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        var record = Assert.Single(scope.Records());
        var formatted = record.GetProperty("exception").GetString()!;
        Assert.Contains("System.InvalidOperationException", formatted);
        Assert.Contains("System.IO.IOException", formatted);
        Assert.Contains("System.ArgumentException", formatted);
        Assert.Contains(nameof(MakeException), formatted);
        Assert.Contains(nameof(ThrowInner), formatted);
        Assert.Contains("%USERPROFILE%", formatted);
        Assert.DoesNotContain(home, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", record.GetProperty("message").GetString());
        Assert.DoesNotContain(home, record.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedExceptionMessagesCannotHideInnerTypesOrStacks()
    {
        using var scope = new LogDirectory();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session");
        Assert.True(await log.Ready);
        var huge = new string('x', 100_000);
        Exception error;
        try
        {
            try { ThrowInner(huge); }
            catch (Exception inner) { throw new InvalidOperationException(huge, inner); }
            throw new InvalidOperationException("Expected ThrowInner to throw.");
        }
        catch (Exception outer) { error = outer; }
        Assert.True(log.Write(DiagnosticLevel.Error, "large-failure", exception: error));
        Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        var formatted = Assert.Single(scope.Records()).GetProperty("exception").GetString()!;
        Assert.InRange(formatted.Length, 1, 4096);
        Assert.Contains("System.InvalidOperationException", formatted);
        Assert.Contains("System.ArgumentException", formatted);
        Assert.Contains(nameof(OversizedExceptionMessagesCannotHideInnerTypesOrStacks), formatted);
        Assert.Contains(nameof(ThrowInner), formatted);
    }

    [Fact]
    public async Task ConcurrentProducersAreBoundedAndCriticalCapacityIsReserved()
    {
        using var scope = new LogDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", new() { Capacity = 16 },
            new() { BeforeWrite = () => { entered.Set(); release.Wait(); } });
        try
        {
            Assert.True(await log.Ready);
            Assert.True(log.Write(DiagnosticLevel.Info, "first"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Parallel.For(0, 1000, i => log.Write(DiagnosticLevel.Info, "progress", i.ToString()));
            Assert.Equal(14, log.Status.PendingEvents);
            Assert.True(log.Write(DiagnosticLevel.Error, "error"));
            Assert.True(log.Write(DiagnosticLevel.Fatal, "fatal", durable: true));
            Assert.False(log.Write(DiagnosticLevel.Fatal, "full"));
            var status = log.Status;
            Assert.Equal(16, status.AcceptedEvents);
            Assert.Equal(16, status.PendingEvents);
            Assert.Equal(16, status.QueueHighWatermark);
            Assert.Equal(988, status.DroppedEvents);
            release.Set();
            Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(16, log.Status.WrittenEvents);
            Assert.Equal(0, log.Status.PendingEvents);
            Assert.Contains(scope.Records(), r => r.GetProperty("eventName").GetString() == "diagnostic.events_dropped" &&
                r.GetProperty("message").GetString()!.Contains("total=988"));
            Assert.False(log.Write(DiagnosticLevel.Info, "after-shutdown"));
            Assert.Equal(989, log.Status.DroppedEvents);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2097152)]
    public async Task LargeEventsRemainValidAndBounded(long maxFileBytes)
    {
        using var scope = new LogDirectory();
        var huge = new string('\u0001', 100_000) + "\ud800";
        await using var log = new AsyncDiagnosticLog(scope.Path, huge, new() { MaxFileBytes = maxFileBytes });
        Assert.True(await log.Ready);
        Assert.True(log.Write(DiagnosticLevel.Error, huge, huge, huge, huge,
            new InvalidOperationException(huge)));
        Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        var record = Assert.Single(scope.Records());
        Assert.True(record.GetProperty("message").GetString()!.Length <= 2048);
        Assert.True(record.GetProperty("exception").GetString()!.Length <= 4096);
        Assert.InRange(new FileInfo(Assert.Single(scope.Logs())).Length, 1, Math.Min(48 * 1024, maxFileBytes));
    }

    [Fact]
    public async Task InitializationFailureIsObservableAndRejectedWritesAreCounted()
    {
        using var scope = new LogDirectory();
        Directory.CreateDirectory(scope.Path);
        var blocker = Path.Combine(scope.Path, "not-a-directory");
        File.WriteAllText(blocker, "owned test file");
        await using var log = new AsyncDiagnosticLog(blocker, "session");
        Assert.False(await log.Ready);
        Assert.False(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.False(log.Status.IsReady);
        Assert.NotNull(log.Status.Failure);
        Assert.False(log.Write(DiagnosticLevel.Error, "rejected"));
        Assert.Equal(1, log.Status.DroppedEvents);
        Assert.False(await log.FlushAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ExclusiveLockPreventsASecondLoggerAndIsReleasedOnShutdown()
    {
        using var scope = new LogDirectory();
        await using var first = new AsyncDiagnosticLog(scope.Path, "first");
        Assert.True(await first.Ready);
        await using var second = new AsyncDiagnosticLog(scope.Path, "second");
        Assert.False(await second.Ready);
        Assert.Contains("IOException", second.Status.Failure);
        Assert.False(await second.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.True(first.Write(DiagnosticLevel.Info, "still-healthy"));
        Assert.True(await first.ShutdownAsync(TimeSpan.FromSeconds(5)));
        await using var third = new AsyncDiagnosticLog(scope.Path, "third");
        Assert.True(await third.Ready);
        Assert.True(await third.ShutdownAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReadOnlyCurrentFileFailsInitializationAndReleasesDirectoryLock()
    {
        using var scope = new LogDirectory();
        Directory.CreateDirectory(scope.Path);
        var path = Path.Combine(scope.Path, "recorder.log");
        File.WriteAllText(path, "");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await using var log = new AsyncDiagnosticLog(scope.Path, "session");
            Assert.False(await log.Ready);
            Assert.False(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("UnauthorizedAccessException", log.Status.Failure);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        await using var next = new AsyncDiagnosticLog(scope.Path, "next");
        Assert.True(await next.Ready);
    }

    [Fact]
    public async Task FlushBarrierIsBoundedAndShutdownEventuallyDrainsAfterTimeout()
    {
        using var scope = new LogDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", null,
            new() { BeforeWrite = () => { entered.Set(); release.Wait(); } });
        try
        {
            Assert.True(await log.Ready);
            Assert.True(log.Write(DiagnosticLevel.Info, "before-barrier"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var barrier = log.FlushAsync(TimeSpan.FromMilliseconds(50));
            var competing = Enumerable.Range(0, 100).Select(_ => log.FlushAsync(TimeSpan.FromSeconds(5))).ToArray();
            Assert.All(competing, task => Assert.True(task.IsCompletedSuccessfully));
            Assert.All(await Task.WhenAll(competing), result => Assert.False(result));
            Assert.False(await barrier);
            Assert.False(await log.FlushAsync(TimeSpan.FromSeconds(5)));
            Assert.True(log.Write(DiagnosticLevel.Info, "after-barrier"));
            Assert.False(await log.ShutdownAsync(TimeSpan.FromMilliseconds(50)));
            Assert.False(log.Write(DiagnosticLevel.Error, "after-shutdown-request"));
            release.Set();
            var results = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => log.ShutdownAsync(TimeSpan.FromSeconds(5))));
            Assert.All(results, result => Assert.True(result));
            Assert.Equal(2, log.Status.WrittenEvents);
            Assert.Equal(0, log.Status.PendingEvents);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DurableWritesAndBarriersActuallyReachTheWorkerFlush()
    {
        using var scope = new LogDirectory();
        var flushes = 0;
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", null,
            new() { AfterFlush = durable => { if (durable) Interlocked.Increment(ref flushes); } });
        Assert.True(await log.Ready);
        Assert.True(log.Write(DiagnosticLevel.Fatal, "fatal", durable: true));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.True(Volatile.Read(ref flushes) >= 2);
        Assert.Single(scope.Records());
    }

    [Fact]
    public async Task IdleWriterFlushesWithoutAProducerBarrier()
    {
        using var scope = new LogDirectory();
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", null,
            new() { AfterFlush = durable => { if (!durable) flushed.TrySetResult(); } });
        Assert.True(await log.Ready);
        Assert.True(log.Write(DiagnosticLevel.Info, "idle-health"));
        await flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(scope.Records());
    }

    [Fact]
    public async Task FlushCoversEarlierEventsWithoutWaitingForLaterEvents()
    {
        using var scope = new LogDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", null,
            new()
            {
                BeforeWrite = () =>
                {
                    if (Interlocked.Increment(ref calls) == 2)
                    {
                        entered.Set();
                        release.Wait();
                    }
                }
            });
        try
        {
            Assert.True(await log.Ready);
            Assert.True(log.Write(DiagnosticLevel.Info, "before"));
            var barrier = log.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.True(log.Write(DiagnosticLevel.Info, "after"));
            Assert.True(await barrier);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("before", Assert.Single(scope.Records()).GetProperty("eventName").GetString());
            Assert.Equal(1, log.Status.PendingEvents);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalIsBoundedAndTheWriterEventuallyReleasesItsLock(bool asynchronous)
    {
        using var scope = new LogDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var log = new AsyncDiagnosticLog(scope.Path, "session", null,
            new() { BeforeWrite = () => { entered.Set(); release.Wait(); } });
        try
        {
            Assert.True(await log.Ready);
            Assert.True(log.Write(DiagnosticLevel.Info, "blocked"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var disposal = Task.Run(async () =>
            {
                if (asynchronous)
                    await log.DisposeAsync();
                else
                    log.Dispose();
            });
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, log.Status.PendingEvents);
            Assert.False(log.Write(DiagnosticLevel.Error, "no-more"));
            release.Set();
            Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
            await using var next = new AsyncDiagnosticLog(scope.Path, "next");
            Assert.True(await next.Ready);
        }
        finally
        {
            release.Set();
            await log.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ExceptionFormattingFailuresDoNotEscapeTheProducer()
    {
        using var scope = new LogDirectory();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session");
        Assert.True(await log.Ready);
        Assert.False(log.Write(DiagnosticLevel.Error, "bad-exception", exception: new BrokenException()));
        Assert.False(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("broken exception formatter", log.Status.Failure);
        Assert.Equal(0, log.Status.AcceptedEvents);
        Assert.Equal(1, log.Status.DroppedEvents);
        Assert.False(log.Write(DiagnosticLevel.Error, "disabled"));
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task WorkerFailuresDisableWritesAndAccountForAbandonedEvents(string stage)
    {
        using var scope = new LogDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Throw()
        {
            entered.Set();
            release.Wait();
            throw new UnauthorizedAccessException($"Denied {home}\\private");
        }
        var hooks = new DiagnosticLogTestHooks
        {
            BeforeInitialize = stage == "initialize" ? Throw : null,
            BeforeWrite = stage == "write" ? Throw : null,
            BeforeFlush = stage == "flush" ? Throw : null
        };
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", null, hooks);
        try
        {
            if (stage != "initialize")
                Assert.True(await log.Ready);
            Assert.True(log.Write(DiagnosticLevel.Error, "one", durable: true));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(log.Write(DiagnosticLevel.Error, "two"));
            release.Set();
            Assert.False(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
            Assert.False(log.Status.IsReady);
            Assert.Contains("UnauthorizedAccessException", log.Status.Failure);
            Assert.Contains("%USERPROFILE%", log.Status.Failure);
            Assert.DoesNotContain(home, log.Status.Failure!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, log.Status.AcceptedEvents);
            Assert.Equal(2, log.Status.DroppedEvents);
            Assert.Equal(0, log.Status.WrittenEvents);
            Assert.Equal(0, log.Status.PendingEvents);
            Assert.False(log.Write(DiagnosticLevel.Fatal, "rejected"));
            Assert.Equal(3, log.Status.DroppedEvents);
            if (stage == "initialize")
                Assert.False(await log.Ready);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task RotationFailureIsObservableRatherThanFallingBack()
    {
        using var scope = new LogDirectory();
        await using var log = new AsyncDiagnosticLog(scope.Path, "session",
            new() { MaxFileBytes = 1024, RetainedFiles = 2 });
        Assert.True(await log.Ready);
        Directory.CreateDirectory(Path.Combine(scope.Path, "recorder.1.log"));
        Assert.True(log.Write(DiagnosticLevel.Info, "first", new string('x', 700)));
        Assert.True(await log.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.True(log.Write(DiagnosticLevel.Info, "second", new string('x', 700)));
        Assert.False(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(log.Status.Failure);
        Assert.Equal(1, log.Status.WrittenEvents);
        Assert.Equal(1, log.Status.DroppedEvents);
    }

    [Fact]
    public async Task StartupPrunesOnlyOwnedRotationsAndRepairsATornTail()
    {
        using var scope = new LogDirectory();
        Directory.CreateDirectory(scope.Path);
        File.WriteAllText(Path.Combine(scope.Path, "recorder.9.log"), "{}\n");
        File.WriteAllText(Path.Combine(scope.Path, "recorder.09.log"), "not ours");
        File.WriteAllText(Path.Combine(scope.Path, "recorder.log"), "{}\n{\"broken\":");
        await using var log = new AsyncDiagnosticLog(scope.Path, "session", new() { RetainedFiles = 2 });
        Assert.True(await log.Ready);
        Assert.True(log.Write(DiagnosticLevel.Info, "new-record"));
        Assert.True(await log.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(Path.Combine(scope.Path, "recorder.9.log")));
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(scope.Path, "recorder.09.log")));
        var lines = File.ReadAllLines(Path.Combine(scope.Path, "recorder.log"));
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var parsed = JsonDocument.Parse(line);
        }
    }

    private static Exception MakeException(string home)
    {
        try
        {
            try { ThrowInner(home); }
            catch (Exception inner) { throw new IOException("middle", inner); }
        }
        catch (Exception middle)
        {
            try { throw new InvalidOperationException($"outer {home.Replace('\\', '/')}/private", middle); }
            catch (Exception outer) { return outer; }
        }
        throw new InvalidOperationException("Expected ThrowInner to throw.");
    }

    private static void ThrowInner(string home) => throw new ArgumentException($"inner {home}\\secret");

    private sealed class BrokenException : Exception
    {
        public override string Message => throw new InvalidOperationException("broken exception formatter");
    }

    private sealed class LogDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory,
            "diagnostic-log-tests", Guid.NewGuid().ToString("N"));

        public string[] Logs() => Directory.GetFiles(Path, "recorder*.log")
            .Where(path => System.IO.Path.GetFileName(path) == "recorder.log" ||
                int.TryParse(System.IO.Path.GetFileNameWithoutExtension(path).AsSpan("recorder.".Length), out _))
            .ToArray();

        public JsonElement[] Records() => Logs().SelectMany(ReadLines).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

        private static string[] ReadLines(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
