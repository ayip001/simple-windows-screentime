using Microsoft.Extensions.Logging;
using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Service;

public class NtpSyncService
{
    private readonly ILogger<NtpSyncService> _logger;
    private readonly ConfigManager _configManager;

    public NtpSyncService(ILogger<NtpSyncService> logger, ConfigManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
    }

    public async Task SyncTimeAsync()
    {
        _logger.LogInformation("Starting NTP time sync...");

        var offset = await NtpClient.GetTimeOffsetAsync();

        if (offset.HasValue)
        {
            _configManager.Update(config =>
            {
                config.TimeOffsetTicks = offset.Value.Ticks;
                config.LastNtpSyncUtc = DateTime.UtcNow;
            });

            _logger.LogInformation("NTP sync successful. Offset: {Offset}", offset.Value);
        }
        else
        {
            _logger.LogWarning("NTP sync failed, using existing offset");

            // If never synced, we use zero offset (trust system time)
            if (!_configManager.Config.LastNtpSyncUtc.HasValue)
            {
                _logger.LogInformation("No previous sync, trusting system time");
            }
        }
    }
}
