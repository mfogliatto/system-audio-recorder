namespace SystemAudioRecorder.Core;

public enum RecordingStopReason { UserStop, WindowClose, TimerElapsed }

public sealed record StopTimerSnapshot(TimeSpan? Duration, bool Started, bool Completed,
    TimeSpan Elapsed, TimeSpan? Remaining, RecordingStopReason? StopReason);

// One instance per recording: UI commands and the capture worker compete for a
// single stop claim. Pausing audio deliberately does not change this clock.
public sealed class RecordingStopSchedule
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan? duration;
    private long startedAt;
    private bool started;
    private bool completed;
    private TimeSpan completedElapsed;
    private RecordingStopReason? stopReason;

    public RecordingStopSchedule(TimeSpan? duration, TimeProvider? clock = null)
    {
        if (duration.HasValue && (duration < StopAfterDuration.Minimum || duration > StopAfterDuration.Maximum))
            throw new ArgumentOutOfRangeException(nameof(duration), "Stop after must be between 1 second and 24 hours.");
        this.duration = duration;
        this.clock = clock ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (gate)
        {
            if (started || completed) throw new InvalidOperationException("The recording clock cannot be restarted.");
            startedAt = clock.GetTimestamp();
            started = true;
        }
    }

    public bool TryExpire()
    {
        lock (gate)
        {
            if (!started || completed || stopReason != null || !duration.HasValue || Elapsed() < duration.Value)
                return false;
            stopReason = RecordingStopReason.TimerElapsed;
            return true;
        }
    }

    public bool TryRequestStop(RecordingStopReason reason)
    {
        if (reason is not (RecordingStopReason.UserStop or RecordingStopReason.WindowClose))
            throw new ArgumentOutOfRangeException(nameof(reason), "Use TryExpire to request a timed stop.");
        lock (gate)
        {
            if (completed || stopReason != null) return false;
            stopReason = reason;
            return true;
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            if (completed) return;
            completedElapsed = Elapsed();
            completed = true;
        }
    }

    public StopTimerSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var elapsed = Elapsed();
                TimeSpan? remaining = duration.HasValue ?
                    elapsed < duration.Value ? duration.Value - elapsed : TimeSpan.Zero : null;
                return new(duration, started, completed, elapsed, remaining, stopReason);
            }
        }
    }

    public RecordingStopReason? StopReason { get { lock (gate) return stopReason; } }

    private TimeSpan Elapsed() => completed ? completedElapsed :
        started ? clock.GetElapsedTime(startedAt, clock.GetTimestamp()) : TimeSpan.Zero;
}
