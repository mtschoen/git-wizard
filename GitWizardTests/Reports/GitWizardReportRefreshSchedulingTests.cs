using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class GitWizardReportRefreshSchedulingTests
{
    static readonly string[] Paths = ["/a", "/b", "/c", "/d", "/e", "/f"];

    [Test]
    public void Refresh_PreviousTimings_StartLongestFirst_Issue453()
    {
        using var sandbox = new LocalFilesSandbox();
        var previousReport = new GitWizardReport();
        var seconds = new[] { 20, 90, 10, 60, 30, 50 };
        for (var index = 0; index < Paths.Length; index++)
        {
            previousReport.Repositories[Paths[index]] = new GitWizardRepository(Paths[index])
            {
                RefreshTimeSeconds = seconds[index],
            };
        }

        previousReport.Save(GitWizardReport.GetCachedReportPath());
        TestUtilities.ResetStaticCaches();

        var order = new GitWizardReport().OrderPathsForRefresh(Paths);

        Assert.That(order, Is.EqualTo([Paths[1], Paths[3], Paths[5], Paths[4], Paths[0], Paths[2]]));
    }

    [Test]
    public void Refresh_WithoutPreviousTimings_PreservesDiscoveryOrder_Issue453()
    {
        using var sandbox = new LocalFilesSandbox();

        var order = new GitWizardReport().OrderPathsForRefresh(Paths);

        Assert.That(order, Is.EqualTo(Paths));
    }
}
