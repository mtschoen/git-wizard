using System.Text;
using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

[NonParallelizable]
// Report JSON streaming tests exercise chunked stdout output that matches the string serializer.
public class ReportJsonStreamingTests
{
    bool _originalSilentMode;

    [SetUp]
    public void SetUp()
    {
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = false;
    }

    [TearDown]
    public void TearDown() => GitWizardLog.SilentMode = _originalSilentMode;

    static GitWizardReport CreateReport()
    {
        // A non-ASCII value and a value larger than one output chunk, so chunk boundaries can fall
        // inside a multi-byte character and a single token can exceed the chunk size.
        var repository = new GitWizardRepository("c:\\streamed");
        repository.RemoteUrls.Add("https://example.test/caf\u00e9/\u65e5\u672c\u8a9e");
        repository.RemoteUrls.Add("https://example.test/" + new string('\u00e9', 40_000));
        var report = new GitWizardReport();
        report.Repositories["c:\\streamed"] = repository;
        return report;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void WriteReportJson_MatchesStringSerializer(bool minified)
    {
        var configuration = new RunConfiguration { Minified = minified };
        using var stdout = new StringWriter();

        Program.WriteReportJson(configuration, CreateReport(), stdout);

        Assert.That(stdout.ToString(), Is.EqualTo(Program.SerializeReport(configuration, CreateReport())));
    }

    [Test]
    public void OutputReport_Json_WritesInBoundedChunks()
    {
        using var stdout = new CountingWriter();

        Program.OutputReport(CreateReport(), new RunConfiguration { Json = true, Minified = true }, stdout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stdout.WriteCalls, Is.GreaterThan(1), "The report was written as one string.");
            Assert.That(stdout.LargestWrite, Is.LessThan(stdout.TotalCharacters));
        }
    }

    sealed class CountingWriter : TextWriter
    {
        public int WriteCalls { get; private set; }

        public int LargestWrite { get; private set; }

        public int TotalCharacters { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Record(1);

        public override void Write(char[] buffer, int index, int count) => Record(count);

        public override void Write(string? value) => Record(value?.Length ?? 0);

        public override void WriteLine(string? value) => Record(value?.Length ?? 0);

        void Record(int count)
        {
            WriteCalls++;
            TotalCharacters += count;
            LargestWrite = Math.Max(LargestWrite, count);
        }
    }
}
