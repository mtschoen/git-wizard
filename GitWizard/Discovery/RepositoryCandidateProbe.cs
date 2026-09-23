using GitWizard.Watch;

namespace GitWizard;

public static class RepositoryCandidateProbe
{
    public static bool Exists(string path, RepositoryCandidateKind kind)
    {
        if (WatchPath.Parse(path).Comparison.Split('/').Any(segment =>
            string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
            return false;

        return kind switch
        {
            RepositoryCandidateKind.Ordinary => Directory.Exists(Path.Combine(path, ".git")),
            RepositoryCandidateKind.GitFile => File.Exists(Path.Combine(path, ".git")),
            RepositoryCandidateKind.Bare => GitWizardApi.IsBareGitRepository(path),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
