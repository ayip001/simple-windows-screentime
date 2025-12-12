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
                _logger.LogWarning("LaunchBlocker: Skipped (rapid relaunch, {Ms}ms since last attempt)", timeSinceLastAttempt.TotalMilliseconds);
                return;
            }

            if (IsBlockerRunning())
            {
                _logger.LogWarning("LaunchBlocker: Skipped (blocker already running)");
                return;
            }

            _lastLaunchAttempt = DateTime.UtcNow;

            var blockerPath = GetBlockerPath();
            _logger.LogInformation("LaunchBlocker: BlockerPath={Path}", blockerPath ?? "(null)");
            if (string.IsNullOrEmpty(blockerPath) || !File.Exists(blockerPath))
            {
                _logger.LogError("Blocker executable not found at expected path. Checked: {Primary}, {Backup}, {Local}",
                    Path.Combine(Constants.ProgramFilesPath, Constants.BlockerExeName),
                    Path.Combine(Constants.BackupPath, Constants.BlockerExeName),
                    Path.Combine(AppContext.BaseDirectory, Constants.BlockerExeName));
                return;
            }

            try
            {
                // Get active session ID
                var sessionId = GetActiveSessionId();
                _logger.LogInformation("LaunchBlocker: SessionId={SessionId}", sessionId);

                var startInfo = new ProcessStartInfo
                {
                    FileName = blockerPath,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                // Launch in user session if possible
                if (sessionId != 0)
                {
                    _logger.LogInformation("LaunchBlocker: Attempting LaunchInUserSession");
                    _blockerProcess = LaunchInUserSession(blockerPath, sessionId);
                }
                else
                {
                    _logger.LogWarning("LaunchBlocker: Session 0 detected, using Process.Start (may not be visible to user)");
                    _blockerProcess = Process.Start(startInfo);
                }

                if (_blockerProcess != null)
                {
                    _blockerProcess.EnableRaisingEvents = true;
                    _blockerProcess.Exited += OnBlockerExited;
                    _logger.LogInformation("Blocker launched successfully, PID: {PID}", _blockerProcess.Id);
                }
                else
                {
                    _logger.LogError("LaunchBlocker: Process launch returned null - blocker failed to start");
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
                var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _logger.LogWarning("WTSQueryUserToken failed for session {SessionId}, error code: {Error}. Falling back to Process.Start", sessionId, error);
                try
                {
                    var proc = Process.Start(path);
                    _logger.LogInformation("Fallback Process.Start returned: {Result}", proc != null ? $"PID {proc.Id}" : "null");
                    return proc;
                }
                catch (Exception startEx)
                {
                    _logger.LogError(startEx, "Fallback Process.Start also failed");
                    return null;
                }
            }

            try
            {
                var si = new NativeMethods.STARTUPINFO();
                si.cb = System.Runtime.InteropServices.Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default";

                _logger.LogInformation("Calling CreateProcessAsUser for session {SessionId}", sessionId);

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
                    var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    _logger.LogWarning("CreateProcessAsUser failed, error code: {Error}. Falling back to Process.Start", error);
                    try
                    {
                        var proc = Process.Start(path);
                        _logger.LogInformation("Fallback Process.Start returned: {Result}", proc != null ? $"PID {proc.Id}" : "null");
                        return proc;
                    }
                    catch (Exception startEx)
                    {
                        _logger.LogError(startEx, "Fallback Process.Start also failed");
                        return null;
                    }
                }

                _logger.LogInformation("CreateProcessAsUser succeeded, PID: {PID}", pi.dwProcessId);

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
            try
            {
                var proc = Process.Start(path);
                _logger.LogInformation("Exception fallback Process.Start returned: {Result}", proc != null ? $"PID {proc.Id}" : "null");
                return proc;
            }
            catch (Exception startEx)
            {
                _logger.LogError(startEx, "Exception fallback Process.Start also failed");
                return null;
            }
        }
    }
}
