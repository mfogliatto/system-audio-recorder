using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemAudioRecorder.Core.Diagnostics;

public enum DiagnosticLevel { Info, Warning, Error, Fatal }

public sealed record DiagnosticLogOptions
{
    public int Capacity { get; init; } = 512;
    public long MaxFileBytes { get; init; } = 2 * 1024 * 1024;
    public int RetainedFiles { get; init; } = 5;
}

public sealed record DiagnosticLogStatus(
    bool IsReady, string? Failure, long AcceptedEvents, long WrittenEvents,
    long DroppedEvents, int PendingEvents, int QueueHighWatermark);

/// <summary>
/// A bounded, nonblocking producer and one filesystem writer. Pending includes the in-flight
/// event. Accepted/Written count producer events only; Dropped includes rejected writes and
/// accepted events abandoned after a failure. Internal drop summaries are not producer events.
/// Written means the entire record and any requested durable flush succeeded; failed events can
/// leave partial bytes. IsReady is false after shutdown or failure; Ready reports initialization
/// only. Drop summaries are emitted periodically and at barriers/shutdown when the sink is healthy.
/// </summary>
/// <remarks>
/// Strings are redacted and capped: identifiers 128, message 2,048, exception 4,096 characters.
/// Exception formatting shares its budget among at most 16 exceptions, including aggregate
/// branches, reserving space for each type and available stack even when messages are oversized. JSON records,
/// including their newline, never exceed min(48 KiB, MaxFileBytes); smaller files further truncate
/// strings. MaxFileBytes must be at least 1,024. Retention includes the current file.
/// One eighth of capacity (at least one slot when capacity exceeds one) is reserved for warning,
/// error, fatal and durable events. Even critical writes can be rejected: check Write or Status.
/// At most one flush barrier is admitted; competing FlushAsync calls return false immediately.
/// A timeout does not cancel a queued barrier or shutdown. Disposal waits at most two seconds;
/// a blocked OS write cannot be cancelled, but the writer exits once it becomes writable.
/// Live readers must share write/delete access. Oversized owned logs are discarded at startup
/// when retention limits shrink; an incomplete current-file tail is removed before appending.
/// </remarks>
public sealed class AsyncDiagnosticLog : IDisposable, IAsyncDisposable
{
    private const int MaximumRecordBytes = 48 * 1024;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _gate = new();
    private readonly Queue<WorkItem> _queue = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource<bool> _ready = NewCompletion();
    private readonly TaskCompletionSource<bool> _shutdown = NewCompletion();
    private readonly DiagnosticLogOptions _options;
    private readonly DiagnosticLogTestHooks? _hooks;
    private readonly string[] _homePaths;
    private readonly int _ordinaryLimit;
    private TaskCompletionSource<bool>? _barrier;
    private bool _accepting = true;
    private bool _initialized;
    private bool _stopped;
    private string? _failure;
    private long _accepted;
    private long _written;
    private long _dropped;
    private int _pending;
    private int _highWatermark;
    // The following fields belong exclusively to the writer.
    private FileStream? _file;
    private long _fileBytes;
    private long _reportedDrops;

    public AsyncDiagnosticLog(string directory, string sessionId, DiagnosticLogOptions? options = null)
        : this(directory, sessionId, options, null) { }

