namespace GitWizard;

/// <summary>
///     The injected composition an index-backed discovery runs against: how to open the index
///     owner for a drive set, and how to validate a discovered candidate against the real
///     filesystem. Grouping the two keeps the discovery entry point's parameter list honest and
///     lets a test substitute both halves without a fake platform flag.
/// </summary>
/// <param name="OpenIndex">Opens the owner for the selected drives. Invoked lazily, only for a
///     nonempty selection, so an empty search never reaches a broker connection.</param>
/// <param name="CandidateExists">Validates one candidate path and kind on the host filesystem.</param>
internal sealed record IndexDiscoveryComposition(
    Func<IReadOnlyList<string>, Task<MftIndexSession>> OpenIndex,
    Func<string, RepositoryCandidateKind, bool> CandidateExists)
{
    public IndexDiscoveryComposition(
        Func<IReadOnlyList<string>, Task<MftIndexSession>> openIndex,
        Func<string, bool> candidateExists)
        : this(openIndex, (path, _) => candidateExists(path))
    {
    }
}
