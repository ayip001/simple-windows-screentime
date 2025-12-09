using System.Security.Cryptography;
using SimpleWindowsScreentime.Shared.Configuration;

namespace SimpleWindowsScreentime.Shared.Security;

public class PinManager
{
    private readonly ConfigManager _configManager;

    public PinManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public void SetPin(string pin)
    {
        if (!IsValidPinFormat(pin))
        {
            throw new ArgumentException("PIN must be exactly 4 digits");
        }

        var salt = GenerateSalt();
        var hash = HashPin(pin, salt);

        _configManager.Update(config =>
        {
            config.PinSalt = Convert.ToBase64String(salt);
            config.PinHash = Convert.ToBase64String(hash);
            config.FailedAttempts = 0;
            config.ConsecutiveFailures = 0;
            config.LockoutUntilUtc = null;
            config.RecoveryActive = false;
            config.RecoveryExpiresUtc = null;
        });
    }

    public PinVerificationResult VerifyPin(string pin)
    {
        var config = _configManager.Config;

        // Check if in setup mode
        if (config.IsSetupMode)
        {
            return PinVerificationResult.SetupRequired;
        }

        // Check lockout
        if (config.LockoutUntilUtc.HasValue && DateTime.UtcNow < config.LockoutUntilUtc.Value)
        {
            var remaining = config.LockoutUntilUtc.Value - DateTime.UtcNow;
            return PinVerificationResult.LockedOut(remaining);
        }

        // Reset attempt counter if window has passed
        if (config.LastAttemptUtc.HasValue &&
            (DateTime.UtcNow - config.LastAttemptUtc.Value).TotalMinutes > Constants.AttemptWindowMinutes)
        {
            _configManager.Update(c =>
            {
                c.FailedAttempts = 0;
                c.LastAttemptUtc = DateTime.UtcNow;
            });
        }

        // Check rate limit
        if (config.FailedAttempts >= Constants.MaxPinAttempts)
        {
            return PinVerificationResult.RateLimited;
        }

        // Verify PIN
        try
        {
            var salt = Convert.FromBase64String(config.PinSalt!);
            var storedHash = Convert.FromBase64String(config.PinHash!);
            var inputHash = HashPin(pin, salt);

            if (CryptographicOperations.FixedTimeEquals(storedHash, inputHash))
            {
                // Reset counters on success
                _configManager.Update(c =>
                {
                    c.FailedAttempts = 0;
                    c.ConsecutiveFailures = 0;
                    c.LockoutUntilUtc = null;
                });
                return PinVerificationResult.Success;
            }
        }
        catch
        {
            // Hash comparison failed
        }

        // Record failed attempt
        _configManager.Update(c =>
        {
            c.FailedAttempts++;
            c.ConsecutiveFailures++;
            c.LastAttemptUtc = DateTime.UtcNow;

            // Check for lockout
            if (c.ConsecutiveFailures >= Constants.MaxConsecutiveFailures)
            {
                c.LockoutUntilUtc = DateTime.UtcNow.AddMinutes(Constants.LockoutMinutes);
                c.ConsecutiveFailures = 0; // Reset after triggering lockout
            }
        });

        var attemptsRemaining = Constants.MaxPinAttempts - _configManager.Config.FailedAttempts;
        return PinVerificationResult.InvalidPin(Math.Max(0, attemptsRemaining));
    }

    public void ChangePin(string currentPin, string newPin)
    {
        var result = VerifyPin(currentPin);
        if (!result.IsSuccess)
        {
            throw new UnauthorizedAccessException("Current PIN is incorrect");
        }

        SetPin(newPin);
    }

    public void ClearPin()
    {
        _configManager.Update(config =>
        {
            config.PinHash = null;
            config.PinSalt = null;
            config.FailedAttempts = 0;
            config.ConsecutiveFailures = 0;
            config.LockoutUntilUtc = null;
        });
    }

    public static bool IsValidPinFormat(string pin)
    {
        return !string.IsNullOrEmpty(pin) &&
               pin.Length == 4 &&
               pin.All(char.IsDigit);
    }

    private static byte[] GenerateSalt()
    {
        var salt = new byte[Constants.PinSaltLength];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    private static byte[] HashPin(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            pin,
            salt,
            Constants.PinIterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(Constants.PinHashLength);
    }
}

public class PinVerificationResult
{
    public bool IsSuccess { get; private init; }
    public bool IsLockedOut { get; private init; }
    public bool IsRateLimited { get; private init; }
    public bool IsSetupRequired { get; private init; }
    public int AttemptsRemaining { get; private init; }
    public TimeSpan? LockoutRemaining { get; private init; }

    public static PinVerificationResult Success => new() { IsSuccess = true };
    public static PinVerificationResult SetupRequired => new() { IsSetupRequired = true };
    public static PinVerificationResult RateLimited => new() { IsRateLimited = true };

    public static PinVerificationResult LockedOut(TimeSpan remaining) => new()
    {
        IsLockedOut = true,
        LockoutRemaining = remaining
    };

    public static PinVerificationResult InvalidPin(int attemptsRemaining) => new()
    {
        AttemptsRemaining = attemptsRemaining
    };
}
