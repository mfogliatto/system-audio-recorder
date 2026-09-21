using System.Globalization;

namespace SystemAudioRecorder.Core;

public static class StopAfterDuration
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(24);

    public static bool TryParse(bool enabled, string hours, string minutes, string seconds,
        out TimeSpan? duration, out string? error)
    {
        duration = null;
        error = null;
        if (!enabled) return true;
        if (!int.TryParse(hours.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(minutes.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(seconds.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ||
            h is < 0 or > 24 || m is < 0 or > 59 || s is < 0 or > 59)
        {
            error = "Enter whole numbers: hours 0-24, minutes and seconds 0-59.";
            return false;
        }
        var parsed = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        if (parsed < Minimum || parsed > Maximum)
        {
            error = "Stop after must be between 00:00:01 and 24:00:00.";
            return false;
        }
        duration = parsed;
        return true;
    }
}
