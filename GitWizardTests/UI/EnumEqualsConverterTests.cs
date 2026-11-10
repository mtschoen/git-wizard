using System.Globalization;
using GitWizardUI.Converters;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class EnumEqualsConverterTests
{
    readonly EnumEqualsConverter _converter = new();

    [TestCase(SortMode.SizeOnDisk, "SizeOnDisk", true)]
    [TestCase(SortMode.SizeOnDisk, "Prunable", false)]
    [TestCase(null, "SizeOnDisk", false)]
    public void Convert_ComparesTheValuesNameToTheParameter(SortMode? current, string parameter, bool expected)
        => Assert.That(_converter.Convert(current, typeof(bool), parameter, CultureInfo.InvariantCulture), Is.EqualTo(expected));

    [Test]
    public void ConvertBack_IsNotSupported()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack(true, typeof(SortMode), "SizeOnDisk", CultureInfo.InvariantCulture));
}
