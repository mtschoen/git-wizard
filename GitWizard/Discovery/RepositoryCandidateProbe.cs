using GitWizard.Watch;

namespace GitWizard.Discovery;

public static class RepositoryCandidateProbe
{
    public static bool Exists(string path, RepositoryCandidateKind kind)
    {
        if (IsInsideGitDirectory(path))
        {
            return false;
        }

        return kind switch
        {
            RepositoryCandidateKind.Ordinary => Directory.Exists(Path.Combine(path, ".git")),
            RepositoryCandidateKind.GitFile => File.Exists(Path.Combine(path, ".git")),
            RepositoryCandidateKind.Bare => IsBareGitRepository(path),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    internal static bool IsInsideGitDirectory(string path) =>
        WatchPath.Parse(path).Comparison.Split('/').Any(segment =>
            string.Equals(segment, GitWizardApi.GitEntryName, StringComparison.OrdinalIgnoreCase));

    static bool IsBareGitRepository(string path)
    {
        // Must exclude any candidate whose path is inside a .git directory
        if (IsInsideGitDirectory(path))
        {
            return false;
        }

        return File.Exists(Path.Combine(path, "HEAD")) &&
               Directory.Exists(Path.Combine(path, "objects")) &&
               Directory.Exists(Path.Combine(path, "refs"));
    }
}