    internal AsyncDiagnosticLog(string directory, string sessionId, DiagnosticLogOptions? options,
        DiagnosticLogTestHooks? hooks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(sessionId);
        _options = options ?? new();
        if (_options.Capacity < 1 || _options.Capacity > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be between 1 and 1,000,000.");
        if (_options.MaxFileBytes < 1024 || _options.RetainedFiles < 1 || _options.RetainedFiles > 1000)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFileBytes must be >= 1024; RetainedFiles must be 1..1000.");
        DirectoryPath = Path.GetFullPath(directory);
        _homePaths = new[]
            {
                Environment.GetEnvironmentVariable("USERPROFILE"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => new[] { p!, p!.Replace('\\', '/') })
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToArray();
        SessionId = Clean(sessionId, 128);
        _hooks = hooks;
        _ordinaryLimit = Math.Max(1, _options.Capacity - Math.Max(1, _options.Capacity / 8));
        _ = Task.Run(RunWriterAsync);
    }

    public string DirectoryPath { get; }
    public string SessionId { get; }
    public Task<bool> Ready => _ready.Task;
    public DiagnosticLogStatus Status
    {
        get
        {
            lock (_gate)
                return new(_initialized && !_stopped && _failure is null, _failure,
                    _accepted, _written, _dropped, _pending, _highWatermark);
        }
    }

    public bool Write(DiagnosticLevel level, string eventName, string message = "",
        string? recordingId = null, string? exportId = null, Exception? exception = null,
        bool durable = false)
    {
        lock (_gate)
        {
            var critical = durable || level >= DiagnosticLevel.Warning;
            if (!_accepting || _failure is not null ||
                _pending >= (critical ? _options.Capacity : _ordinaryLimit))
            {
                _dropped++;
                return false;
            }
            try
            {
                var entry = new LogEntry(
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    level.ToString(), Clean(eventName, 128), SessionId,
                    recordingId is null ? null : Clean(recordingId, 128),
                    exportId is null ? null : Clean(exportId, 128),
                    Environment.CurrentManagedThreadId, Clean(message, 2048),
                    exception is null ? null : FormatException(exception));
                _queue.Enqueue(new(entry, durable, null));
                _accepted++;
                _pending++;
                _highWatermark = Math.Max(_highWatermark, _pending);
                Signal();
                return true;
            }
            catch (Exception error)
            {
                _dropped++;
                Fail(error);
                return false;
            }
        }
    }

    public Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (!ValidTimeout(timeout))
            return Task.FromResult(false);
        lock (_gate)
        {
            if (!_accepting || _failure is not null || _barrier is not null)
                return Task.FromResult(false);
            var barrier = NewCompletion();
            _barrier = barrier;
            _queue.Enqueue(new(null, true, barrier));
            Signal();
            return WaitBoundedAsync(barrier.Task, timeout);
        }
    }

    public Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            _accepting = false;
            Signal();
        }
        return ValidTimeout(timeout) ? WaitBoundedAsync(_shutdown.Task, timeout) : Task.FromResult(false);
    }

    public void Dispose() => ShutdownAsync(DisposeTimeout).GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await ShutdownAsync(DisposeTimeout).ConfigureAwait(false);

    private static bool ValidTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout.TotalMilliseconds <= uint.MaxValue - 1;

    private static async Task<bool> WaitBoundedAsync(Task<bool> completion, TimeSpan timeout)
    {
        try { return await completion.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        // All signalers hold _gate; only the single writer consumes this binary wakeup.
        if (!_stopped && _wake.CurrentCount == 0)
            _wake.Release();
    }

    private void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_failure is null)
            {
                try { _failure = FormatException(error, 1280); }
                catch { _failure = "Diagnostic sink failed; exception details were unavailable."; }
            }
            _accepting = false;
            _ready.TrySetResult(false);
            _barrier?.TrySetResult(false);
            Signal();
        }
    }

    private async Task RunWriterAsync()
    {
        FileStream? directoryLock = null;
        try
        {
            _hooks?.BeforeInitialize?.Invoke();
            Directory.CreateDirectory(DirectoryPath);
            directoryLock = new FileStream(Path.Combine(DirectoryPath, "recorder.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            PruneOwnedFiles();
            OpenCurrent();
            lock (_gate)
            {
                if (_failure is null)
                {
                    _initialized = true;
                    _ready.TrySetResult(true);
                }
            }
            var lastFlush = Stopwatch.GetTimestamp();
            while (true)
            {
                WorkItem? item;
                lock (_gate)
                {
                    if (_failure is not null)
                        break;
                    item = _queue.Count > 0 ? _queue.Dequeue() : null;
                    if (item is null && !_accepting)
                        break;
                }
                if (item is not null)
                {
                    if (item.Entry is not null)
                    {
                        _hooks?.BeforeWrite?.Invoke();
                        WriteRecord(item.Entry);
                        if (item.Durable)
                            FlushFile(true);
                        lock (_gate)
                        {
                            _written++;
                            _pending--;
                        }
                    }
                    else
                    {
                        WriteDropSummary();
                        FlushFile(true);
                        lock (_gate)
                        {
                            item.Barrier!.TrySetResult(_failure is null);
                            _barrier = null;
                        }
                    }
                }
                if (Stopwatch.GetElapsedTime(lastFlush) >= TimeSpan.FromSeconds(1))
                {
                    WriteDropSummary();
                    FlushFile(false);
                    lastFlush = Stopwatch.GetTimestamp();
                }
                if (item is null)
                    await _wake.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            lock (_gate)
            {
                if (_failure is not null)
                    return;
            }
            WriteDropSummary();
            FlushFile(true);
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            // A close can itself fail. Release exclusivity even if closing the data file fails.
            try { _file?.Dispose(); }
            catch (Exception error) { Fail(error); }
            try { directoryLock?.Dispose(); }
            catch (Exception error) { Fail(error); }
            lock (_gate)
            {
                _dropped += _pending;
                _pending = 0;
                _queue.Clear();
                _barrier?.TrySetResult(false);
                _barrier = null;
                _accepting = false;
                _stopped = true;
                _ready.TrySetResult(false);
                _shutdown.TrySetResult(_failure is null);
                _wake.Dispose();
            }
        }
    }

    private void PruneOwnedFiles()
    {
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "recorder.*.log"))
        {
            var name = Path.GetFileName(path);
            var suffix = name.AsSpan("recorder.".Length, name.Length - "recorder.".Length - ".log".Length);
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index > 0 && name == $"recorder.{index}.log" &&
                (index >= _options.RetainedFiles || new FileInfo(path).Length > _options.MaxFileBytes))
                File.Delete(path);
        }
        var current = Path.Combine(DirectoryPath, "recorder.log");
        if (File.Exists(current) && new FileInfo(current).Length > _options.MaxFileBytes)
            File.Delete(current);
    }

    private void OpenCurrent()
    {
        _file = new FileStream(Path.Combine(DirectoryPath, "recorder.log"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _fileBytes = _file.Length;
        // Do not append to a torn record left by an unclean process exit.
        if (_fileBytes > 0)
        {
            _file.Position = _fileBytes - 1;
            if (_file.ReadByte() != '\n')
            {
                long end = _fileBytes - 1;
                while (end >= 0)
                {
                    _file.Position = end;
                    if (_file.ReadByte() == '\n')
                        break;
                    end--;
                }
                _file.SetLength(end + 1);
                _fileBytes = end + 1;
            }
        }
        _file.Position = _fileBytes;
    }

    private void Rotate()
    {
        FlushFile(true);
        _file!.Dispose();
        _file = null;
        if (_options.RetainedFiles == 1)
            File.Delete(Path.Combine(DirectoryPath, "recorder.log"));
        else
        {
            File.Delete(Path.Combine(DirectoryPath, $"recorder.{_options.RetainedFiles - 1}.log"));
            for (var index = _options.RetainedFiles - 2; index >= 1; index--)
            {
                var source = Path.Combine(DirectoryPath, $"recorder.{index}.log");
                if (File.Exists(source))
                    File.Move(source, Path.Combine(DirectoryPath, $"recorder.{index + 1}.log"));
            }
            File.Move(Path.Combine(DirectoryPath, "recorder.log"), Path.Combine(DirectoryPath, "recorder.1.log"));
        }
        OpenCurrent();
    }

    private void WriteRecord(LogEntry entry)
    {
        var limit = Math.Min(MaximumRecordBytes, _options.MaxFileBytes);
        byte[] encoded;
        while (true)
        {
            encoded = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            if (encoded.Length + 1 <= limit)
                break;
            entry = entry with
            {
                EventName = Halve(entry.EventName), SessionId = Halve(entry.SessionId),
                RecordingId = entry.RecordingId is null ? null : Halve(entry.RecordingId),
                ExportId = entry.ExportId is null ? null : Halve(entry.ExportId),
                Message = Halve(entry.Message),
                Exception = entry.Exception is null ? null : Halve(entry.Exception)
            };
        }
        if (_fileBytes + encoded.Length + 1 > _options.MaxFileBytes)
            Rotate();
        _file!.Write(encoded);
        _file.WriteByte((byte)'\n');
        _fileBytes += encoded.Length + 1;
    }

    private static string Halve(string value) =>
        value.Length <= 1 ? "" : value[..(value.Length / 2)];

    private void FlushFile(bool durable)
    {
        _hooks?.BeforeFlush?.Invoke();
        _file!.Flush(durable);
        _hooks?.AfterFlush?.Invoke(durable);
    }

    private void WriteDropSummary()
    {
        long dropped;
        lock (_gate) dropped = _dropped;
        if (dropped == _reportedDrops)
            return;
        WriteRecord(new(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "Warning", "diagnostic.events_dropped", SessionId, null, null,
            Environment.CurrentManagedThreadId,
            FormattableString.Invariant($"Dropped events: total={dropped}, sinceLastSummary={dropped - _reportedDrops}"),
            null));
        _reportedDrops = dropped;
    }

    private string Clean(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var result = new StringBuilder(Math.Min(value.Length, limit));
        var index = 0;
        while (index < value.Length && result.Length < limit)
        {
            string? match = null;
            foreach (var home in _homePaths)
            {
                if (value.AsSpan(index).StartsWith(home, StringComparison.OrdinalIgnoreCase))
                {
                    match = home;
                    break;
                }
            }
            if (match is not null)
            {
                const string replacement = "%USERPROFILE%";
                result.Append(replacement.AsSpan(0, Math.Min(replacement.Length, limit - result.Length)));
                index += match.Length;
            }
            else
                result.Append(value[index++]);
        }
        if (index < value.Length && result.Length >= 1)
            result[^1] = '…';
        return result.ToString();
    }

    private string FormatException(Exception error, int limit = 4096)
    {
        var exceptions = new List<Exception>(16);
        Collect(error);
        const string separator = "\n---> ";
        var budget = (limit - separator.Length * (exceptions.Count - 1)) / exceptions.Count;
        var builder = new StringBuilder(limit);
        foreach (var current in exceptions)
        {
            if (builder.Length > 0)
                builder.Append(separator);
            var type = Clean(current.GetType().FullName ?? current.GetType().Name, Math.Min(256, budget / 3));
            var remaining = budget - type.Length - 3;
            var rawStack = current.StackTrace ?? "";
            var stack = Clean(rawStack, remaining);
            // Share the budget before formatting messages so an oversized outer message cannot
            // hide inner causes or all the stack frames.
            var messageBudget = Math.Max(Math.Min(512, remaining / 3), remaining - stack.Length);
            var message = Clean(current.Message, messageBudget);
            stack = Clean(rawStack, remaining - message.Length);
            builder.Append(type).Append(": ").Append(message).Append('\n').Append(stack);
        }
        return builder.ToString();

        void Collect(Exception current)
        {
            if (exceptions.Count == 16)
                return;
            exceptions.Add(current);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (exceptions.Count == 16)
                        break;
                    Collect(inner);
                }
            }
            else if (current.InnerException is not null)
                Collect(current.InnerException);
        }
    }

    private sealed record WorkItem(LogEntry? Entry, bool Durable, TaskCompletionSource<bool>? Barrier);

    private sealed record LogEntry(
        [property: JsonPropertyName("timestampUtc")] string TimestampUtc,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("eventName")] string EventName,
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("recordingId")] string? RecordingId,
        [property: JsonPropertyName("exportId")] string? ExportId,
        [property: JsonPropertyName("threadId")] int ThreadId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("exception")] string? Exception);
}

internal sealed class DiagnosticLogTestHooks
{
    internal Action? BeforeInitialize { get; init; }
    internal Action? BeforeWrite { get; init; }
    internal Action? BeforeFlush { get; init; }
    internal Action<bool>? AfterFlush { get; init; }
}
