using System.Text;

namespace CsharpLspMux;

public sealed class LspFrameReader : IFrameReader
{
    private readonly Stream _stream;

    public LspFrameReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<Frame?> ReadFrameAsync(CancellationToken ct = default)
    {
        int contentLength = -1;

        while (true)
        {
            var line = await ReadLineAsync(_stream, ct);
            if (line is null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length: ".Length..], out var len))
                contentLength = len;
        }

        if (contentLength < 0) return null;

        var buffer = new byte[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) return null;
            totalRead += read;
        }

        return Frame.FromWire(buffer);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            if (read == 0) return null;
            var ch = (char)buf[0];
            if (ch == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(ch);
        }
    }
}
