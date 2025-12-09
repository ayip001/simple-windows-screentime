using SimpleWindowsScreentime.Shared.Configuration;

namespace SimpleWindowsScreentime.Shared.Security;

public class RecoveryManager
{
    private readonly ConfigManager _configManager;
    private readonly PinManager _pinManager;

    public RecoveryManager(ConfigManager configManager, PinManager pinManager)
    {
        _configManager = configManager;
        _pinManager = pinManager;
    }

    public bool IsRecoveryActive => _configManager.Config.RecoveryActive;

    public DateTime? RecoveryExpiresUtc => _configManager.Config.RecoveryExpiresUtc;

    public TimeSpan? GetRecoveryRemaining()
    {
        var config = _configManager.Config;
        if (!config.RecoveryActive || !config.RecoveryExpiresUtc.HasValue)
            return null;

        var remaining = config.RecoveryExpiresUtc.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public void InitiateRecovery()
    {
        _configManager.Update(config =>
        {
            config.RecoveryActive = true;
            config.RecoveryExpiresUtc = DateTime.UtcNow.AddHours(Constants.RecoveryHours);
        });
    }

    public void CancelRecovery()
    {
        _configManager.Update(config =>
        {
            config.RecoveryActive = false;
            config.RecoveryExpiresUtc = null;
        });
    }

    public bool CheckAndProcessRecovery()
    {
        var config = _configManager.Config;

        if (!config.RecoveryActive)
            return false;

        if (!config.RecoveryExpiresUtc.HasValue)
        {
            // Malformed recovery state, cancel it
            CancelRecovery();
            return false;
        }

        // Check if recovery period has completed
        if (DateTime.UtcNow >= config.RecoveryExpiresUtc.Value)
        {
            // Recovery complete - wipe PIN
            _pinManager.ClearPin();
            CancelRecovery();
            return true; // Indicates recovery completed
        }

        return false;
    }
}
