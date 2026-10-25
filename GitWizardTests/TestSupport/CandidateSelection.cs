using GitWizard;
using MFTLib.Index;

namespace GitWizardTests.TestSupport;

/// <summary>Runs the production candidate selection for one logical search root and returns the repository paths.</summary>
static class CandidateSelection
{
    public static string[] Paths(
        FileIndex index, string root, string[] ignoredPaths, bool? skipHiddenDirectories = null,
        bool? includeTempDirectories = null)
    {
        var configuration = new GitWizardConfiguration
        {
            IncludeTempDirectories = includeTempDirectories,
            SkipHiddenDirectories = skipHiddenDirectories
        };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
        var scope = DiscoveryScope.CreateExpanded(
            request, [root], [], ignoredPaths, _ => new RootClassification(root, RootFileSystem.Ntfs, "X", null));
        return GitWizardApi.SelectRepositoryCandidates(index, scope).Select(candidate => candidate.Path).ToArray();
    }
}
