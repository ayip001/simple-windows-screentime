using System.Windows;

namespace SimpleWindowsScreentime.Blocker;

public partial class App : Application
{
    private KeyboardHook? _keyboardHook;
    private AccessibilityManager? _accessibilityManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Install keyboard hook to block system shortcuts
        _keyboardHook = new KeyboardHook();
        _keyboardHook.Install();

        // Suppress accessibility shortcuts
        _accessibilityManager = new AccessibilityManager();
        _accessibilityManager.SuppressAccessibilityShortcuts();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Uninstall keyboard hook
        _keyboardHook?.Uninstall();

        // Restore accessibility settings
        _accessibilityManager?.RestoreAccessibilityShortcuts();

        base.OnExit(e);
    }
}
