using System.IO;
using System.IO.Pipes;
using System.Text;
using SimpleWindowsScreentime.Shared;
using SimpleWindowsScreentime.Shared.IPC;

namespace SimpleWindowsScreentime.Blocker;

public class IpcClient : IDisposable
{
    private const int TimeoutMs = 5000;
    // UTF8 encoding WITHOUT BOM - critical for pipe communication
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public async Task<StateResponse?> GetStateAsync()
    {
        var request = new GetStateRequest();
        var response = await SendRequestAsync<StateResponse>(request);
        return response;
    }

    public async Task<PinResultResponse?> VerifyPinAsync(string pin)
    {
        var request = new VerifyPinRequest { Pin = pin };
        var response = await SendRequestAsync<PinResultResponse>(request);
        return response;
    }

    public async Task<bool> RequestUnlockAsync(string pin, UnlockDuration duration)
    {
        var request = new UnlockRequest { Pin = pin, Duration = duration };
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> InitiateRecoveryAsync()
    {
        var request = new InitiateRecoveryRequest();
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> CancelRecoveryAsync()
    {
        var request = new CancelRecoveryRequest();
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<bool> SetPinAsync(string pin, string confirmPin)
    {
        var request = new SetPinRequest { Pin = pin, ConfirmPin = confirmPin };
        var response = await SendRequestAsync<AckResponse>(request);
        return response?.Success ?? false;
    }

    public async Task<ConfigResponse?> GetConfigAsync()
    {
        var request = new GetConfigRequest();
        return await SendRequestAsync<ConfigResponse>(request);
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
