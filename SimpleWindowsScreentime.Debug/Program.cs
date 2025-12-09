using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Debug;

class Program
{
    private static readonly string PipeName = Constants.PipeName.Replace(@"Global\", "");
    private static bool _debugModeActive = false;
    private static string? _debugToken = null;

    static async Task Main(string[] args)
    {
        Console.Title = "Screen Time Debug Console";
        WriteHeader();

        if (args.Length > 0 && args[0] == "--enable-backdoor")
        {
            _debugModeActive = true;
            _debugToken = Guid.NewGuid().ToString("N")[..8];
            WriteColor($"[DEBUG MODE ENABLED] Backdoor token: {_debugToken}\n", ConsoleColor.Magenta);
        }

        while (true)
        {
            Console.WriteLine();
            WriteColor("Commands:", ConsoleColor.Cyan);
            Console.WriteLine("  1. Check service status");
            Console.WriteLine("  2. Query service state (via IPC)");
            Console.WriteLine("  3. View config file");
            Console.WriteLine("  4. View service logs (Event Viewer)");
            Console.WriteLine("  5. Test IPC connection");
            Console.WriteLine("  6. List running processes");
            Console.WriteLine("  7. Kill blocker process");
            Console.WriteLine("  8. Check scheduled tasks");
            Console.WriteLine("  9. Restart service");
            if (_debugModeActive)
            {
                WriteColor("  B. [BACKDOOR] Disable blocking temporarily", ConsoleColor.Magenta);
                WriteColor("  C. [BACKDOOR] Clear PIN (reset to setup mode)", ConsoleColor.Magenta);
            }
            Console.WriteLine("  Q. Quit");
            Console.WriteLine();
            Console.Write("Enter choice: ");

            var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    CheckServiceStatus();
                    break;
                case "2":
                    await QueryServiceStateAsync();
                    break;
                case "3":
                    ViewConfigFile();
                    break;
                case "4":
                    ViewServiceLogs();
                    break;
                case "5":
                    await TestIpcConnectionAsync();
                    break;
                case "6":
                    ListProcesses();
                    break;
                case "7":
                    KillBlocker();
                    break;
                case "8":
                    CheckScheduledTasks();
                    break;
                case "9":
                    RestartService();
                    break;
                case "B" when _debugModeActive:
                    await DisableBlockingTemporarilyAsync();
                    break;
                case "C" when _debugModeActive:
                    ClearPin();
                    break;
                case "Q":
                    return;
                default:
                    WriteColor("Invalid choice", ConsoleColor.Red);
                    break;
            }
        }
    }

    static void WriteHeader()
    {
        WriteColor("=".PadRight(60, '='), ConsoleColor.Cyan);
        WriteColor("       SCREEN TIME DEBUG CONSOLE", ConsoleColor.Cyan);
        WriteColor("=".PadRight(60, '='), ConsoleColor.Cyan);
        Console.WriteLine();
        WriteColor("This tool is for debugging purposes only.", ConsoleColor.Yellow);
        WriteColor("Run with --enable-backdoor for emergency access.", ConsoleColor.Yellow);
        Console.WriteLine();
    }

    static void WriteColor(string message, ConsoleColor color)
    {
        var oldColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = oldColor;
    }

