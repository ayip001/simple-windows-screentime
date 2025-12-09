using System.Windows;

namespace SimpleWindowsScreentime.Blocker;

public partial class App : Application
{
    private KeyboardHook? _keyboardHook;
    private AccessibilityManager? _accessibilityManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First, check if we should actually be blocking
        // This prevents the blocker from engaging without service confirmation
        var shouldBlock = await CheckShouldBlockAsync();

        if (!shouldBlock)
        {
            // Service says we shouldn't block or can't connect - exit immediately
            Shutdown(0);
            return;
        }

        // Only install hooks if we confirmed blocking is needed
        _keyboardHook = new KeyboardHook();
        _keyboardHook.Install();

        // Suppress accessibility shortcuts
        _accessibilityManager = new AccessibilityManager();
        _accessibilityManager.SuppressAccessibilityShortcuts();
    }

    private async Task<bool> CheckShouldBlockAsync()
    {
        try
        {
            var client = new IpcClient();
            var state = await client.GetStateAsync();

            if (state == null)
            {
                // Cannot connect to service - don't block without confirmation
                return false;
            }

            // Only block if:
            // 1. Not in setup mode (no PIN set yet), OR
            // 2. Currently blocking AND no active unlock
            if (state.IsSetupMode)
            {
                // Setup mode - don't block, let user set up the system
                return false;
            }

            // Check if we should actually be blocking
            return state.IsBlocking && !state.TempUnlockActive;
        }
        catch
        {
            // Any error - don't block
            return false;
        }
    }

    public void EnableBlocking()
    {
        // Called when transitioning from setup to blocking
        if (_keyboardHook == null)
        {
            _keyboardHook = new KeyboardHook();
            _keyboardHook.Install();
        }
        if (_accessibilityManager == null)
        {
            _accessibilityManager = new AccessibilityManager();
            _accessibilityManager.SuppressAccessibilityShortcuts();
        }
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
