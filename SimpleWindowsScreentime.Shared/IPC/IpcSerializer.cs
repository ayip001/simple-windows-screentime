using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleWindowsScreentime.Shared.IPC;

public static class IpcSerializer
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T message) where T : class
    {
        return JsonSerializer.Serialize(message, _options);
    }

    public static IpcRequest? DeserializeRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return null;
            }

            var type = typeElement.GetString();

            return type switch
            {
                "get_state" => JsonSerializer.Deserialize<GetStateRequest>(json, _options),
                "verify_pin" => JsonSerializer.Deserialize<VerifyPinRequest>(json, _options),
                "unlock" => JsonSerializer.Deserialize<UnlockRequest>(json, _options),
                "set_pin" => JsonSerializer.Deserialize<SetPinRequest>(json, _options),
                "change_pin" => JsonSerializer.Deserialize<ChangePinRequest>(json, _options),
                "initiate_recovery" => JsonSerializer.Deserialize<InitiateRecoveryRequest>(json, _options),
                "cancel_recovery" => JsonSerializer.Deserialize<CancelRecoveryRequest>(json, _options),
                "get_config" => JsonSerializer.Deserialize<GetConfigRequest>(json, _options),
                "set_schedule" => JsonSerializer.Deserialize<SetScheduleRequest>(json, _options),
                "reset_all" => JsonSerializer.Deserialize<ResetAllRequest>(json, _options),
                "check_access" => JsonSerializer.Deserialize<CheckAccessRequest>(json, _options),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _options);
        }
        catch
        {
            return null;
        }
    }
}
