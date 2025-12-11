using System.Text.Json.Serialization;

namespace SimpleWindowsScreentime.Shared.IPC;

// Base message types
public abstract class IpcRequest
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public abstract class IpcResponse
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

// Request types
public class GetStateRequest : IpcRequest
{
    public override string Type => "get_state";
}

public class VerifyPinRequest : IpcRequest
{
    public override string Type => "verify_pin";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;
}

public class UnlockRequest : IpcRequest
{
    public override string Type => "unlock";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public UnlockDuration Duration { get; set; }
}

public class SetPinRequest : IpcRequest
{
    public override string Type => "set_pin";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;

    [JsonPropertyName("confirm_pin")]
    public string ConfirmPin { get; set; } = string.Empty;
}

public class ChangePinRequest : IpcRequest
{
    public override string Type => "change_pin";

    [JsonPropertyName("current_pin")]
    public string CurrentPin { get; set; } = string.Empty;

    [JsonPropertyName("new_pin")]
    public string NewPin { get; set; } = string.Empty;
}

public class InitiateRecoveryRequest : IpcRequest
{
    public override string Type => "initiate_recovery";
}

public class CancelRecoveryRequest : IpcRequest
{
    public override string Type => "cancel_recovery";
}

public class GetConfigRequest : IpcRequest
{
    public override string Type => "get_config";
}

public class SetScheduleRequest : IpcRequest
{
    public override string Type => "set_schedule";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;

    [JsonPropertyName("block_start_minutes")]
    public int BlockStartMinutes { get; set; }

    [JsonPropertyName("block_end_minutes")]
    public int BlockEndMinutes { get; set; }
}

public class ResetAllRequest : IpcRequest
{
    public override string Type => "reset_all";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;
}

public class CheckAccessRequest : IpcRequest
{
    public override string Type => "check_access";
}

// Debug time manipulation requests
public class SetDebugTimeOffsetRequest : IpcRequest
{
    public override string Type => "debug_set_time_offset";

    [JsonPropertyName("offset_minutes")]
    public int OffsetMinutes { get; set; }
}

public class AdjustDebugTimeRequest : IpcRequest
{
    public override string Type => "debug_adjust_time";

    [JsonPropertyName("delta_minutes")]
    public int DeltaMinutes { get; set; }
}

public class ClearDebugTimeOffsetRequest : IpcRequest
{
    public override string Type => "debug_clear_time_offset";
}

public class GetDebugTimeInfoRequest : IpcRequest
{
    public override string Type => "debug_get_time_info";
}

// Response types
public class StateResponse : IpcResponse
{
    public override string Type => "state";

    [JsonPropertyName("is_blocking")]
    public bool IsBlocking { get; set; }

    [JsonPropertyName("is_setup_mode")]
    public bool IsSetupMode { get; set; }

    [JsonPropertyName("block_start_minutes")]
    public int BlockStartMinutes { get; set; }

    [JsonPropertyName("block_end_minutes")]
    public int BlockEndMinutes { get; set; }

    [JsonPropertyName("current_time_utc")]
    public DateTime CurrentTimeUtc { get; set; }

    [JsonPropertyName("trusted_time_utc")]
    public DateTime TrustedTimeUtc { get; set; }

    [JsonPropertyName("block_ends_at_local")]
    public DateTime? BlockEndsAtLocal { get; set; }

    [JsonPropertyName("temp_unlock_active")]
    public bool TempUnlockActive { get; set; }

    [JsonPropertyName("temp_unlock_expires_utc")]
    public DateTime? TempUnlockExpiresUtc { get; set; }

    [JsonPropertyName("recovery_active")]
    public bool RecoveryActive { get; set; }

    [JsonPropertyName("recovery_expires_utc")]
    public DateTime? RecoveryExpiresUtc { get; set; }

    [JsonPropertyName("is_locked_out")]
    public bool IsLockedOut { get; set; }

    [JsonPropertyName("lockout_until_utc")]
    public DateTime? LockoutUntilUtc { get; set; }

    [JsonPropertyName("failed_attempts")]
    public int FailedAttempts { get; set; }
}

public class PinResultResponse : IpcResponse
{
    public override string Type => "pin_result";

    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("is_locked_out")]
    public bool IsLockedOut { get; set; }

    [JsonPropertyName("is_rate_limited")]
    public bool IsRateLimited { get; set; }

    [JsonPropertyName("attempts_remaining")]
    public int AttemptsRemaining { get; set; }

    [JsonPropertyName("lockout_remaining_seconds")]
    public int? LockoutRemainingSeconds { get; set; }
}

public class ConfigResponse : IpcResponse
{
    public override string Type => "config";

    [JsonPropertyName("block_start_minutes")]
    public int BlockStartMinutes { get; set; }

    [JsonPropertyName("block_end_minutes")]
    public int BlockEndMinutes { get; set; }

    [JsonPropertyName("is_setup_mode")]
    public bool IsSetupMode { get; set; }

    [JsonPropertyName("recovery_active")]
    public bool RecoveryActive { get; set; }

    [JsonPropertyName("recovery_expires_utc")]
    public DateTime? RecoveryExpiresUtc { get; set; }
}

public class AckResponse : IpcResponse
{
    public override string Type => "ack";

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class ErrorResponse : IpcResponse
{
    public override string Type => "error";

    public ErrorResponse()
    {
        Success = false;
    }

    public ErrorResponse(string error)
    {
        Success = false;
        Error = error;
    }
}

public class AccessCheckResponse : IpcResponse
{
    public override string Type => "access_check";

    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public class DebugTimeInfoResponse : IpcResponse
{
    public override string Type => "debug_time_info";

    [JsonPropertyName("system_time_utc")]
    public DateTime SystemTimeUtc { get; set; }

    [JsonPropertyName("trusted_time_utc")]
    public DateTime TrustedTimeUtc { get; set; }

    [JsonPropertyName("trusted_time_local")]
    public DateTime TrustedTimeLocal { get; set; }

    [JsonPropertyName("ntp_offset_minutes")]
    public double NtpOffsetMinutes { get; set; }

    [JsonPropertyName("debug_offset_minutes")]
    public double DebugOffsetMinutes { get; set; }

    [JsonPropertyName("total_offset_minutes")]
    public double TotalOffsetMinutes { get; set; }
}

// Unlock duration enum
public enum UnlockDuration
{
    FifteenMinutes,
    OneHour,
    RestOfPeriod
}
