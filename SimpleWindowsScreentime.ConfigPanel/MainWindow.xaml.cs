using System.Windows;
using System.Windows.Controls;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.ConfigPanel;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipcClient;
    private string? _verifiedPin;
    private StateResponse? _currentState;

    public MainWindow()
    {
        InitializeComponent();
        _ipcClient = new IpcClient();

        Loaded += MainWindow_Loaded;
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

    private void ShowPinEntry()
    {
        PinEntryView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        AccessDeniedView.Visibility = Visibility.Collapsed;
        AccessPinBox.Focus();
    }

    private void ShowSettings()
    {
        PinEntryView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        AccessDeniedView.Visibility = Visibility.Collapsed;
    }

    private void ShowAccessDenied()
    {
        PinEntryView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        AccessDeniedView.Visibility = Visibility.Visible;
    }

    private async void AccessPinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AccessPinBox.Password.Length == 4)
        {
            await VerifyAccessPinAsync();
        }
        else
        {
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
                ShowAccessError($"Too many attempts. Try again in {result.LockoutRemainingSeconds / 60} minutes");
                AccessPinBox.Password = "";
            }
            else if (result.IsRateLimited)
            {
                ShowAccessError("Too many attempts. Please wait.");
                AccessPinBox.Password = "";
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

            // For setup mode, use empty PIN (server will allow it)
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
                ShowScheduleError("Failed to save schedule");
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

            if (string.IsNullOrEmpty(currentPin) || currentPin.Length != 4)
            {
                ShowChangePinError("Please enter your current 4-digit PIN");
                return;
            }

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

            var success = await _ipcClient.ChangePinAsync(currentPin, newPin);

            if (success)
            {
                ChangePinErrorText.Visibility = Visibility.Collapsed;
                CurrentPinBox.Password = "";
                NewPinBox.Password = "";
                ConfirmPinBox.Password = "";
                _verifiedPin = newPin;
                MessageBox.Show("PIN changed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ShowChangePinError("Failed to change PIN. Check your current PIN.");
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
            var pin = ResetPinBox.Password;

            if (string.IsNullOrEmpty(pin) || pin.Length != 4)
            {
                ShowResetError("Please enter your PIN to confirm reset");
                return;
            }

            var success = await _ipcClient.ResetAllAsync(pin);

            if (success)
            {
                MessageBox.Show("All settings have been reset.", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                ShowResetError("Failed to reset. Check your PIN.");
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
        _ipcClient.Dispose();
        base.OnClosed(e);
    }
}
