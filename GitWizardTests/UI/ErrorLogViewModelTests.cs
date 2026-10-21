using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// Covers the framework-agnostic error log: newest-first ordering, the bounded capacity that
/// keeps it from growing unbounded across a long-running session, Count/PropertyChanged, and Clear.
/// </summary>
public class ErrorLogViewModelTests
{
    [Test]
    public void AddError_InsertsNewestFirst()
    {
        var log = new ErrorLogViewModel();

        log.AddError("first");
        log.AddError("second");

        Assert.That(log.Entries.Select(e => e.Message), Is.EqualTo(new[] { "second", "first" }));
    }

    [Test]
    public void AddError_RecordsRepositoryPathAndTimestamp()
    {
        var log = new ErrorLogViewModel();
        var before = DateTime.Now;

        log.AddError("boom", "/repos/alpha");

        var entry = log.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Message, Is.EqualTo("boom"));
            Assert.That(entry.RepositoryPath, Is.EqualTo("/repos/alpha"));
            Assert.That(entry.Timestamp, Is.GreaterThanOrEqualTo(before));
            Assert.That(entry.DisplayText, Does.Contain("/repos/alpha"));
            Assert.That(entry.DisplayText, Does.Contain("boom"));
        });
    }

    [Test]
    public void AddError_NoRepositoryPath_DisplayTextOmitsIt()
    {
        var log = new ErrorLogViewModel();

        log.AddError("dialog failed");

        var displayText = log.Entries.Single().DisplayText;
        Assert.Multiple(() =>
        {
            Assert.That(displayText, Does.EndWith("dialog failed"));
            Assert.That(displayText, Does.Not.Contain("null"));
        });
    }

    [Test]
    public void AddError_BeyondMaxEntries_DropsOldest()
    {
        var log = new ErrorLogViewModel();

        for (var i = 0; i < ErrorLogViewModel.MaxEntries + 10; i++)
            log.AddError($"error-{i}");

        Assert.Multiple(() =>
        {
            Assert.That(log.Entries, Has.Count.EqualTo(ErrorLogViewModel.MaxEntries));
            Assert.That(log.Entries[0].Message, Is.EqualTo($"error-{ErrorLogViewModel.MaxEntries + 9}"),
                "Newest entry must survive trimming.");
            Assert.That(log.Entries.Select(e => e.Message), Does.Not.Contain("error-0"),
                "Oldest entries beyond the cap must be dropped.");
        });
    }

    [Test]
    public void Count_ReflectsEntryCountAndRaisesPropertyChanged()
    {
        var log = new ErrorLogViewModel();
        var raisedProperties = new List<string?>();
        log.PropertyChanged += (_, args) => raisedProperties.Add(args.PropertyName);

        log.AddError("one");

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.EqualTo(1));
            Assert.That(raisedProperties, Does.Contain(nameof(ErrorLogViewModel.Count)));
        });
    }

    [Test]
    public void Clear_EmptiesTheLog()
    {
        var log = new ErrorLogViewModel();
        log.AddError("one");
        log.AddError("two");

        log.Clear();

        Assert.That(log.Entries, Is.Empty);
        Assert.That(log.Count, Is.EqualTo(0));
    }
}