    static void CheckServiceStatus()
    {
        WriteColor("==> Service Status", ConsoleColor.Cyan);

        try
        {
            using var sc = new ServiceController(Constants.ServiceName);
            Console.WriteLine($"  Service Name:   {sc.ServiceName}");
            Console.WriteLine($"  Display Name:   {sc.DisplayName}");
            Console.WriteLine($"  Status:         {sc.Status}");
            Console.WriteLine($"  Start Type:     {sc.StartType}");

            if (sc.Status == ServiceControllerStatus.Running)
            {
                WriteColor("  [OK] Service is running", ConsoleColor.Green);
            }
            else
            {
                WriteColor($"  [!] Service is not running (Status: {sc.Status})", ConsoleColor.Yellow);
            }
        }
        catch (InvalidOperationException)
        {
            WriteColor("  [X] Service not found!", ConsoleColor.Red);
            Console.WriteLine("  The service may not be installed.");
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static async Task QueryServiceStateAsync()
    {
        WriteColor("==> Querying Service State via IPC", ConsoleColor.Cyan);

        try
        {
            var response = await SendIpcRequestAsync<StateResponse>(new GetStateRequest());

            if (response == null)
            {
                WriteColor("  [X] No response from service", ConsoleColor.Red);
                return;
            }

            Console.WriteLine($"  Is Blocking:        {response.IsBlocking}");
            Console.WriteLine($"  Is Setup Mode:      {response.IsSetupMode}");
            Console.WriteLine($"  Block Start:        {ScheduleChecker.FormatMinutesAsTime(response.BlockStartMinutes)}");
            Console.WriteLine($"  Block End:          {ScheduleChecker.FormatMinutesAsTime(response.BlockEndMinutes)}");
            Console.WriteLine($"  Current Time (UTC): {response.CurrentTimeUtc}");
            Console.WriteLine($"  Trusted Time (UTC): {response.TrustedTimeUtc}");
            Console.WriteLine($"  Temp Unlock Active: {response.TempUnlockActive}");
            Console.WriteLine($"  Recovery Active:    {response.RecoveryActive}");
            Console.WriteLine($"  Is Locked Out:      {response.IsLockedOut}");
            Console.WriteLine($"  Failed Attempts:    {response.FailedAttempts}");

            if (response.BlockEndsAtLocal.HasValue)
            {
                Console.WriteLine($"  Block Ends At:      {response.BlockEndsAtLocal.Value:g}");
            }

            WriteColor("  [OK] IPC communication successful", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static void ViewConfigFile()
    {
        WriteColor("==> Config File Contents", ConsoleColor.Cyan);
        Console.WriteLine($"  Path: {Constants.ConfigFilePath}");
        Console.WriteLine();

        if (!File.Exists(Constants.ConfigFilePath))
        {
            WriteColor("  [!] Config file does not exist", ConsoleColor.Yellow);
            return;
        }

        try
        {
            var json = File.ReadAllText(Constants.ConfigFilePath);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var config = JsonSerializer.Deserialize<AppConfig>(json);

            if (config != null)
            {
                Console.WriteLine($"  PIN Set:            {!config.IsSetupMode}");
                Console.WriteLine($"  Block Start:        {ScheduleChecker.FormatMinutesAsTime(config.BlockStartMinutes)}");
                Console.WriteLine($"  Block End:          {ScheduleChecker.FormatMinutesAsTime(config.BlockEndMinutes)}");
                Console.WriteLine($"  Time Offset:        {config.TimeOffset}");
                Console.WriteLine($"  Last NTP Sync:      {config.LastNtpSyncUtc}");
                Console.WriteLine($"  Recovery Active:    {config.RecoveryActive}");
                Console.WriteLine($"  Failed Attempts:    {config.FailedAttempts}");
                Console.WriteLine($"  Temp Unlock Until:  {config.TempUnlockExpiresUtc}");
            }

            Console.WriteLine();
            WriteColor("  Raw JSON:", ConsoleColor.Gray);
            Console.WriteLine(json);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error reading config: {ex.Message}", ConsoleColor.Red);
        }
    }

    static void ViewServiceLogs()
    {
        WriteColor("==> Opening Event Viewer for service logs", ConsoleColor.Cyan);
        Console.WriteLine("  Looking for logs in Application and System event logs...");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "eventvwr.msc",
                UseShellExecute = true
            });
            WriteColor("  [OK] Event Viewer opened", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static async Task TestIpcConnectionAsync()
    {
        WriteColor("==> Testing IPC Connection", ConsoleColor.Cyan);
        Console.WriteLine($"  Pipe Name: {PipeName}");

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            Console.WriteLine("  Attempting to connect (5 second timeout)...");

            var cts = new CancellationTokenSource(5000);
            await client.ConnectAsync(cts.Token);

            WriteColor("  [OK] Connected to pipe!", ConsoleColor.Green);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            Console.WriteLine("  Sending get_state request...");
            await writer.WriteLineAsync("{\"type\":\"get_state\"}");

            Console.WriteLine("  Waiting for response...");
            var response = await reader.ReadLineAsync();

            if (!string.IsNullOrEmpty(response))
            {
                WriteColor("  [OK] Received response!", ConsoleColor.Green);
                Console.WriteLine($"  Response length: {response.Length} chars");
                Console.WriteLine($"  Response preview: {response[..Math.Min(200, response.Length)]}...");
            }
            else
            {
                WriteColor("  [!] Empty response received", ConsoleColor.Yellow);
            }
        }
        catch (System.TimeoutException)
        {
            WriteColor("  [X] Connection timed out - service may not be running or IPC not started", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.GetType().Name}: {ex.Message}", ConsoleColor.Red);
        }
    }

    static void ListProcesses()
    {
        WriteColor("==> Screen Time Related Processes", ConsoleColor.Cyan);

        var processNames = new[] { "WinDisplayCalibration", "STBlocker", "STConfigPanel", "STDebug" };

        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                WriteColor($"  {name}:", ConsoleColor.White);
                foreach (var p in processes)
                {
                    Console.WriteLine($"    PID: {p.Id}, Started: {p.StartTime:g}");
                    p.Dispose();
                }
            }
            else
            {
                Console.WriteLine($"  {name}: Not running");
            }
        }
    }

    static void KillBlocker()
    {
        WriteColor("==> Killing Blocker Process", ConsoleColor.Cyan);

        var processes = Process.GetProcessesByName("STBlocker");
        if (processes.Length == 0)
        {
            Console.WriteLine("  No blocker processes found");
            return;
        }

        foreach (var p in processes)
        {
            try
            {
                Console.WriteLine($"  Killing PID {p.Id}...");
                p.Kill(true);
                WriteColor($"  [OK] Killed PID {p.Id}", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteColor($"  [X] Failed to kill PID {p.Id}: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                p.Dispose();
            }
        }

        WriteColor("  [!] Note: Service will relaunch blocker if in block period", ConsoleColor.Yellow);
    }

    static void CheckScheduledTasks()
    {
        WriteColor("==> Scheduled Tasks", ConsoleColor.Cyan);

        var taskNames = new[] { "STG_Monitor", "STG_LogonTrigger", "STG_BootCheck" };

        foreach (var taskName in taskNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{taskName}\" /FO LIST",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        WriteColor($"  [OK] {taskName}: EXISTS", ConsoleColor.Green);
                    }
                    else
                    {
                        WriteColor($"  [X] {taskName}: NOT FOUND", ConsoleColor.Red);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteColor($"  [X] {taskName}: Error - {ex.Message}", ConsoleColor.Red);
            }
        }

        // Check for check script file
        Console.WriteLine();
        Console.WriteLine("  Task files:");

        var checkScript = Path.Combine(Constants.ProgramDataPath, "STG_Check.ps1");
        if (File.Exists(checkScript))
        {
            WriteColor($"    [OK] {checkScript}", ConsoleColor.Green);
        }
        else
        {
            WriteColor($"    [X] {checkScript} - NOT FOUND", ConsoleColor.Red);
        }
    }

    static void RestartService()
    {
        WriteColor("==> Restarting Service", ConsoleColor.Cyan);

        try
        {
            using var sc = new ServiceController(Constants.ServiceName);

            if (sc.Status == ServiceControllerStatus.Running)
            {
                Console.WriteLine("  Stopping service...");
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                WriteColor("  [OK] Service stopped", ConsoleColor.Green);
            }

            Console.WriteLine("  Starting service...");
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            WriteColor("  [OK] Service started", ConsoleColor.Green);
        }
        catch (InvalidOperationException)
        {
            WriteColor("  [X] Service not found", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static async Task DisableBlockingTemporarilyAsync()
    {
        WriteColor("==> [BACKDOOR] Disabling Blocking Temporarily", ConsoleColor.Magenta);

        Console.Write("  Enter backdoor token: ");
        var token = Console.ReadLine()?.Trim();

        if (token != _debugToken)
        {
            WriteColor("  [X] Invalid token", ConsoleColor.Red);
            return;
        }

        // Kill blocker first
        KillBlocker();

        // Grant a temporary unlock by modifying config directly
        try
        {
            var configManager = new ConfigManager();
            configManager.Update(config =>
            {
                config.TempUnlockExpiresUtc = DateTime.UtcNow.AddHours(1);
            });

            WriteColor("  [OK] Granted 1-hour temporary unlock via config", ConsoleColor.Green);
            WriteColor("  [!] Restart service to apply: option 9", ConsoleColor.Yellow);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static void ClearPin()
    {
        WriteColor("==> [BACKDOOR] Clearing PIN", ConsoleColor.Magenta);

        Console.Write("  Enter backdoor token: ");
        var token = Console.ReadLine()?.Trim();

        if (token != _debugToken)
        {
            WriteColor("  [X] Invalid token", ConsoleColor.Red);
            return;
        }

        Console.Write("  Are you sure? This will reset to setup mode. (yes/no): ");
        var confirm = Console.ReadLine()?.Trim();

        if (confirm?.ToLower() != "yes")
        {
            Console.WriteLine("  Cancelled");
            return;
        }

        try
        {
            var configManager = new ConfigManager();
            configManager.Update(config =>
            {
                config.PinHash = null;
                config.PinSalt = null;
                config.FailedAttempts = 0;
                config.ConsecutiveFailures = 0;
                config.LockoutUntilUtc = null;
                config.RecoveryActive = false;
                config.RecoveryExpiresUtc = null;
            });

            WriteColor("  [OK] PIN cleared - now in setup mode", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColor($"  [X] Error: {ex.Message}", ConsoleColor.Red);
        }
    }

    static async Task<T?> SendIpcRequestAsync<T>(IpcRequest request) where T : IpcResponse
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            var cts = new CancellationTokenSource(5000);
            await client.ConnectAsync(cts.Token);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var json = IpcSerializer.Serialize(request);
            await writer.WriteLineAsync(json);

            var responseLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(responseLine))
                return null;

            return IpcSerializer.Deserialize<T>(responseLine);
        }
        catch
        {
            return null;
        }
    }
}
