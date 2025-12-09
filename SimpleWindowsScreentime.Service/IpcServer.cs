using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.Configuration;
using SimpleWindowsScreentime.Shared.IPC;
using SimpleWindowsScreentime.Shared.Security;
using SimpleWindowsScreentime.Shared.Time;

namespace SimpleWindowsScreentime.Service;

public class IpcServer
{
    private readonly ILogger<IpcServer> _logger;
    private readonly ConfigManager _configManager;
    private readonly PinManager _pinManager;
    private readonly ScheduleChecker _scheduleChecker;
    private readonly RecoveryManager _recoveryManager;
    private readonly UnlockManager _unlockManager;
    private readonly BlockerProcessManager _blockerManager;

    private CancellationTokenSource? _cts;
    private bool _running;

    public IpcServer(
        ILogger<IpcServer> logger,
        ConfigManager configManager,
        PinManager pinManager,
        ScheduleChecker scheduleChecker,
        RecoveryManager recoveryManager,
        UnlockManager unlockManager,
        BlockerProcessManager blockerManager)
    {
        _logger = logger;
        _configManager = configManager;
        _pinManager = pinManager;
        _scheduleChecker = scheduleChecker;
        _recoveryManager = recoveryManager;
        _unlockManager = unlockManager;
        _blockerManager = blockerManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        _logger.LogInformation("IPC Server starting on pipe: {PipeName}", Constants.PipeName);

        while (_running && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    Constants.PipeName.Replace(@"Global\", ""),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);

                // Handle client in background
                _ = HandleClientAsync(server, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in IPC server loop");
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            while (server.IsConnected && !token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrEmpty(line))
                    break;

                var response = ProcessRequest(line);
                await writer.WriteLineAsync(response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling IPC client");
        }
    }

    private string ProcessRequest(string json)
    {
        try
        {
            var request = IpcSerializer.DeserializeRequest(json);
            if (request == null)
            {
                return IpcSerializer.Serialize(new ErrorResponse("Invalid request format"));
            }

            var response = request switch
            {
                GetStateRequest => HandleGetState(),
                VerifyPinRequest r => HandleVerifyPin(r),
                UnlockRequest r => HandleUnlock(r),
                SetPinRequest r => HandleSetPin(r),
                ChangePinRequest r => HandleChangePin(r),
                InitiateRecoveryRequest => HandleInitiateRecovery(),
                CancelRecoveryRequest => HandleCancelRecovery(),
                GetConfigRequest => HandleGetConfig(),
                SetScheduleRequest r => HandleSetSchedule(r),
                ResetAllRequest r => HandleResetAll(r),
                CheckAccessRequest => HandleCheckAccess(),
                _ => new ErrorResponse("Unknown request type")
            };

            return IpcSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing IPC request");
            return IpcSerializer.Serialize(new ErrorResponse($"Internal error: {ex.Message}"));
        }
    }

    private IpcResponse HandleGetState()
    {
        var config = _configManager.Config;
        var trustedTime = _scheduleChecker.GetTrustedTimeUtc();
        var isBlocking = _scheduleChecker.IsWithinBlockWindow() && !_unlockManager.HasActiveUnlock();

        return new StateResponse
        {
            IsBlocking = isBlocking,
            IsSetupMode = config.IsSetupMode,
            BlockStartMinutes = config.BlockStartMinutes,
            BlockEndMinutes = config.BlockEndMinutes,
            CurrentTimeUtc = DateTime.UtcNow,
            TrustedTimeUtc = trustedTime,
            BlockEndsAtLocal = isBlocking ? _scheduleChecker.GetBlockEndTimeLocal() : null,
            TempUnlockActive = _unlockManager.HasActiveUnlock(),
            TempUnlockExpiresUtc = config.TempUnlockExpiresUtc,
            RecoveryActive = config.RecoveryActive,
            RecoveryExpiresUtc = config.RecoveryExpiresUtc,
            IsLockedOut = config.LockoutUntilUtc.HasValue && DateTime.UtcNow < config.LockoutUntilUtc.Value,
            LockoutUntilUtc = config.LockoutUntilUtc,
            FailedAttempts = config.FailedAttempts
        };
    }

    private IpcResponse HandleVerifyPin(VerifyPinRequest request)
    {
        var result = _pinManager.VerifyPin(request.Pin);

        return new PinResultResponse
        {
            Valid = result.IsSuccess,
            IsLockedOut = result.IsLockedOut,
            IsRateLimited = result.IsRateLimited,
            AttemptsRemaining = result.AttemptsRemaining,
            LockoutRemainingSeconds = result.LockoutRemaining.HasValue
                ? (int)result.LockoutRemaining.Value.TotalSeconds
                : null
        };
    }

    private IpcResponse HandleUnlock(UnlockRequest request)
    {
        var result = _pinManager.VerifyPin(request.Pin);

        if (!result.IsSuccess)
        {
            return new PinResultResponse
            {
                Success = false,
                Valid = false,
                IsLockedOut = result.IsLockedOut,
                IsRateLimited = result.IsRateLimited,
                AttemptsRemaining = result.AttemptsRemaining,
                LockoutRemainingSeconds = result.LockoutRemaining.HasValue
                    ? (int)result.LockoutRemaining.Value.TotalSeconds
                    : null,
                Error = "Invalid PIN"
            };
        }

        _unlockManager.GrantUnlock(request.Duration);
        _blockerManager.KillBlocker();

        return new AckResponse { Message = "Unlock granted" };
    }

    private IpcResponse HandleSetPin(SetPinRequest request)
    {
        if (request.Pin != request.ConfirmPin)
        {
            return new ErrorResponse("PINs do not match");
        }

        if (!PinManager.IsValidPinFormat(request.Pin))
        {
            return new ErrorResponse("PIN must be exactly 4 digits");
        }

        // Only allow in setup mode
        if (!_configManager.Config.IsSetupMode)
        {
            return new ErrorResponse("Cannot set PIN - not in setup mode. Use change_pin instead.");
        }

        _pinManager.SetPin(request.Pin);
        return new AckResponse { Message = "PIN set successfully" };
    }

    private IpcResponse HandleChangePin(ChangePinRequest request)
    {
        if (!PinManager.IsValidPinFormat(request.NewPin))
        {
            return new ErrorResponse("New PIN must be exactly 4 digits");
        }

        var result = _pinManager.VerifyPin(request.CurrentPin);
        if (!result.IsSuccess)
        {
            return new PinResultResponse
            {
                Success = false,
                Valid = false,
                IsLockedOut = result.IsLockedOut,
                IsRateLimited = result.IsRateLimited,
                AttemptsRemaining = result.AttemptsRemaining,
                Error = "Current PIN is incorrect"
            };
        }

        _pinManager.SetPin(request.NewPin);
        return new AckResponse { Message = "PIN changed successfully" };
    }

    private IpcResponse HandleInitiateRecovery()
    {
        if (_recoveryManager.IsRecoveryActive)
        {
            return new ErrorResponse("Recovery is already active");
        }

        _recoveryManager.InitiateRecovery();
        return new AckResponse { Message = "Recovery initiated - PIN will be cleared in 48 hours" };
    }

    private IpcResponse HandleCancelRecovery()
    {
        _recoveryManager.CancelRecovery();
        return new AckResponse { Message = "Recovery cancelled" };
    }

    private IpcResponse HandleGetConfig()
    {
        var config = _configManager.Config;

        return new ConfigResponse
        {
            BlockStartMinutes = config.BlockStartMinutes,
            BlockEndMinutes = config.BlockEndMinutes,
            IsSetupMode = config.IsSetupMode,
            RecoveryActive = config.RecoveryActive,
            RecoveryExpiresUtc = config.RecoveryExpiresUtc
        };
    }

    private IpcResponse HandleSetSchedule(SetScheduleRequest request)
    {
        // Verify PIN first
        var result = _pinManager.VerifyPin(request.Pin);
        if (!result.IsSuccess)
        {
            return new PinResultResponse
            {
                Success = false,
                Valid = false,
                IsLockedOut = result.IsLockedOut,
                IsRateLimited = result.IsRateLimited,
                AttemptsRemaining = result.AttemptsRemaining,
                Error = "Invalid PIN"
            };
        }

        // Validate schedule
        if (request.BlockStartMinutes < 0 || request.BlockStartMinutes >= 1440 ||
            request.BlockEndMinutes < 0 || request.BlockEndMinutes >= 1440)
        {
            return new ErrorResponse("Invalid schedule times");
        }

        _configManager.Update(config =>
        {
            config.BlockStartMinutes = request.BlockStartMinutes;
            config.BlockEndMinutes = request.BlockEndMinutes;
        });

        return new AckResponse { Message = "Schedule updated successfully" };
    }

    private IpcResponse HandleResetAll(ResetAllRequest request)
    {
        // Verify PIN first (unless in setup mode)
        if (!_configManager.Config.IsSetupMode)
        {
            var result = _pinManager.VerifyPin(request.Pin);
            if (!result.IsSuccess)
            {
                return new PinResultResponse
                {
                    Success = false,
                    Valid = false,
                    IsLockedOut = result.IsLockedOut,
                    IsRateLimited = result.IsRateLimited,
                    AttemptsRemaining = result.AttemptsRemaining,
                    Error = "Invalid PIN"
                };
            }
        }

        _configManager.Reset();
        return new AckResponse { Message = "All settings reset" };
    }

    private IpcResponse HandleCheckAccess()
    {
        var isBlocking = _scheduleChecker.IsWithinBlockWindow();
        var hasUnlock = _unlockManager.HasActiveUnlock();

        if (isBlocking && !hasUnlock)
        {
            return new AccessCheckResponse
            {
                Allowed = false,
                Reason = "Currently in block period"
            };
        }

        return new AccessCheckResponse { Allowed = true };
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
    }
}
