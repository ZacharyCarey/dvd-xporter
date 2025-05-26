using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DvdXporter.Core.Remote;

/// <param name="Address">Where to reach it, as "ip:port".</param>
public sealed record FoundRenderServer(string Address, RenderServerInfo Info);

/// <summary>Finds render servers on the local network: asks by multicast (and broadcast, for networks that drop multicast), and listens for answers.</summary>
public static class RenderServerDiscovery
{
    /// <summary>The servers that answered within <paramref name="wait"/>, each once.</summary>
    public static async Task<List<FoundRenderServer>> FindAsync(TimeSpan wait, CancellationToken cancellation = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.MulticastLoopback = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = Encoding.UTF8.GetBytes(RenderServerProtocol.DiscoveryProbe);
        foreach (var target in new[] { RenderServerProtocol.DiscoveryGroup, IPAddress.Broadcast })
        {
            try
            {
                await udp.SendAsync(probe, new IPEndPoint(target, RenderServerProtocol.DiscoveryPort), cancellation);
            }
            catch (SocketException)
            {
                // No route for one of them (no network, say); the other may still get there
            }
        }

        var found = new Dictionary<string, FoundRenderServer>();
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        listening.CancelAfter(wait);
        while (true)
        {
            UdpReceiveResult answer;
            try
            {
                answer = await udp.ReceiveAsync(listening.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            RenderServerInfo? info;
            try
            {
                info = JsonSerializer.Deserialize<RenderServerInfo>(answer.Buffer, RenderServerProtocol.Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (info is not null)
                found.TryAdd(info.Id, new FoundRenderServer($"{answer.RemoteEndPoint.Address}:{info.Port}", info));
        }

        return found.Values.OrderBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
