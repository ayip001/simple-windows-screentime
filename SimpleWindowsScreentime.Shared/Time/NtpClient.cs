using System.Net;
using System.Net.Sockets;

namespace SimpleWindowsScreentime.Shared.Time;

public static class NtpClient
{
    private const int NtpPort = 123;
    private const int NtpPacketSize = 48;
    private const int TimeoutMs = 5000;

    public static async Task<DateTime?> GetNetworkTimeAsync(string server)
    {
        try
        {
            var ntpData = new byte[NtpPacketSize];
            ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (client)

            var addresses = await Dns.GetHostAddressesAsync(server);
            if (addresses.Length == 0)
                return null;

            var ipEndPoint = new IPEndPoint(addresses[0], NtpPort);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveTimeout = TimeoutMs;
            socket.SendTimeout = TimeoutMs;

            await socket.ConnectAsync(ipEndPoint);
            await socket.SendAsync(ntpData, SocketFlags.None);

            var received = await socket.ReceiveAsync(ntpData, SocketFlags.None);
            if (received < NtpPacketSize)
                return null;

            // Extract timestamp from bytes 40-47 (transmit timestamp)
            ulong intPart = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16 |
                           (ulong)ntpData[42] << 8 | ntpData[43];
            ulong fractPart = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16 |
                             (ulong)ntpData[46] << 8 | ntpData[47];

            var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);

            // NTP timestamp starts from 1900-01-01
            var ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var networkTime = ntpEpoch.AddMilliseconds((long)milliseconds);

            return networkTime;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<TimeSpan?> GetTimeOffsetAsync(string[]? servers = null)
    {
        servers ??= Constants.NtpServers;

        foreach (var server in servers)
        {
            try
            {
                var beforeRequest = DateTime.UtcNow;
                var networkTime = await GetNetworkTimeAsync(server);
                var afterRequest = DateTime.UtcNow;

                if (networkTime.HasValue)
                {
                    // Account for round-trip time
                    var roundTripTime = afterRequest - beforeRequest;
                    var localTime = beforeRequest + TimeSpan.FromTicks(roundTripTime.Ticks / 2);
                    var offset = networkTime.Value - localTime;

                    return offset;
                }
            }
            catch
            {
                // Try next server
            }
        }

        return null;
    }
}
