using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DvdXporter.Core.Remote;

namespace DvdXporter.Server;

/// <summary>Answers the app's probes for render servers on the local network (see <see cref="RenderServerDiscovery"/>).</summary>
public static class DiscoveryResponder
{
    public static async Task RunAsync(ServerConfig config, CancellationToken stopping)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, RenderServerProtocol.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"The app won't find this server by itself (UDP port {RenderServerProtocol.DiscoveryPort}: {ex.Message}). Enter its address in the app instead.");
            return;
        }

        // On every network it's on, as the default one may not be the one the app's on
        foreach (var address in LocalAddresses())
        {
            try
            {
                udp.JoinMulticastGroup(RenderServerProtocol.DiscoveryGroup, address);
            }
            catch (SocketException)
            {
            }
        }

        var answer = JsonSerializer.SerializeToUtf8Bytes(ServerApi.Info(config), RenderServerProtocol.Json);
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var probe = await udp.ReceiveAsync(stopping);
                if (Encoding.UTF8.GetString(probe.Buffer) == RenderServerProtocol.DiscoveryProbe)
                    await udp.SendAsync(answer, probe.RemoteEndPoint, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    /// <summary>This computer's IPv4 addresses on networks that are up, for saying where to reach it.</summary>
    public static List<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .ToList();
}
