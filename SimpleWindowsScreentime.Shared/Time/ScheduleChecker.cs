using SimpleWindowsScreentime.Shared.Configuration;

namespace SimpleWindowsScreentime.Shared.Time;

public class ScheduleChecker
{
    private readonly ConfigManager _configManager;

    public ScheduleChecker(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public DateTime GetTrustedTimeUtc()
    {
        var config = _configManager.Config;
        var ntpOffset = TimeSpan.FromTicks(config.TimeOffsetTicks);
        var debugOffset = TimeSpan.FromTicks(config.DebugTimeOffsetTicks);
        return DateTime.UtcNow + ntpOffset + debugOffset;
    }

    public DateTime GetTrustedTimeLocal()
    {
        return GetTrustedTimeUtc().ToLocalTime();
    }

    public bool IsWithinBlockWindow()
    {
        return IsWithinBlockWindow(GetTrustedTimeLocal());
    }

    public bool IsWithinBlockWindow(DateTime localTime)
    {
        var config = _configManager.Config;
        var currentMinutes = localTime.Hour * 60 + localTime.Minute;

        return IsWithinBlockWindow(
            currentMinutes,
            config.BlockStartMinutes,
            config.BlockEndMinutes);
    }

    public static bool IsWithinBlockWindow(int currentMinutes, int startMinutes, int endMinutes)
    {
        // Handle overnight window (e.g., 22:00 - 06:00)
        if (startMinutes > endMinutes)
        {
            // Overnight: block if current is >= start OR current < end
            return currentMinutes >= startMinutes || currentMinutes < endMinutes;
        }
        else
        {
            // Same day: block if current is >= start AND current < end
            return currentMinutes >= startMinutes && currentMinutes < endMinutes;
        }
    }

    public DateTime GetBlockEndTimeLocal()
    {
        var config = _configManager.Config;
        var now = GetTrustedTimeLocal();
        var currentMinutes = now.Hour * 60 + now.Minute;

        var endHour = config.BlockEndMinutes / 60;
        var endMinute = config.BlockEndMinutes % 60;

        var blockEnd = now.Date.AddHours(endHour).AddMinutes(endMinute);

        // Handle overnight window
        if (config.BlockStartMinutes > config.BlockEndMinutes)
        {
            // If we're in the early morning portion (before end time), end is today
            // If we're in the evening portion (after start time), end is tomorrow
            if (currentMinutes >= config.BlockStartMinutes)
            {
                blockEnd = blockEnd.AddDays(1);
            }
        }
        else
        {
            // Same day window: if we've passed the end, it's tomorrow
            if (currentMinutes >= config.BlockEndMinutes)
            {
                blockEnd = blockEnd.AddDays(1);
            }
        }

        return blockEnd;
    }

    public TimeSpan GetTimeUntilBlockEnd()
    {
        var now = GetTrustedTimeLocal();
        var blockEnd = GetBlockEndTimeLocal();
        var remaining = blockEnd - now;

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public DateTime GetNextBlockStartLocal()
    {
        var config = _configManager.Config;
        var now = GetTrustedTimeLocal();
        var currentMinutes = now.Hour * 60 + now.Minute;

        var startHour = config.BlockStartMinutes / 60;
        var startMinute = config.BlockStartMinutes % 60;

        var blockStart = now.Date.AddHours(startHour).AddMinutes(startMinute);

        // If we've passed today's start time, next start is tomorrow
        if (currentMinutes >= config.BlockStartMinutes)
        {
            blockStart = blockStart.AddDays(1);
        }

        return blockStart;
    }

    public bool HasTempUnlock()
    {
        var config = _configManager.Config;
        if (!config.TempUnlockExpiresUtc.HasValue)
            return false;

        return GetTrustedTimeUtc() < config.TempUnlockExpiresUtc.Value;
    }

    public TimeSpan? GetTempUnlockRemaining()
    {
        var config = _configManager.Config;
        if (!config.TempUnlockExpiresUtc.HasValue)
            return null;

        var remaining = config.TempUnlockExpiresUtc.Value - GetTrustedTimeUtc();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public bool ShouldBlock()
    {
        // Don't block during temp unlock
        if (HasTempUnlock())
            return false;

        // Block only during block window
        return IsWithinBlockWindow();
    }

    public static string FormatMinutesAsTime(int minutes)
    {
        var hour = minutes / 60;
        var minute = minutes % 60;
        var ampm = hour >= 12 ? "PM" : "AM";
        var displayHour = hour % 12;
        if (displayHour == 0) displayHour = 12;

        return $"{displayHour}:{minute:D2} {ampm}";
    }

    public static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        else if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
        else if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }
        else
        {
            return $"{span.Seconds}s";
        }
    }
}
