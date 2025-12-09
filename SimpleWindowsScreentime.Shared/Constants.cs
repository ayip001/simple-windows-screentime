namespace SimpleWindowsScreentime.Shared;

public static class Constants
{
    // Paths
    public static readonly string ProgramDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "STGuard");

    public static readonly string ConfigFilePath = Path.Combine(ProgramDataPath, "config.json");
    public static readonly string BackupPath = Path.Combine(ProgramDataPath, "backup");

    public static readonly string ProgramFilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SimpleWindowsScreentime");

    // Service
    public const string ServiceName = "WinDisplayCalibration";
    public const string ServiceDisplayName = "Windows Display Calibration Service";
    public const string ServiceDescription = "Manages display calibration and color profiles";

    // IPC
    public const string PipeName = @"Global\STG_Pipe_7f3a";

    // PIN Settings
    public const int PinIterations = 100000;
    public const int PinHashLength = 32;
    public const int PinSaltLength = 32;
    public const int MaxPinAttempts = 5;
    public const int MaxConsecutiveFailures = 10;
    public const int LockoutMinutes = 15;
    public const int AttemptWindowMinutes = 1;

    // Default Schedule (1 AM to 7 AM)
    public const int DefaultBlockStartMinutes = 60;   // 01:00
    public const int DefaultBlockEndMinutes = 420;    // 07:00

    // Timing
    public const int MainLoopIntervalMs = 5000;       // 5 seconds
    public const int SelfHealIntervalMs = 300000;     // 5 minutes
    public const int ConfigCheckIntervalMs = 30000;   // 30 seconds
    public const int NtpSyncIntervalHours = 6;
    public const int BlockerRelaunchDelayMs = 500;

    // Recovery
    public const int RecoveryHours = 48;

    // NTP Servers
    public static readonly string[] NtpServers = { "pool.ntp.org", "time.windows.com" };

    // Scheduled Tasks
    public const string TaskMonitor1 = "STG_Monitor_1";
    public const string TaskMonitor2 = "STG_Monitor_2";
    public const string TaskLogonTrigger = "STG_LogonTrigger";
    public const string TaskBootCheck = "STG_BootCheck";

    // Registry
    public const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string RegistryRunValueName = "WinDisplayCalibration";

    // Executables
    public const string ServiceExeName = "WinDisplayCalibration.exe";
    public const string BlockerExeName = "STBlocker.exe";
    public const string ConfigPanelExeName = "STConfigPanel.exe";
}
