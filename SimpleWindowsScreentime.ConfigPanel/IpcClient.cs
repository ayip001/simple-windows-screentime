using System.IO;
using System.IO.Pipes;
using System.Text;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.IPC;

namespace SimpleWindowsScreentime.ConfigPanel;

public class IpcClient : IDisposable
{
    private const int TimeoutMs = 5000;
    // UTF8 encoding WITHOUT BOM - critical for pipe communication
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public async Task<StateResponse?> GetStateAsync()
    {
        var request = new GetStateRequest();
        return await SendRequestAsync<StateResponse>(request);
    }

    public async Task<AccessCheckResponse?> CheckAccessAsync()
    {
        var request = new CheckAccessRequest();
        return await SendRequestAsync<AccessCheckResponse>(request);
    }

    public async Task<PinResultResponse?> VerifyPinAsync(string pin)
    {
        var request = new VerifyPinRequest { Pin = pin };
        return await SendRequestAsync<PinResultResponse>(request);
    }

    public async Task<ConfigResponse?> GetConfigAsync()
    {
        var request = new GetConfigRequest();
        return await SendRequestAsync<ConfigResponse>(request);
    }

    public async Task<bool> SetScheduleAsync(string pin, int startMinutes, int endMinutes)
    {
        var request = new SetScheduleRequest
        {
            Pin = pin,
            BlockStartMinutes = startMinutes,
            BlockEndMinutes = endMinutes
        };
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> ChangePinAsync(string currentPin, string newPin)
    {
        var request = new ChangePinRequest
        {
            CurrentPin = currentPin,
            NewPin = newPin
        };
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> ResetAllAsync(string pin)
    {
        var request = new ResetAllRequest { Pin = pin };
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> CancelRecoveryAsync()
    {
        var request = new CancelRecoveryRequest();
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    private async Task<T?> SendRequestAsync<T>(IpcRequest request) where T : IpcResponse
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                Constants.PipeName.Replace(@"Global\", ""),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource(TimeoutMs);
            await client.ConnectAsync(cts.Token);

            using var reader = new StreamReader(client, Utf8NoBom, leaveOpen: true);
            using var writer = new StreamWriter(client, Utf8NoBom, leaveOpen: true);
            writer.AutoFlush = true;

            var json = IpcSerializer.Serialize(request);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
            await client.FlushAsync(); // Critical: flush the pipe stream

            // Add timeout to ReadLineAsync to prevent indefinite hanging
            using var readCts = new CancellationTokenSource(TimeoutMs);
            var responseLine = await reader.ReadLineAsync(readCts.Token);

            if (string.IsNullOrEmpty(responseLine))
                return null;

            return IpcSerializer.Deserialize<T>(responseLine);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Nothing to dispose currently
    }
}
