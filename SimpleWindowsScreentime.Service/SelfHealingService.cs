using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SimpleWindowsScreentime.Shared;

namespace SimpleWindowsScreentime.Service;

public class SelfHealingService
{
    private readonly ILogger<SelfHealingService> _logger;
    private readonly Dictionary<string, byte[]> _binaryHashes = new();

    private static readonly string[] BinaryFiles =
    {
        Constants.ServiceExeName,
        Constants.BlockerExeName,
        Constants.ConfigPanelExeName
    };

    public SelfHealingService(ILogger<SelfHealingService> logger)
    {
        _logger = logger;
    }

    public void BackupBinaries()
    {
        _logger.LogInformation("Backing up binaries...");

        try
        {
            Directory.CreateDirectory(Constants.BackupPath);

            foreach (var file in BinaryFiles)
            {
                var sourcePath = Path.Combine(Constants.ProgramFilesPath, file);
                var backupPath = Path.Combine(Constants.BackupPath, file);

                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, backupPath, true);
                    _binaryHashes[file] = ComputeFileHash(sourcePath);
                    _logger.LogInformation("Backed up: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backing up binaries");
        }
    }

    public void VerifyAndRestoreBinaries()
    {
        _logger.LogInformation("Verifying binaries...");

        foreach (var file in BinaryFiles)
        {
            var primaryPath = Path.Combine(Constants.ProgramFilesPath, file);
            var backupPath = Path.Combine(Constants.BackupPath, file);

            try
            {
                bool needsRestore = false;

                if (!File.Exists(primaryPath))
                {
                    _logger.LogWarning("Binary missing: {File}", file);
                    needsRestore = true;
                }
                else if (_binaryHashes.TryGetValue(file, out var expectedHash))
                {
                    var currentHash = ComputeFileHash(primaryPath);
                    if (!expectedHash.SequenceEqual(currentHash))
                    {
                        _logger.LogWarning("Binary corrupted: {File}", file);
                        needsRestore = true;
                    }
                }

                if (needsRestore && File.Exists(backupPath))
                {
                    _logger.LogInformation("Restoring from backup: {File}", file);
                    Directory.CreateDirectory(Constants.ProgramFilesPath);
                    File.Copy(backupPath, primaryPath, true);
                }
                else if (needsRestore)
                {
                    _logger.LogError("Cannot restore {File} - no backup available", file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying/restoring binary: {File}", file);
            }
        }
    }

    public async Task VerifyScheduledTasksAsync()
    {
        _logger.LogInformation("Verifying scheduled tasks...");

        var tasks = new[]
        {
            (Constants.TaskMonitor1, "PT1M", false),  // Every 1 minute
            (Constants.TaskMonitor2, "PT1M", false),  // Every 1 minute (offset)
            (Constants.TaskLogonTrigger, null, true), // At logon
            (Constants.TaskBootCheck, null, false)    // At boot
        };

        foreach (var (taskName, interval, isLogon) in tasks)
        {
            if (!await TaskExistsAsync(taskName))
            {
                _logger.LogWarning("Scheduled task missing: {Task}", taskName);
                await CreateScheduledTaskAsync(taskName, interval, isLogon);
            }
        }
    }

    private async Task<bool> TaskExistsAsync(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{taskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateScheduledTaskAsync(string taskName, string? interval, bool isLogonTrigger)
    {
        try
        {
            var servicePath = Path.Combine(Constants.ProgramFilesPath, Constants.ServiceExeName);
            var checkScript = $@"
                $serviceName = '{Constants.ServiceName}'
                $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($null -eq $service) {{
                    # Try to recreate from backup
                    $backupReg = '{Constants.BackupPath}\service.reg'
                    if (Test-Path $backupReg) {{
                        reg import $backupReg 2>$null
                    }}
                }}
                $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                if ($null -ne $service -and $service.Status -ne 'Running') {{
                    Start-Service -Name $serviceName
                }}
            ";

            string trigger;
            if (isLogonTrigger)
            {
                trigger = "/SC ONLOGON";
            }
            else if (taskName == Constants.TaskBootCheck)
            {
                trigger = "/SC ONSTART";
            }
            else
            {
                trigger = $"/SC MINUTE /MO 1";
            }

            // Create a PowerShell script file for the task
            var scriptPath = Path.Combine(Constants.ProgramDataPath, $"{taskName}.ps1");
            await File.WriteAllTextAsync(scriptPath, checkScript);

            var args = $"/Create /TN \"{taskName}\" {trigger} /TR \"powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File \\\"{scriptPath}\\\"\" /RU SYSTEM /F /RL HIGHEST";

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logger.LogInformation("Created scheduled task: {Task}", taskName);
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError("Failed to create task {Task}: {Error}", taskName, error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating scheduled task: {Task}", taskName);
        }
    }

    public void VerifyRegistryRunKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Constants.RegistryRunKey, true);
            if (key == null)
            {
                _logger.LogWarning("Cannot open registry run key");
                return;
            }

            var value = key.GetValue(Constants.RegistryRunValueName) as string;
            var expectedPath = Path.Combine(Constants.ProgramFilesPath, Constants.ServiceExeName);

            if (string.IsNullOrEmpty(value) || !value.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Recreating registry run key");
                key.SetValue(Constants.RegistryRunValueName, expectedPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying registry run key");
        }
    }

    public void BackupServiceRegistry()
    {
        try
        {
            var backupPath = Path.Combine(Constants.BackupPath, "service.reg");

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{Constants.ServiceName}\" \"{backupPath}\" /y",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            if (process?.ExitCode == 0)
            {
                _logger.LogInformation("Backed up service registry");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backing up service registry");
        }
    }

    private static byte[] ComputeFileHash(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return sha256.ComputeHash(stream);
    }
}
