using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Serves video straight off discs (DVD cells, clips inside Blu-ray images) to ffmpeg over HTTP on this
/// computer only, so nothing has to be copied out first. ffmpeg seeks with range requests, so every request
/// opens its own stream at the asked-for offset.
/// </summary>
/// <remarks>
/// Listens on 127.0.0.1 at a random port, and every address carries a random token, so other programs on the
/// computer can't guess their way in. Addresses stay valid while the app runs.
/// </remarks>
public sealed class ClipStreamServer : IDisposable
{
    private const int BufferSize = 256 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;

    private static readonly Lazy<ClipStreamServer> SharedServer = new(() => new ClipStreamServer());

    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly ConcurrentDictionary<string, Func<Stream>> _streams = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The app's server, started the first time it's needed.</summary>
    public static ClipStreamServer Shared => SharedServer.Value;

    private ClipStreamServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// The address that serves the stream <paramref name="open"/> makes (one per request; it must be seekable
    /// and know its length). The same <paramref name="key"/> always gets the same address.
    /// </summary>
    /// <param name="name">The file name at the end of the address, e.g. "VTS_01_TitleCell_01-01.vob".</param>
    public Uri Register(string key, string name, Func<Stream> open)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        _streams[id] = open;
        return new Uri($"http://127.0.0.1:{Port}/{_token}/{id}/{Uri.EscapeDataString(name)}");
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var network = client.GetStream();
                // ffmpeg can ask for several ranges on one connection
                Request? request;
                while ((request = await ReadRequest(network)) is not null)
                {
                    if (!await Respond(network, request))
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // ffmpeg closes the connection when it seeks or finishes; that's normal
            }
        }
    }

    private sealed record Request(string Method, string Path, long? RangeStart, long? RangeEnd, bool KeepAlive);

    private static async Task<Request?> ReadRequest(NetworkStream network)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < MaxHeaderBytes)
        {
            if (await network.ReadAsync(one) == 0)
                return null;
            header.Add(one[0]);
            int n = header.Count;
            if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n')
                break;
        }

        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3)
            return null;

        long? start = null, end = null;
        bool keepAlive = parts[2] == "HTTP/1.1";
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase) && value.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var range = value["bytes=".Length..].Split(',')[0].Split('-');
                if (long.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                    start = s;
                if (range.Length > 1 && long.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var e))
                    end = e;
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = value.Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
            }
        }

        return new Request(parts[0], parts[1], start, end, keepAlive);
    }

    /// <summary>Sends the response; returns whether the connection can take another request.</summary>
    private async Task<bool> Respond(NetworkStream network, Request request)
    {
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != _token || !_streams.TryGetValue(segments[1], out var open)
            || request.Method is not ("GET" or "HEAD"))
        {
            await WriteHeader(network, "404 Not Found", 0, null, keepAlive: false);
            return false;
        }

        Stream stream;
        try
        {
            stream = open();
        }
        catch (Exception ex)
        {
            await WriteHeader(network, "500 Internal Server Error", 0, null, keepAlive: false, ex.Message);
            return false;
        }

        await using (stream)
        {
            long length = stream.Length;
            long start = request.RangeStart ?? 0;
            long end = Math.Min(request.RangeEnd ?? length - 1, length - 1);
            if (request.RangeStart is not null && start >= length)
            {
                await WriteHeader(network, "416 Range Not Satisfiable", 0, $"bytes */{length}", request.KeepAlive);
                return request.KeepAlive;
            }

            var partial = request.RangeStart is not null || request.RangeEnd is not null;
            long count = Math.Max(0, end - start + 1);
            await WriteHeader(network, partial ? "206 Partial Content" : "200 OK", count,
                partial ? $"bytes {start}-{end}/{length}" : null, request.KeepAlive);
            if (request.Method == "HEAD")
                return request.KeepAlive;

            stream.Position = start;
            var buffer = new byte[BufferSize];
            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)));
                if (read <= 0)
                    return false;
                await network.WriteAsync(buffer.AsMemory(0, read));
                count -= read;
            }
        }

        return request.KeepAlive;
    }

    private static async Task WriteHeader(NetworkStream network, string status, long length, string? contentRange, bool keepAlive,
        string? body = null)
    {
        var bodyBytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var text = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Type: application/octet-stream\r\n")
            .Append("Content-Length: ").Append(body is null ? length : bodyBytes.Length).Append("\r\n");
        if (contentRange is not null)
            text.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        text.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n").Append("\r\n");
        await network.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));
        if (bodyBytes.Length > 0)
            await network.WriteAsync(bodyBytes);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
    }
}
