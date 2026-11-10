using System.Text;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// The byte sink a JSON writer flushes into: each advanced chunk is decoded into the text writer,
/// and a single token larger than the initial chunk grows the buffer instead of failing.
/// </summary>
[TestFixture]
public class Utf8TextWriterBufferTests
{
    [Test]
    public void GetSpan_TokenLargerThanTheInitialChunk_GrowsTheBufferAndDecodesIt()
    {
        using var output = new StringWriter();
        var buffer = new Utf8TextWriterBuffer(output);
        var token = new string('a', 40_000);

        var span = buffer.GetSpan(token.Length);
        Encoding.UTF8.GetBytes(token, span);
        buffer.Advance(token.Length);

        Assert.That(output.ToString(), Is.EqualTo(token));
    }

    [Test]
    public void GetMemory_MultiByteCharacterSplitAcrossAdvances_IsDecodedWhole()
    {
        using var output = new StringWriter();
        var buffer = new Utf8TextWriterBuffer(output);
        var encoded = Encoding.UTF8.GetBytes("é");

        encoded.AsSpan(0, 1).CopyTo(buffer.GetMemory().Span);
        buffer.Advance(1);
        encoded.AsSpan(1, 1).CopyTo(buffer.GetMemory().Span);
        buffer.Advance(1);

        Assert.That(output.ToString(), Is.EqualTo("é"));
    }
}
