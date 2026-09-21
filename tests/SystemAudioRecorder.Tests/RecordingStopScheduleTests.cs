using SystemAudioRecorder.Core;

namespace SystemAudioRecorder.Tests;

public sealed class RecordingStopScheduleTests
{
    [Fact]
    public void DisabledTimerNeverExpiresButManualStopStillWorks()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(null, clock);
        timer.Start();
        clock.Advance(TimeSpan.FromDays(10));
        Assert.Null(timer.Snapshot.Remaining);
        Assert.False(timer.TryExpire());
        Assert.True(timer.TryRequestStop(RecordingStopReason.UserStop));
        Assert.Equal(RecordingStopReason.UserStop, timer.StopReason);
    }

    [Fact]
    public void DeadlineStartsOnSuccessfulStartNotWhenConfigured()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(10), clock);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(timer.TryExpire());
        Assert.False(timer.Snapshot.Started);
        Assert.Equal(TimeSpan.FromSeconds(10), timer.Snapshot.Remaining);
        timer.Start();
        Assert.Equal(TimeSpan.Zero, timer.Snapshot.Elapsed);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(timer.TryExpire());
        Assert.Equal(TimeSpan.FromSeconds(1), timer.Snapshot.Remaining);
    }

    [Fact]
    public void PausedAudioTimeIsExcludedButCountdownContinuesAndStopsWhilePaused()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(5), clock);
        using var output = new MemoryStream();
        var audio = new RecordingTimeline(output, 0);
        timer.Start();
        clock.Advance(TimeSpan.FromSeconds(1));
        audio.Pause(TimeSpan.TicksPerSecond);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(1), timer.Snapshot.Remaining);
        Assert.False(timer.TryExpire());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(timer.TryExpire());
        audio.Stop(TimeSpan.TicksPerSecond * 5);
        Assert.Equal(RecordingStopReason.TimerElapsed, timer.StopReason);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.Snapshot.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(1), AudioFormat.Duration(output.Length));
    }

    [Fact]
    public void ExactBoundaryExpiresOnce()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(1), clock);
        timer.Start();
        clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        Assert.False(timer.TryExpire());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(TimeSpan.Zero, timer.Snapshot.Remaining);
        Assert.True(timer.TryExpire());
        Assert.False(timer.TryExpire());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(timer.TryExpire());
    }

    [Fact]
    public void DelayedChecksDoNotExtendOrRestartTheDeadline()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(5), clock);
        timer.Start();
        clock.Advance(TimeSpan.FromSeconds(80));
        Assert.Equal(TimeSpan.Zero, timer.Snapshot.Remaining);
        Assert.True(timer.TryExpire());
        Assert.Equal(TimeSpan.FromSeconds(80), timer.Snapshot.Elapsed);
        Assert.False(timer.TryExpire());
    }

    [Fact]
    public void WallClockChangesDoNotAffectTheMonotonicDeadline()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(5), clock);
        timer.Start();
        clock.UtcNow = clock.UtcNow.AddDays(100);
        Assert.False(timer.TryExpire());
        Assert.Equal(TimeSpan.FromSeconds(5), timer.Snapshot.Remaining);
        clock.Advance(TimeSpan.FromSeconds(5));
        clock.UtcNow = clock.UtcNow.AddDays(-200);
        Assert.True(timer.TryExpire());
    }

    [Theory]
    [InlineData(RecordingStopReason.UserStop)]
    [InlineData(RecordingStopReason.WindowClose)]
    public void ManualStopOrCloseCancelsLaterExpiry(RecordingStopReason reason)
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(5), clock);
        timer.Start();
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(timer.TryRequestStop(reason));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(timer.TryExpire());
        Assert.False(timer.TryRequestStop(reason));
        Assert.Equal(reason, timer.StopReason);
    }

    [Fact]
    public void ExpiryBeforeManualStopClaimsOnlyOneStop()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(1), clock);
        timer.Start();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(timer.TryExpire());
        Assert.False(timer.TryRequestStop(RecordingStopReason.UserStop));
        Assert.False(timer.TryRequestStop(RecordingStopReason.WindowClose));
        Assert.Equal(RecordingStopReason.TimerElapsed, timer.StopReason);
    }

    [Fact]
    public async Task ConcurrentManualStopAndExpiryHaveExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var clock = new ManualClock();
            var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(1), clock);
            timer.Start();
            clock.Advance(TimeSpan.FromSeconds(1));
            using var go = new ManualResetEventSlim();
            var manual = Task.Run(() => { go.Wait(); return timer.TryRequestStop(RecordingStopReason.UserStop); });
            var expiry = Task.Run(() => { go.Wait(); return timer.TryExpire(); });
            go.Set();
            var winners = await Task.WhenAll(manual, expiry);
            Assert.Single(winners, won => won);
            Assert.False(timer.TryExpire());
            Assert.False(timer.TryRequestStop(RecordingStopReason.WindowClose));
        }
    }

    [Fact]
    public void FailureCompletionCancelsExpiryAndFreezesItsClock()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(10), clock);
        timer.Start();
        clock.Advance(TimeSpan.FromSeconds(3));
        timer.Complete();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(timer.TryExpire());
        Assert.False(timer.TryRequestStop(RecordingStopReason.UserStop));
        Assert.Equal(TimeSpan.FromSeconds(3), timer.Snapshot.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(7), timer.Snapshot.Remaining);
        Assert.True(timer.Snapshot.Completed);
    }

    [Fact]
    public void FailedStartupNeverStartsOrExpiresTimer()
    {
        var clock = new ManualClock();
        var timer = new RecordingStopSchedule(TimeSpan.FromSeconds(1), clock);
        timer.Complete();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(timer.TryExpire());
        Assert.False(timer.Snapshot.Started);
        Assert.Throws<InvalidOperationException>(timer.Start);
    }

    [Fact]
    public void ANewRecordingHasItsOwnFreshDeadlineAndOldChecksCannotAffectIt()
    {
        var clock = new ManualClock();
        var old = new RecordingStopSchedule(TimeSpan.FromSeconds(2), clock);
        old.Start();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(old.TryExpire());
        old.Complete();
        var next = new RecordingStopSchedule(TimeSpan.FromSeconds(10), clock);
        next.Start();
        Assert.False(old.TryExpire());
        Assert.False(old.TryRequestStop(RecordingStopReason.WindowClose));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(next.TryExpire());
        Assert.Null(next.StopReason);
        Assert.Equal(TimeSpan.FromSeconds(8), next.Snapshot.Remaining);
        Assert.Throws<InvalidOperationException>(next.Start);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(86401)]
    public void InvalidProgrammaticDurationsAreRejected(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingStopSchedule(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void AManualCallerCannotFalselyDeclareTimerExpiry()
    {
        var timer = new RecordingStopSchedule(null);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.TryRequestStop(RecordingStopReason.TimerElapsed));
        Assert.Null(timer.StopReason);
    }

    [Theory]
    [InlineData("0", "0", "1", 1)]
    [InlineData("00", "30", "00", 1800)]
    [InlineData("1", "2", "3", 3723)]
    [InlineData("24", "00", "00", 86400)]
    [InlineData(" 1 ", "0", "0", 3600)]
    public void DurationInputHasPreciseSupportedBounds(string hours, string minutes, string seconds, int expected)
    {
        Assert.True(StopAfterDuration.TryParse(true, hours, minutes, seconds, out var duration, out var error));
        Assert.Null(error);
        Assert.Equal(TimeSpan.FromSeconds(expected), duration);
    }

    [Theory]
    [InlineData("0", "0", "0")]
    [InlineData("24", "0", "1")]
    [InlineData("25", "0", "0")]
    [InlineData("0", "60", "0")]
    [InlineData("0", "0", "60")]
    [InlineData("-1", "0", "1")]
    [InlineData("0", "-1", "1")]
    [InlineData("+1", "0", "0")]
    [InlineData("1.5", "0", "0")]
    [InlineData("", "1", "0")]
    [InlineData("abc", "1", "0")]
    [InlineData("9999999999999999", "1", "0")]
    public void InvalidDurationInputProvidesAnExplicitError(string hours, string minutes, string seconds)
    {
        Assert.False(StopAfterDuration.TryParse(true, hours, minutes, seconds, out var duration, out var error));
        Assert.Null(duration);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void DisabledInputIsOptionalAndDoesNotBlockRecording()
    {
        Assert.True(StopAfterDuration.TryParse(false, "", "invalid", "-2", out var duration, out var error));
        Assert.Null(duration);
        Assert.Null(error);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }
}
