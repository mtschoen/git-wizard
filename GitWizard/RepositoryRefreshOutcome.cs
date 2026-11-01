namespace GitWizard;

internal enum RepositoryRefreshOutcome
{
    Success = 0,
    NonRepository = 1,
    TransientFailure = 2,
    Cancelled = 3,
    OwnershipBlocked = 4,
}
