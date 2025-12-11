using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.Security;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Service;

public class ScreenTimeWorker : BackgroundService
{
    private readonly ILogger<ScreenTimeWorker> _logger;
    private readonly ConfigManager _configManager;
    private readonly ScheduleChecker _scheduleChecker;
    private readonly RecoveryManager _recoveryManager;
    private readonly UnlockManager _unlockManager;
    private readonly BlockerProcessManager _blockerManager;
    private readonly IpcServer _ipcServer;
    private readonly NtpSyncService _ntpSyncService;
    private readonly SelfHealingService _selfHealingService;
    private readonly SessionMonitor _sessionMonitor;

    private DateTime _lastSelfHealCheck = DateTime.MinValue;
    private DateTime _lastConfigCheck = DateTime.MinValue;
    private DateTime _lastNtpSync = DateTime.MinValue;

    public ScreenTimeWorker(
        ILogger<ScreenTimeWorker> logger,
        ConfigManager configManager,
        ScheduleChecker scheduleChecker,
        RecoveryManager recoveryManager,
        UnlockManager unlockManager,
        BlockerProcessManager blockerManager,
        IpcServer ipcServer,
        NtpSyncService ntpSyncService,
        SelfHealingService selfHealingService,
        SessionMonitor sessionMonitor)
    {
        _logger = logger;
        _configManager = configManager;
        _scheduleChecker = scheduleChecker;
        _recoveryManager = recoveryManager;
        _unlockManager = unlockManager;
        _blockerManager = blockerManager;
        _ipcServer = ipcServer;
        _ntpSyncService = ntpSyncService;
        _selfHealingService = selfHealingService;
        _sessionMonitor = sessionMonitor;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Screen Time Service starting...");

        // Ensure config directory exists
        Directory.CreateDirectory(Constants.ProgramDataPath);
        Directory.CreateDirectory(Constants.BackupPath);

        // Load or create config
        _configManager.Load();
        _configManager.EnsureConfigFileExists();

        // Copy binaries to backup on startup
        _selfHealingService.BackupBinaries();

        // Start IPC server
        _ = _ipcServer.StartAsync(cancellationToken);

        // Initial NTP sync
        await _ntpSyncService.SyncTimeAsync();
        _lastNtpSync = DateTime.UtcNow;

        // Subscribe to session events
        _sessionMonitor.SessionChanged += OnSessionChanged;
        _sessionMonitor.Start();

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Screen Time Service main loop starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MainLoopIteration();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main loop iteration");
            }

            await Task.Delay(Constants.MainLoopIntervalMs, stoppingToken);
        }
    }

    private async Task MainLoopIteration()
    {
        // Process recovery if active
        _recoveryManager.CheckAndProcessRecovery();

        // Clear expired temp unlock
        _unlockManager.CheckAndClearExpiredUnlock();

        // Determine if we should be blocking (use consistent trusted time)
        var inBlockWindow = _scheduleChecker.IsWithinBlockWindow();
        var hasTempUnlock = _scheduleChecker.HasTempUnlock();  // Use ScheduleChecker for consistent trusted time
        var shouldBlock = _scheduleChecker.ShouldBlock();
        var blockerRunning = _blockerManager.IsBlockerRunning();

        _logger.LogInformation("MainLoop: InBlockWindow={InBlock}, HasTempUnlock={Unlock}, ShouldBlock={Should}, BlockerRunning={Running}",
            inBlockWindow, hasTempUnlock, shouldBlock, blockerRunning);

        if (shouldBlock && !blockerRunning)
        {
            _logger.LogInformation("Block period active, launching blocker");
            _blockerManager.LaunchBlocker();
        }
        else if (!shouldBlock && blockerRunning)
        {
            _logger.LogInformation("Block period ended or temp unlock active, killing blocker");
            _blockerManager.KillBlocker();
        }

        // Periodic checks
        var now = DateTime.UtcNow;

        // Config file check every 30 seconds
        if ((now - _lastConfigCheck).TotalMilliseconds >= Constants.ConfigCheckIntervalMs)
        {
            CheckConfigFile();
            _lastConfigCheck = now;
        }

        // Self-healing check every 5 minutes
        if ((now - _lastSelfHealCheck).TotalMilliseconds >= Constants.SelfHealIntervalMs)
        {
            await PerformSelfHealingChecks();
            _lastSelfHealCheck = now;
        }

        // NTP sync every 6 hours
        if ((now - _lastNtpSync).TotalHours >= Constants.NtpSyncIntervalHours)
        {
            await _ntpSyncService.SyncTimeAsync();
            _lastNtpSync = now;
        }
    }

    private void CheckConfigFile()
    {
        if (!_configManager.ConfigFileExists() || ConfigManager.IsConfigCorrupted())
        {
            _logger.LogWarning("Config file missing or corrupted, rewriting from cache");
            _configManager.RewriteFromCache();
        }
    }

    private async Task PerformSelfHealingChecks()
    {
        _logger.LogInformation("Performing self-healing checks...");

        // Verify and restore binaries
        _selfHealingService.VerifyAndRestoreBinaries();

        // Verify and recreate scheduled tasks
        await _selfHealingService.VerifyScheduledTasksAsync();

        // Verify registry run key
        _selfHealingService.VerifyRegistryRunKey();

        // Backup service registry
        _selfHealingService.BackupServiceRegistry();
    }

    private void OnSessionChanged(object? sender, SessionChangeEventArgs e)
    {
        _logger.LogInformation("Session change event: {Event}", e.Reason);

        // Re-evaluate blocking on session changes
        var shouldBlock = _scheduleChecker.ShouldBlock();
        var blockerRunning = _blockerManager.IsBlockerRunning();

        if (shouldBlock && !blockerRunning)
        {
            _logger.LogInformation("Launching blocker after session change");
            _blockerManager.LaunchBlocker();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Screen Time Service stopping...");

        _sessionMonitor.Stop();
        _ipcServer.Stop();
        _blockerManager.KillBlocker();

        await base.StopAsync(cancellationToken);
    }
}
