using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SimpleWindowsScreentime.Shared;

namespace SimpleWindowsScreentime.Service;

public class BlockerProcessManager
{
    private readonly ILogger<BlockerProcessManager> _logger;
    private Process? _blockerProcess;
    private readonly object _lock = new();
    private DateTime _lastLaunchAttempt = DateTime.MinValue;

    public BlockerProcessManager(ILogger<BlockerProcessManager> logger)
    {
        _logger = logger;
    }

    public bool IsBlockerRunning()
    {
        lock (_lock)
        {
            if (_blockerProcess == null)
                return false;

            try
            {
                _blockerProcess.Refresh();
                return !_blockerProcess.HasExited;
            }
            catch
            {
                _blockerProcess = null;
                return false;
            }
        }
    }

    public void LaunchBlocker()
    {
        lock (_lock)
        {
            // Prevent rapid relaunch attempts
            var timeSinceLastAttempt = DateTime.UtcNow - _lastLaunchAttempt;
            if (timeSinceLastAttempt.TotalMilliseconds < Constants.BlockerRelaunchDelayMs)
            {
                return;
            }

            if (IsBlockerRunning())
            {
                return;
            }

            _lastLaunchAttempt = DateTime.UtcNow;

            var blockerPath = GetBlockerPath();
            if (string.IsNullOrEmpty(blockerPath) || !File.Exists(blockerPath))
            {
                _logger.LogError("Blocker executable not found at expected path");
                return;
            }

            try
            {
                // Get active session ID
                var sessionId = GetActiveSessionId();

                var startInfo = new ProcessStartInfo
                {
                    FileName = blockerPath,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                // Launch in user session if possible
                if (sessionId != 0)
                {
                    _blockerProcess = LaunchInUserSession(blockerPath, sessionId);
                }
                else
                {
                    _blockerProcess = Process.Start(startInfo);
                }

                if (_blockerProcess != null)
                {
                    _blockerProcess.EnableRaisingEvents = true;
                    _blockerProcess.Exited += OnBlockerExited;
                    _logger.LogInformation("Blocker launched successfully, PID: {PID}", _blockerProcess.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch blocker");
            }
        }
    }

    private void OnBlockerExited(object? sender, EventArgs e)
    {
        _logger.LogInformation("Blocker process exited");
        lock (_lock)
        {
            _blockerProcess = null;
        }
    }

    public void KillBlocker()
    {
        lock (_lock)
        {
            if (_blockerProcess == null)
                return;

            try
            {
                if (!_blockerProcess.HasExited)
                {
                    _blockerProcess.Kill(true);
                    _logger.LogInformation("Blocker process killed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error killing blocker process");
            }
            finally
            {
                _blockerProcess?.Dispose();
                _blockerProcess = null;
            }
        }

        // Also kill any other instances
        KillAllBlockerInstances();
    }

    private void KillAllBlockerInstances()
    {
        try
        {
            var blockerName = Path.GetFileNameWithoutExtension(Constants.BlockerExeName);
            var processes = Process.GetProcessesByName(blockerName);

            foreach (var process in processes)
            {
                try
                {
                    process.Kill(true);
                    _logger.LogInformation("Killed additional blocker instance, PID: {PID}", process.Id);
                }
                catch
                {
                    // Ignore
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error killing blocker instances");
        }
    }

    private static string? GetBlockerPath()
    {
        // Try primary location first
        var primaryPath = Path.Combine(Constants.ProgramFilesPath, Constants.BlockerExeName);
        if (File.Exists(primaryPath))
            return primaryPath;

        // Try backup location
        var backupPath = Path.Combine(Constants.BackupPath, Constants.BlockerExeName);
        if (File.Exists(backupPath))
            return backupPath;

        // Try same directory as service
        var localPath = Path.Combine(AppContext.BaseDirectory, Constants.BlockerExeName);
        if (File.Exists(localPath))
            return localPath;

        return null;
    }

    private static uint GetActiveSessionId()
    {
        try
        {
            return NativeMethods.WTSGetActiveConsoleSessionId();
        }
        catch
        {
            return 0;
        }
    }

    private Process? LaunchInUserSession(string path, uint sessionId)
    {
        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out var userToken))
            {
                _logger.LogWarning("Failed to get user token, launching directly");
                return Process.Start(path);
            }

            try
            {
                var si = new NativeMethods.STARTUPINFO();
                si.cb = System.Runtime.InteropServices.Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default";

                if (!NativeMethods.CreateProcessAsUser(
                    userToken,
                    path,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CREATE_NEW_CONSOLE,
                    IntPtr.Zero,
                    null,
                    ref si,
                    out var pi))
                {
                    _logger.LogWarning("CreateProcessAsUser failed, launching directly");
                    return Process.Start(path);
                }

                NativeMethods.CloseHandle(pi.hProcess);
                NativeMethods.CloseHandle(pi.hThread);

                return Process.GetProcessById((int)pi.dwProcessId);
            }
            finally
            {
                NativeMethods.CloseHandle(userToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error launching in user session, falling back to direct launch");
            return Process.Start(path);
        }
    }
}
