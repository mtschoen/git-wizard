using System.Globalization;
using GitWizardUI.Converters;

namespace GitWizardTests;

public class PathPrettyPrintConverterTests
{
    readonly PathPrettyPrintConverter _converter = new();

    [Test]
    public void Convert_StringPath_ReturnsPrettyPrintedPath()
    {
        var result = _converter.Convert("C:\\Users\\example", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("C:\\Users\\example"));
    }

    [Test]
    public void Convert_TildePath_ExpandsHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = _converter.Convert("~/projects", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo(Path.Combine(home, "projects")));
    }

    [TestCase(null)]
    public void Convert_NullInput_ReturnsNull(object? input)
    {
        var result = _converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.Null);
    }

    [TestCase(42)]
    public void Convert_NonStringInput_ReturnsInputUnchanged(object input)
    {
        var result = _converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo(input));
    }

    [TestCase("some path")]
    [TestCase(null)]
    public void ConvertBack_ReturnsValueUnchanged(object? input)
    {
        var result = _converter.ConvertBack(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo(input));
    }

    [Test]
    public void Convert_EnvironmentVariable_Expands()
    {
        var varName = "GW_TEST_PRETTY_PRINT_" + Guid.NewGuid().ToString("N");
        var original = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, "expanded_path");
            var result = _converter.Convert($"%{varName}%", typeof(string), null, CultureInfo.InvariantCulture);
            Assert.That(result, Is.EqualTo("expanded_path"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, original);
        }
    }
}
