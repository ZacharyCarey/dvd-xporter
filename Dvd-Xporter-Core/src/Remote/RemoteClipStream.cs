using System.Net.Http.Headers;

namespace DvdXporter.Core.Remote;

/// <summary>
/// A clip on the render server, read as a seekable stream for a player or ffmpeg. Reading on from where the last read
/// stopped carries on with the same response; anywhere else asks the server again, from there.
/// </summary>
/// <param name="length">Its length, if it's known; otherwise the server's first answer says.</param>
internal sealed class RemoteClipStream(HttpClient http, string path, long? length) : Stream
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private HttpResponseMessage? _response;
    private Stream? _body;
    // Where the open response has got to
    private long _bodyPosition = -1;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            if (length is null)
                Open();
            return length!.Value;
        }
    }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0)
            return 0;

        // Once more from where it's got to, if the connection drops
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_body is null || _bodyPosition != _position)
                    Open();
                int read = _body!.Read(buffer);
                if (read == 0)
                    throw new IOException("The render server stopped sending the clip.");
                _position += read;
                _bodyPosition = _position;
                return read;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && attempt == 0)
            {
                CloseResponse();
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    /// <summary>Asks for the rest of the clip from <see cref="Position"/>.</summary>
    private void Open()
    {
        CloseResponse();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(_position, null);
        using var connecting = new CancellationTokenSource(ConnectTimeout);
        try
        {
            _response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, connecting.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("The render server didn't answer.", ex);
        }

        if (!_response.IsSuccessStatusCode)
            throw new IOException($"The render server couldn't send the clip ({(int)_response.StatusCode}).");
        length ??= _response.Content.Headers.ContentRange?.Length ?? _response.Content.Headers.ContentLength
            ?? throw new IOException("The render server didn't say how long the clip is.");
        _body = _response.Content.ReadAsStream();
        _bodyPosition = _position;
    }

    private void CloseResponse()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
        _bodyPosition = -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseResponse();
        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
