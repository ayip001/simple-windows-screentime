using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SimpleWindowsScreentime.Service;

public class SessionMonitor
{
    private readonly ILogger<SessionMonitor> _logger;
    private bool _running;

    public event EventHandler<SessionChangeEventArgs>? SessionChanged;

    public SessionMonitor(ILogger<SessionMonitor> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _running = true;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _logger.LogInformation("Session monitor started");
    }

    public void Stop()
    {
        _running = false;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _logger.LogInformation("Session monitor stopped");
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (!_running) return;

        var reason = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLogon => SessionChangeReason.SessionLogon,
            Microsoft.Win32.SessionSwitchReason.SessionLogoff => SessionChangeReason.SessionLogoff,
            Microsoft.Win32.SessionSwitchReason.SessionLock => SessionChangeReason.SessionLock,
            Microsoft.Win32.SessionSwitchReason.SessionUnlock => SessionChangeReason.SessionUnlock,
            Microsoft.Win32.SessionSwitchReason.RemoteConnect => SessionChangeReason.RemoteConnect,
            Microsoft.Win32.SessionSwitchReason.RemoteDisconnect => SessionChangeReason.RemoteDisconnect,
            Microsoft.Win32.SessionSwitchReason.ConsoleConnect => SessionChangeReason.ConsoleConnect,
            Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect => SessionChangeReason.ConsoleDisconnect,
            _ => SessionChangeReason.Unknown
        };

        _logger.LogInformation("Session switch event: {Reason}", reason);
        SessionChanged?.Invoke(this, new SessionChangeEventArgs(reason));
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (!_running) return;

        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            _logger.LogInformation("Power mode changed: Resume from sleep/hibernate");
            SessionChanged?.Invoke(this, new SessionChangeEventArgs(SessionChangeReason.PowerResume));
        }
    }
}

public class SessionChangeEventArgs : EventArgs
{
    public SessionChangeReason Reason { get; }

    public SessionChangeEventArgs(SessionChangeReason reason)
    {
        Reason = reason;
    }
}

public enum SessionChangeReason
{
    Unknown,
    SessionLogon,
    SessionLogoff,
    SessionLock,
    SessionUnlock,
    RemoteConnect,
    RemoteDisconnect,
    ConsoleConnect,
    ConsoleDisconnect,
    PowerResume
}
