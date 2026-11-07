using System.Globalization;
using GitWizardUI.Converters;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class EnumHasFlagConverterTests
{
    readonly EnumHasFlagConverter _converter = new();

    [TestCase(FilterType.Stale, "Stale", true)]
    [TestCase(FilterType.Stale | FilterType.Stashes, "Stashes", true)]
    [TestCase(FilterType.Stale | FilterType.Stashes, "Stale", true)]
    [TestCase(FilterType.Stale, "Stashes", false)]
    [TestCase(FilterType.None, "Stale", false)]
    public void Convert_ReportsWhetherTheNamedFlagIsSet(FilterType current, string parameter, bool expected)
        => Assert.That(_converter.Convert(current, typeof(bool), parameter, CultureInfo.InvariantCulture), Is.EqualTo(expected));

    [Test]
    public void Convert_UnknownFlagOrNonEnum_IsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_converter.Convert(FilterType.Stale, typeof(bool), "Nope", CultureInfo.InvariantCulture), Is.False);
            Assert.That(_converter.Convert("Stale", typeof(bool), "Stale", CultureInfo.InvariantCulture), Is.False);
            Assert.That(_converter.Convert(null, typeof(bool), "Stale", CultureInfo.InvariantCulture), Is.False);
        }
    }
}
