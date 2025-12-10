using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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

    // Use simple pipe name without Global prefix - .NET handles this
    private static readonly string PipeNameSimple = Constants.PipeName.Replace(@"Global\", "");

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

    // Debug log file for troubleshooting IPC issues
    private static readonly string DebugLogPath = Path.Combine(Constants.ProgramDataPath, "ipc_debug.log");

    private void DebugLog(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(DebugLogPath, $"[{timestamp}] {message}\n");
        }
        catch { /* ignore logging errors */ }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        _logger.LogInformation("IPC Server starting on pipe: {PipeName}", PipeNameSimple);
        DebugLog($"IPC Server starting on pipe: {PipeNameSimple}");

        while (_running && !_cts.Token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Create pipe with security that allows all users to connect
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                server = NamedPipeServerStreamAcl.Create(
                    PipeNameSimple,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 0,
                    pipeSecurity);

                DebugLog("Waiting for client connection...");
                await server.WaitForConnectionAsync(_cts.Token);
                DebugLog("Client connected!");

                // Handle client - don't await, let it run in background
                var clientServer = server;
                server = null; // Prevent disposal in finally
                _ = Task.Run(() => HandleClientAsync(clientServer, _cts.Token), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                DebugLog("IPC server shutting down (cancelled)");
                break;
            }
            catch (Exception ex)
            {
                DebugLog($"Error in IPC server loop: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError(ex, "Error in IPC server loop");
                await Task.Delay(1000, _cts.Token);
            }
            finally
            {
                server?.Dispose();
            }
        }

        DebugLog("IPC Server stopped");
        _logger.LogInformation("IPC Server stopped");
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
    {
        DebugLog("HandleClientAsync started");
        try
        {
            DebugLog($"Server.IsConnected: {server.IsConnected}, CanRead: {server.CanRead}, CanWrite: {server.CanWrite}");

            // Read and write directly using the pipe stream to avoid StreamReader/StreamWriter issues
            var buffer = new byte[4096];
            var responseBuffer = new byte[8192];

            // Set a read timeout
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            readCts.CancelAfter(TimeSpan.FromSeconds(30));

            while (server.IsConnected && !token.IsCancellationRequested)
            {
                try
                {
                    DebugLog("Reading from pipe...");

                    // Read until we get a newline
                    var messageBuilder = new StringBuilder();
                    int bytesRead;

                    do
                    {
                        bytesRead = await server.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                        DebugLog($"Read {bytesRead} bytes");

                        if (bytesRead == 0)
                        {
                            DebugLog("Client disconnected (0 bytes read)");
                            return;
                        }

                        var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(chunk);

                    } while (bytesRead == buffer.Length && !messageBuilder.ToString().Contains('\n'));

                    var message = messageBuilder.ToString().TrimEnd('\r', '\n');
                    DebugLog($"Received message: {message}");

                    if (string.IsNullOrEmpty(message))
                    {
                        DebugLog("Empty message, closing connection");
                        return;
                    }

                    // Process request
                    string response;
                    try
                    {
                        response = ProcessRequest(message);
                        DebugLog($"ProcessRequest returned: {(response.Length > 100 ? response[..100] + "..." : response)}");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error processing request: {ex.GetType().Name}: {ex.Message}");
                        response = IpcSerializer.Serialize(new ErrorResponse($"Internal error: {ex.Message}"));
                    }

                    // Write response with newline
                    var responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                    DebugLog($"Writing {responseBytes.Length} bytes response...");

                    await server.WriteAsync(responseBytes, 0, responseBytes.Length, token);
                    await server.FlushAsync(token);

                    DebugLog("Response sent successfully");

                    // Reset timeout for next read
                    readCts.CancelAfter(TimeSpan.FromSeconds(30));
                }
                catch (OperationCanceledException)
                {
                    DebugLog("Read operation cancelled/timed out");
                    break;
                }
            }

            DebugLog("HandleClientAsync loop ended normally");
        }
        catch (IOException ex)
        {
            DebugLog($"IOException in HandleClientAsync: {ex.Message}");
        }
        catch (Exception ex)
        {
            DebugLog($"Exception in HandleClientAsync: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private string ProcessRequest(string json)
    {
        try
        {
            var request = IpcSerializer.DeserializeRequest(json);
            if (request == null)
            {
                _logger.LogWarning("Invalid IPC request format: {Json}", json);
                return IpcSerializer.Serialize(new ErrorResponse("Invalid request format"));
            }

            _logger.LogDebug("Processing IPC request: {Type}", request.Type);

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

            // Debug: log the response details before serialization
            DebugLog($"Response type: {response.GetType().Name}");
            if (response is AccessCheckResponse acr)
            {
                DebugLog($"AccessCheckResponse.Allowed = {acr.Allowed}");
            }

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
        // Verify PIN first (skip if in setup mode)
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
        // Always allow ConfigPanel access - if blocking is active, the blocker
        // will handle it. This prevents the ConfigPanel from being inaccessible
        // after a fresh install when the blocker isn't running yet.
        return new AccessCheckResponse { Allowed = true };
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
    }
}
