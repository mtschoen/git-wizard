using System.Buffers;
using System.Text;

namespace GitWizard.Watch.Linux;

/// <summary>
/// Reads newline-terminated request lines from the helper's stdin as bytes, never holding more than
/// the request limit of one line. A line over the limit is reported as <see cref="RequestLineStatus.TooLong"/>
/// and the rest of it is read and discarded without being retained, so a hostile peer cannot make the
/// root helper allocate for a line it never ends.
/// </summary>
sealed class LinuxWatchHelperRequestReader(Stream input, int maximumLineBytes)
{
    enum LineState { Collecting = 0, Discarding = 1 }

    const int ChunkBytes = 4096;

    static readonly UTF8Encoding Utf8WithoutMarker = new(false);

    readonly byte[] _chunk = new byte[ChunkBytes];
    readonly ArrayBufferWriter<byte> _line = new();
    int _chunkOffset;
    int _chunkLength;

    /// <summary>The next line, or <see cref="RequestLineStatus.EndOfInput"/> once stdin is exhausted.</summary>
    public async Task<RequestLine> ReadLineAsync()
    {
        var state = LineState.Collecting;
        var sawBytes = false;
        while (true)
        {
            if (_chunkOffset == _chunkLength)
            {
                _chunkLength = await input.ReadAsync(_chunk).ConfigureAwait(false);
                _chunkOffset = 0;
                if (_chunkLength == 0)
                {
                    // An unterminated final line is still a line, as StreamReader.ReadLine reports it.
                    return sawBytes ? Complete(state) : new RequestLine(RequestLineStatus.EndOfInput, string.Empty);
                }
            }

            var available = _chunk.AsSpan(_chunkOffset, _chunkLength - _chunkOffset);
            var newline = available.IndexOf((byte)'\n');
            var take = newline < 0 ? available.Length : newline;
            sawBytes = true;
            if (state == LineState.Collecting)
            {
                if (_line.WrittenCount + take > maximumLineBytes)
                {
                    _line.Clear();
                    state = LineState.Discarding;
                }
                else
                {
                    _line.Write(available[..take]);
                }
            }

            _chunkOffset += newline < 0 ? take : take + 1;
            if (newline >= 0)
            {
                return Complete(state);
            }
        }
    }

    RequestLine Complete(LineState state)
    {
        if (state == LineState.Discarding)
        {
            return new RequestLine(RequestLineStatus.TooLong, string.Empty);
        }

        var text = Utf8WithoutMarker.GetString(_line.WrittenSpan);
        _line.Clear();
        return new RequestLine(RequestLineStatus.Line, text);
    }
}

enum RequestLineStatus { Line = 0, TooLong = 1, EndOfInput = 2 }

readonly record struct RequestLine(RequestLineStatus Status, string Text);
