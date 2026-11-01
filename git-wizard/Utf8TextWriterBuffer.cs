using System.Buffers;
using System.Text;

namespace GitWizard.CLI;

/// <summary>
/// Adapts a <see cref="TextWriter"/> to the UTF-8 byte sink a <c>Utf8JsonWriter</c> writes to, decoding
/// each flushed chunk straight into the text writer. Memory stays at one small chunk however large the
/// document is, unlike serializing to a string first; the chunk only grows for a single token larger
/// than it.
/// </summary>
internal sealed class Utf8TextWriterBuffer(TextWriter writer) : IBufferWriter<byte>
{
    const int InitialChunkSize = 16 * 1024;

    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    byte[] _bytes = new byte[InitialChunkSize];
    char[] _chars = new char[Encoding.UTF8.GetMaxCharCount(InitialChunkSize)];

    public void Advance(int count)
    {
        var maxChars = _decoder.GetCharCount(_bytes, 0, count, flush: false);
        if (_chars.Length < maxChars)
        {
            _chars = new char[maxChars];
        }

        var charCount = _decoder.GetChars(_bytes, 0, count, _chars, 0, flush: false);
        writer.Write(_chars, 0, charCount);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _bytes;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _bytes;
    }

    void Reserve(int sizeHint)
    {
        if (sizeHint > _bytes.Length)
        {
            _bytes = new byte[sizeHint];
        }
    }
}
