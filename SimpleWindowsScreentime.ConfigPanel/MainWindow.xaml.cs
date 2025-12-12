using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.ConfigPanel;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipcClient;
    private string? _verifiedPin;
    private StateResponse? _currentState;
    private DispatcherTimer? _inactivityTimer;
    private const int InactivityTimeoutMinutes = 3;

    public MainWindow()
    {
        InitializeComponent();
        _ipcClient = new IpcClient();

        Loaded += MainWindow_Loaded;

        // Set up inactivity detection
        InputManager.Current.PreProcessInput += OnActivity;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckAccessAsync();
    }

    private async Task CheckAccessAsync()
    {
        try
        {
            var accessResult = await _ipcClient.CheckAccessAsync();

            if (accessResult == null)
            {
                MessageBox.Show("Cannot connect to Screen Time service. Please ensure the service is running.",
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            if (!accessResult.Allowed)
            {
                ShowAccessDenied();
                return;
            }

            // Check if setup mode (no PIN set)
            _currentState = await _ipcClient.GetStateAsync();
            if (_currentState?.IsSetupMode == true)
            {
                // Allow direct access in setup mode
                await LoadSettingsAsync();
                ShowSettings();
            }
            else
            {
                ShowPinEntry();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void ShowPinEntry()
    {
        PinEntryView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        AccessDeniedView.Visibility = Visibility.Collapsed;

        // Check if already locked out
        await CheckLockoutStateAsync();

        if (AccessPinBox.IsEnabled)
        {
            AccessPinBox.Focus();
        }
    }

    private async Task CheckLockoutStateAsync()
    {
        try
        {
            _currentState = await _ipcClient.GetStateAsync();
            if (_currentState != null && _currentState.IsLockedOut && _currentState.LockoutUntilUtc.HasValue)
            {
                var remaining = (int)(_currentState.LockoutUntilUtc.Value - DateTime.UtcNow).TotalSeconds;
                if (remaining > 0)
                {
                    SetLockoutState(remaining);
                }
            }
        }
        catch
        {
            // Ignore errors checking lockout state
        }
    }

    private void ShowSettings()
    {
        PinEntryView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        AccessDeniedView.Visibility = Visibility.Collapsed;
        StartInactivityTimer();
    }

    private void OnActivity(object sender, PreProcessInputEventArgs e)
    {
        // Reset inactivity timer on any input
        if (_inactivityTimer != null && SettingsView.Visibility == Visibility.Visible)
        {
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }
    }

    private void StartInactivityTimer()
    {
        _inactivityTimer?.Stop();
        _inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(InactivityTimeoutMinutes)
        };
        _inactivityTimer.Tick += OnInactivityTimeout;
        _inactivityTimer.Start();
    }

    private void StopInactivityTimer()
    {
        _inactivityTimer?.Stop();
        _inactivityTimer = null;
    }

    private void OnInactivityTimeout(object? sender, EventArgs e)
    {
        StopInactivityTimer();

        // Only lock if we're in settings view and not in setup mode
        if (SettingsView.Visibility == Visibility.Visible && _currentState?.IsSetupMode != true)
        {
            _verifiedPin = null;
            AccessPinBox.Password = "";
            AccessErrorText.Visibility = Visibility.Collapsed;
            ShowPinEntry();
        }
    }

    private void ShowAccessDenied()
    {
        PinEntryView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        AccessDeniedView.Visibility = Visibility.Visible;
    }

    private DispatcherTimer? _lockoutTimer;

    private async void AccessPinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AccessPinBox.Password.Length == 4)
        {
            await VerifyAccessPinAsync();
        }
        else if (AccessPinBox.Password.Length > 0)
        {
            // Only hide error when user starts typing a new PIN, not when cleared
            AccessErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private async Task VerifyAccessPinAsync()
    {
        try
        {
            var result = await _ipcClient.VerifyPinAsync(AccessPinBox.Password);

            if (result == null)
            {
                ShowAccessError("Cannot connect to service");
                return;
            }

            if (result.Valid)
            {
                _verifiedPin = AccessPinBox.Password;
                await LoadSettingsAsync();
                ShowSettings();
            }
            else if (result.IsLockedOut)
            {
                AccessPinBox.Password = "";
                SetLockoutState(result.LockoutRemainingSeconds ?? 0);
            }
            else if (result.IsRateLimited)
            {
                AccessPinBox.Password = "";
                SetRateLimitState();
            }
            else
            {
                ShowAccessError($"Invalid PIN ({result.AttemptsRemaining} attempts remaining)");
                AccessPinBox.Password = "";
            }
        }
        catch (Exception ex)
        {
            ShowAccessError($"Error: {ex.Message}");
        }
    }

    private void SetLockoutState(int remainingSeconds)
    {
        // Disable input
        AccessPinBox.IsEnabled = false;

        // Show lockout message
        UpdateLockoutMessage(remainingSeconds);

        // Start countdown timer
        _lockoutTimer?.Stop();
        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        var lockoutEnd = DateTime.Now.AddSeconds(remainingSeconds);

        _lockoutTimer.Tick += (s, e) =>
        {
            var remaining = (int)(lockoutEnd - DateTime.Now).TotalSeconds;
            if (remaining <= 0)
            {
                ClearLockoutState();
            }
            else
            {
                UpdateLockoutMessage(remaining);
            }
        };

        _lockoutTimer.Start();
    }

    private void UpdateLockoutMessage(int remainingSeconds)
    {
        var minutes = remainingSeconds / 60;
        var seconds = remainingSeconds % 60;

        string timeText;
        if (minutes > 0)
        {
            timeText = seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes} minute(s)";
        }
        else
        {
            timeText = $"{seconds} second(s)";
        }

        ShowAccessError($"Too many attempts. Locked out for {timeText}.");
    }

    private void ClearLockoutState()
    {
        _lockoutTimer?.Stop();
        _lockoutTimer = null;
        AccessPinBox.IsEnabled = true;
        AccessErrorText.Visibility = Visibility.Collapsed;
        AccessPinBox.Focus();
    }

    private void SetRateLimitState()
    {
        // Disable input for a short period (rate limiting is typically a few seconds)
        AccessPinBox.IsEnabled = false;
        ShowAccessError("Too many attempts. Please wait...");

        // Re-enable after 3 seconds
        _lockoutTimer?.Stop();
        _lockoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _lockoutTimer.Tick += (s, e) =>
        {
            _lockoutTimer?.Stop();
            _lockoutTimer = null;
            AccessPinBox.IsEnabled = true;
            AccessErrorText.Visibility = Visibility.Collapsed;
            AccessPinBox.Focus();
        };
        _lockoutTimer.Start();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _currentState = await _ipcClient.GetStateAsync();
            var config = await _ipcClient.GetConfigAsync();

            if (_currentState == null || config == null)
            {
                MessageBox.Show("Cannot load settings", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Update status display
            if (_currentState.IsBlocking)
            {
                StatusText.Text = "Status: Currently blocking";
            }
            else if (_currentState.TempUnlockActive)
            {
                StatusText.Text = "Status: Temporarily unlocked";
            }
            else
            {
                StatusText.Text = "Status: Not blocking";
            }

            // Show next block time
            var nextBlockStart = GetNextBlockStart(config.BlockStartMinutes, config.BlockEndMinutes);
            NextBlockText.Text = $"Next block period: {ScheduleChecker.FormatMinutesAsTime(config.BlockStartMinutes)} - {ScheduleChecker.FormatMinutesAsTime(config.BlockEndMinutes)}";

            // Recovery status
            if (config.RecoveryActive && config.RecoveryExpiresUtc.HasValue)
            {
                RecoveryBanner.Visibility = Visibility.Visible;
                var remaining = config.RecoveryExpiresUtc.Value - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    RecoveryStatusText.Text = $"Recovery active: PIN will be cleared in {ScheduleChecker.FormatTimeSpan(remaining)}";
                }
            }
            else
            {
                RecoveryBanner.Visibility = Visibility.Collapsed;
            }

            // Populate time combos
            PopulateTimeCombo(StartTimeCombo, config.BlockStartMinutes);
            PopulateTimeCombo(EndTimeCombo, config.BlockEndMinutes);

            // Update PIN section based on setup mode
            if (_currentState.IsSetupMode)
            {
                ChangePinTitle.Text = "Set PIN";
                CurrentPinLabel.Visibility = Visibility.Collapsed;
                CurrentPinBox.Visibility = Visibility.Collapsed;
                ChangePinButton.Content = "Set PIN";
            }
            else
            {
                ChangePinTitle.Text = "Change PIN";
                CurrentPinLabel.Visibility = Visibility.Visible;
                CurrentPinBox.Visibility = Visibility.Visible;
                ChangePinButton.Content = "Change PIN";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateTimeCombo(ComboBox combo, int selectedMinutes)
    {
        combo.Items.Clear();

        for (int minutes = 0; minutes < 1440; minutes += 30)
        {
            var item = new ComboBoxItem
            {
                Content = ScheduleChecker.FormatMinutesAsTime(minutes),
                Tag = minutes
            };
            combo.Items.Add(item);

            if (minutes == selectedMinutes)
            {
                combo.SelectedItem = item;
            }
        }

        // If exact time not found (not on 30-min boundary), add it
        if (combo.SelectedItem == null)
        {
            var item = new ComboBoxItem
            {
                Content = ScheduleChecker.FormatMinutesAsTime(selectedMinutes),
                Tag = selectedMinutes
            };
            combo.Items.Insert(0, item);
            combo.SelectedItem = item;
        }
    }

    private static string GetNextBlockStart(int startMinutes, int endMinutes)
    {
        var now = DateTime.Now;
        var currentMinutes = now.Hour * 60 + now.Minute;

        if (currentMinutes < startMinutes)
        {
            return $"Today at {ScheduleChecker.FormatMinutesAsTime(startMinutes)}";
        }
        else
        {
            return $"Tomorrow at {ScheduleChecker.FormatMinutesAsTime(startMinutes)}";
        }
    }

    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startItem = StartTimeCombo.SelectedItem as ComboBoxItem;
            var endItem = EndTimeCombo.SelectedItem as ComboBoxItem;

            if (startItem == null || endItem == null)
            {
                ShowScheduleError("Please select both start and end times");
                return;
            }

            var startMinutes = (int)startItem.Tag;
            var endMinutes = (int)endItem.Tag;

            // Use verified PIN from login, or empty for setup mode
            var pin = _verifiedPin ?? "";

            var success = await _ipcClient.SetScheduleAsync(pin, startMinutes, endMinutes);

            if (success)
            {
                ScheduleErrorText.Visibility = Visibility.Collapsed;
                MessageBox.Show("Schedule saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadSettingsAsync();
            }
            else
            {
                ShowScheduleError("Failed to save schedule.");
            }
        }
        catch (Exception ex)
        {
            ShowScheduleError($"Error: {ex.Message}");
        }
    }

    private async void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var currentPin = CurrentPinBox.Password;
            var newPin = NewPinBox.Password;
            var confirmPin = ConfirmPinBox.Password;

            if (string.IsNullOrEmpty(newPin) || newPin.Length != 4 || !newPin.All(char.IsDigit))
            {
                ShowChangePinError("New PIN must be exactly 4 digits");
                return;
            }

            if (newPin != confirmPin)
            {
                ShowChangePinError("New PINs do not match");
                return;
            }

            bool success;

            // In setup mode, use set_pin (no current PIN required)
            if (_currentState?.IsSetupMode == true)
            {
                success = await _ipcClient.SetPinAsync(newPin, confirmPin);
            }
            else
            {
                // Normal mode - require current PIN
                if (string.IsNullOrEmpty(currentPin) || currentPin.Length != 4)
                {
                    ShowChangePinError("Please enter your current 4-digit PIN");
                    return;
                }
                success = await _ipcClient.ChangePinAsync(currentPin, newPin);
            }

            if (success)
            {
                ChangePinErrorText.Visibility = Visibility.Collapsed;
                CurrentPinBox.Password = "";
                NewPinBox.Password = "";
                ConfirmPinBox.Password = "";
                _verifiedPin = newPin;

                var message = _currentState?.IsSetupMode == true
                    ? "PIN set successfully!"
                    : "PIN changed successfully!";
                MessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh UI after setting PIN (updates setup mode state)
                await LoadSettingsAsync();
            }
            else
            {
                var errorMsg = _currentState?.IsSetupMode == true
                    ? "Failed to set PIN."
                    : "Failed to change PIN. Check your current PIN.";
                ShowChangePinError(errorMsg);
            }
        }
        catch (Exception ex)
        {
            ShowChangePinError($"Error: {ex.Message}");
        }
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to reset all settings?\n\nThis will:\n- Clear your PIN\n- Reset the schedule to defaults\n- Cancel any active recovery\n\nYou will need to set up a new PIN.",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            // Use verified PIN from login, or empty for setup mode
            var pin = _verifiedPin ?? "";

            var success = await _ipcClient.ResetAllAsync(pin);

            if (success)
            {
                MessageBox.Show("All settings have been reset.", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                ShowResetError("Failed to reset.");
            }
        }
        catch (Exception ex)
        {
            ShowResetError($"Error: {ex.Message}");
        }
    }

    private async void CancelRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var success = await _ipcClient.CancelRecoveryAsync();

            if (success)
            {
                await LoadSettingsAsync();
            }
            else
            {
                MessageBox.Show("Failed to cancel recovery", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowAccessError(string message)
    {
        AccessErrorText.Text = message;
        AccessErrorText.Visibility = Visibility.Visible;
    }

    private void ShowScheduleError(string message)
    {
        ScheduleErrorText.Text = message;
        ScheduleErrorText.Visibility = Visibility.Visible;
    }

    private void ShowChangePinError(string message)
    {
        ChangePinErrorText.Text = message;
        ChangePinErrorText.Visibility = Visibility.Visible;
    }

    private void ShowResetError(string message)
    {
        ResetErrorText.Text = message;
        ResetErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopInactivityTimer();
        _lockoutTimer?.Stop();
        _lockoutTimer = null;
        InputManager.Current.PreProcessInput -= OnActivity;
        _ipcClient.Dispose();
        base.OnClosed(e);
    }
}
