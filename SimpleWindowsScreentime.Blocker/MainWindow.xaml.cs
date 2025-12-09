using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Blocker;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipcClient;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _focusTimer;
    private StateResponse? _currentState;
    private bool _pinValidated;

    public MainWindow()
    {
        InitializeComponent();

        _ipcClient = new IpcClient();

        // Timer to update display every second
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        // Timer to maintain focus every 100ms
        _focusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _focusTimer.Tick += FocusTimer_Tick;

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Cover all screens
        CoverAllScreens();

        // Start timers
        _updateTimer.Start();
        _focusTimer.Start();

        // Get initial state
        await UpdateStateAsync();
    }

    private void CoverAllScreens()
    {
        // Get virtual screen bounds (covers all monitors)
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var width = SystemParameters.VirtualScreenWidth;
        var height = SystemParameters.VirtualScreenHeight;

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        WindowState = WindowState.Normal;
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        // Immediately reactivate
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Activate();
            Focus();
            Topmost = true;
        });
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            CoverAllScreens();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Block certain key combinations at WPF level too
        if (e.Key == Key.System || e.Key == Key.LWin || e.Key == Key.RWin)
        {
            e.Handled = true;
        }
    }

    private void FocusTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive)
        {
            Activate();
            Topmost = true;
        }
        Focus();

        // Re-cover screens in case of display changes
        if (Width != SystemParameters.VirtualScreenWidth ||
            Height != SystemParameters.VirtualScreenHeight)
        {
            CoverAllScreens();
        }
    }

    private async void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateStateAsync();
    }

    private async Task UpdateStateAsync()
    {
        try
        {
            _currentState = await _ipcClient.GetStateAsync();

            if (_currentState == null)
            {
                // Cannot connect to service
                ShowError("Cannot connect to service");
                return;
            }

            // Check if we should show setup mode
            if (_currentState.IsSetupMode)
            {
                ShowSetupView();
            }
            else
            {
                ShowBlockingView();
            }

            UpdateDisplay();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private void ShowSetupView()
    {
        BlockingView.Visibility = Visibility.Collapsed;
        SetupView.Visibility = Visibility.Visible;
    }

    private void ShowBlockingView()
    {
        SetupView.Visibility = Visibility.Collapsed;
        BlockingView.Visibility = Visibility.Visible;
    }

    private void UpdateDisplay()
    {
        if (_currentState == null) return;

        // Update time display
        if (_currentState.BlockEndsAtLocal.HasValue)
        {
            UnblockTimeText.Text = $"Unblocks at: {_currentState.BlockEndsAtLocal.Value:h:mm tt}";

            var remaining = _currentState.BlockEndsAtLocal.Value.ToUniversalTime() - _currentState.TrustedTimeUtc;
            if (remaining > TimeSpan.Zero)
            {
                TimeRemainingText.Text = $"Time remaining: {ScheduleChecker.FormatTimeSpan(remaining)}";
            }
            else
            {
                TimeRemainingText.Text = "Block period ending...";
            }
        }
        else
        {
            UnblockTimeText.Text = $"Block time: {ScheduleChecker.FormatMinutesAsTime(_currentState.BlockStartMinutes)} - {ScheduleChecker.FormatMinutesAsTime(_currentState.BlockEndMinutes)}";
            TimeRemainingText.Text = "";
        }

        // Update recovery banner
        if (_currentState.RecoveryActive && _currentState.RecoveryExpiresUtc.HasValue)
        {
            RecoveryBanner.Visibility = Visibility.Visible;
            var remaining = _currentState.RecoveryExpiresUtc.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                RecoveryText.Text = $"Recovery active: {ScheduleChecker.FormatTimeSpan(remaining)} remaining";
            }
            else
            {
                RecoveryText.Text = "Recovery completing...";
            }
            ForgotPinButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            RecoveryBanner.Visibility = Visibility.Collapsed;
            ForgotPinButton.Visibility = Visibility.Visible;
        }

        // Update lockout status
        if (_currentState.IsLockedOut && _currentState.LockoutUntilUtc.HasValue)
        {
            var remaining = _currentState.LockoutUntilUtc.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                ShowError($"Too many attempts. Try again in {remaining.Minutes}m {remaining.Seconds}s");
                PinBox.IsEnabled = false;
            }
        }
        else
        {
            PinBox.IsEnabled = true;
        }
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        HideError();

        if (PinBox.Password.Length == 4)
        {
            ValidatePinAsync();
        }
        else
        {
            UnlockButtons.Visibility = Visibility.Collapsed;
            _pinValidated = false;
        }
    }

    private async void ValidatePinAsync()
    {
        try
        {
            var result = await _ipcClient.VerifyPinAsync(PinBox.Password);

            if (result == null)
            {
                ShowError("Cannot connect to service");
                return;
            }

            if (result.Valid)
            {
                _pinValidated = true;
                UnlockButtons.Visibility = Visibility.Visible;
                HideError();
            }
            else if (result.IsLockedOut)
            {
                ShowError($"Too many attempts. Locked out for {result.LockoutRemainingSeconds / 60} minutes");
                PinBox.Password = "";
            }
            else if (result.IsRateLimited)
            {
                ShowError("Too many attempts. Please wait.");
                PinBox.Password = "";
            }
            else
            {
                ShowError($"Invalid PIN ({result.AttemptsRemaining} attempts remaining)");
                PinBox.Password = "";
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private async void Unlock15Min_Click(object sender, RoutedEventArgs e)
    {
        await RequestUnlockAsync(UnlockDuration.FifteenMinutes);
    }

    private async void Unlock1Hour_Click(object sender, RoutedEventArgs e)
    {
        await RequestUnlockAsync(UnlockDuration.OneHour);
    }

    private async void UnlockPeriod_Click(object sender, RoutedEventArgs e)
    {
        await RequestUnlockAsync(UnlockDuration.RestOfPeriod);
    }

    private async Task RequestUnlockAsync(UnlockDuration duration)
    {
        if (!_pinValidated || string.IsNullOrEmpty(PinBox.Password))
        {
            ShowError("Please enter your PIN first");
            return;
        }

        try
        {
            var success = await _ipcClient.RequestUnlockAsync(PinBox.Password, duration);

            if (success)
            {
                // Service will kill this process
                Application.Current.Shutdown();
            }
            else
            {
                ShowError("Unlock request failed");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private async void ForgotPin_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will start a 48-hour recovery process.\n\nAfter 48 hours, your PIN will be cleared and you can set a new one.\n\nDuring this time, the blocker will continue to function.\n\nContinue?",
            "Start Recovery",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var success = await _ipcClient.InitiateRecoveryAsync();
                if (success)
                {
                    await UpdateStateAsync();
                }
                else
                {
                    ShowError("Failed to initiate recovery");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error: {ex.Message}");
            }
        }
    }

    private async void CancelRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var success = await _ipcClient.CancelRecoveryAsync();
            if (success)
            {
                await UpdateStateAsync();
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private async void SetupPin_Click(object sender, RoutedEventArgs e)
    {
        var pin = SetupPinBox.Password;
        var confirmPin = SetupConfirmPinBox.Password;

        if (pin.Length != 4 || !pin.All(char.IsDigit))
        {
            ShowSetupError("PIN must be exactly 4 digits");
            return;
        }

        if (pin != confirmPin)
        {
            ShowSetupError("PINs do not match");
            return;
        }

        try
        {
            var success = await _ipcClient.SetPinAsync(pin, confirmPin);

            if (success)
            {
                SetupPinBox.Password = "";
                SetupConfirmPinBox.Password = "";
                await UpdateStateAsync();
            }
            else
            {
                ShowSetupError("Failed to set PIN");
            }
        }
        catch (Exception ex)
        {
            ShowSetupError($"Error: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        PinErrorText.Text = message;
        PinErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        PinErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowSetupError(string message)
    {
        SetupErrorText.Text = message;
        SetupErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateTimer.Stop();
        _focusTimer.Stop();
        _ipcClient.Dispose();
        base.OnClosed(e);
    }
}
