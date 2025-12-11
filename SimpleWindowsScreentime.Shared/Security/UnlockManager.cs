using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Shared.Security;

public class UnlockManager
{
    private readonly ConfigManager _configManager;
    private readonly ScheduleChecker _scheduleChecker;

    public UnlockManager(ConfigManager configManager, ScheduleChecker scheduleChecker)
    {
        _configManager = configManager;
        _scheduleChecker = scheduleChecker;
    }

    public bool HasActiveUnlock()
    {
        var config = _configManager.Config;
        if (!config.TempUnlockExpiresUtc.HasValue)
            return false;

        // Use trusted time for consistency
        return _scheduleChecker.GetTrustedTimeUtc() < config.TempUnlockExpiresUtc.Value;
    }

    public DateTime? GetUnlockExpiry()
    {
        return _configManager.Config.TempUnlockExpiresUtc;
    }

    public TimeSpan? GetUnlockRemaining()
    {
        var config = _configManager.Config;
        if (!config.TempUnlockExpiresUtc.HasValue)
            return null;

        // Use trusted time for consistency
        var remaining = config.TempUnlockExpiresUtc.Value - _scheduleChecker.GetTrustedTimeUtc();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public void GrantUnlock(UnlockDuration duration)
    {
        var expiresUtc = CalculateUnlockExpiry(duration);

        _configManager.Update(config =>
        {
            config.TempUnlockExpiresUtc = expiresUtc;
        });
    }

    public void ClearUnlock()
    {
        _configManager.Update(config =>
        {
            config.TempUnlockExpiresUtc = null;
        });
    }

    public void CheckAndClearExpiredUnlock()
    {
        var config = _configManager.Config;

        // Use trusted time for consistency
        if (config.TempUnlockExpiresUtc.HasValue &&
            _scheduleChecker.GetTrustedTimeUtc() >= config.TempUnlockExpiresUtc.Value)
        {
            ClearUnlock();
        }
    }

    private DateTime CalculateUnlockExpiry(UnlockDuration duration)
    {
        // Use trusted time for consistency
        var trustedUtc = _scheduleChecker.GetTrustedTimeUtc();
        return duration switch
        {
            UnlockDuration.FifteenMinutes => trustedUtc.AddMinutes(15),
            UnlockDuration.OneHour => trustedUtc.AddHours(1),
            UnlockDuration.RestOfPeriod => CalculateRestOfPeriodExpiry(),
            _ => trustedUtc.AddMinutes(15)
        };
    }

    private DateTime CalculateRestOfPeriodExpiry()
    {
        var blockEndLocal = _scheduleChecker.GetBlockEndTimeLocal();
        return blockEndLocal.ToUniversalTime();
    }
}
