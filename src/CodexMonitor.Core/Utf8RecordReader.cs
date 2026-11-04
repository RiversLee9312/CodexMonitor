using System.Text;

namespace CodexMonitor.Core;

/// <summary>JSONL records with byte-stable identities, including CRLF, BOM and incomplete final records.</summary>
internal sealed class Utf8RecordReader(FileStream stream, long start, long end)
{
    private readonly byte[] _buffer = new byte[65536];
    private int _position, _length;
    private long _offset = start;
    public async ValueTask<(string Text, long Offset)?> ReadAsync(CancellationToken ct)
    {
        var begin = _offset; MemoryStream? parts = null;
        try
        {
            while (true)
            {
                if (_position == _length)
                {
                    var remaining = end - stream.Position;
                    if (remaining <= 0) break;
                    _length = await stream.ReadAsync(_buffer.AsMemory(0, (int)Math.Min(_buffer.Length, remaining)), ct); _position = 0;
                    if (_length == 0) break;
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
                var count = (newline < 0 ? _length : newline) - _position;
                if (parts is null && newline >= 0)
                {
                    var text = Encoding.UTF8.GetString(_buffer, _position, count).TrimEnd('\r'); _position += count + 1; _offset += count + 1;
                    return (begin == 0 ? text.TrimStart('\uFEFF') : text, begin);
                }
                parts ??= new(); parts.Write(_buffer, _position, count); _position += count; _offset += count;
                if (parts.Length > 64 * 1024 * 1024) throw new IOException("A single transcript record exceeds the preview limit");
                if (newline >= 0) { _position++; _offset++; break; }
            }
            if (parts is null || parts.Length == 0) return null;
            var value = Encoding.UTF8.GetString(parts.GetBuffer(), 0, checked((int)parts.Length)).TrimEnd('\r');
            return (begin == 0 ? value.TrimStart('\uFEFF') : value, begin);
        }
        finally { parts?.Dispose(); }
    }
}
