using System.Text.Json.Serialization;

namespace SimpleWindowsScreentime.Shared.Configuration;

public class AppConfig
{
    [JsonPropertyName("pin_hash")]
    public string? PinHash { get; set; }

    [JsonPropertyName("pin_salt")]
    public string? PinSalt { get; set; }

    [JsonPropertyName("block_start_minutes")]
    public int BlockStartMinutes { get; set; } = Constants.DefaultBlockStartMinutes;

    [JsonPropertyName("block_end_minutes")]
    public int BlockEndMinutes { get; set; } = Constants.DefaultBlockEndMinutes;

    [JsonPropertyName("time_offset_ticks")]
    public long TimeOffsetTicks { get; set; }

    [JsonPropertyName("debug_time_offset_ticks")]
    public long DebugTimeOffsetTicks { get; set; }

    [JsonPropertyName("last_ntp_sync_utc")]
    public DateTime? LastNtpSyncUtc { get; set; }

    [JsonPropertyName("recovery_active")]
    public bool RecoveryActive { get; set; }

    [JsonPropertyName("recovery_expires_utc")]
    public DateTime? RecoveryExpiresUtc { get; set; }

    [JsonPropertyName("failed_attempts")]
    public int FailedAttempts { get; set; }

    [JsonPropertyName("lockout_until_utc")]
    public DateTime? LockoutUntilUtc { get; set; }

    [JsonPropertyName("temp_unlock_expires_utc")]
    public DateTime? TempUnlockExpiresUtc { get; set; }

    [JsonPropertyName("last_attempt_utc")]
    public DateTime? LastAttemptUtc { get; set; }

    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; set; }

    [JsonIgnore]
    public bool IsSetupMode => string.IsNullOrEmpty(PinHash) || string.IsNullOrEmpty(PinSalt);

    [JsonIgnore]
    public TimeSpan TimeOffset => TimeSpan.FromTicks(TimeOffsetTicks);

    public AppConfig Clone()
    {
        return new AppConfig
        {
            PinHash = PinHash,
            PinSalt = PinSalt,
            BlockStartMinutes = BlockStartMinutes,
            BlockEndMinutes = BlockEndMinutes,
            TimeOffsetTicks = TimeOffsetTicks,
            DebugTimeOffsetTicks = DebugTimeOffsetTicks,
            LastNtpSyncUtc = LastNtpSyncUtc,
            RecoveryActive = RecoveryActive,
            RecoveryExpiresUtc = RecoveryExpiresUtc,
            FailedAttempts = FailedAttempts,
            LockoutUntilUtc = LockoutUntilUtc,
            TempUnlockExpiresUtc = TempUnlockExpiresUtc,
            LastAttemptUtc = LastAttemptUtc,
            ConsecutiveFailures = ConsecutiveFailures
        };
    }

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            BlockStartMinutes = Constants.DefaultBlockStartMinutes,
            BlockEndMinutes = Constants.DefaultBlockEndMinutes
        };
    }
}
