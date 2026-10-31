using System.Text.Json.Serialization;

namespace GitWizard;

public partial class GitWizardRepository
{
    long? _sizeOnDisk;

    /// <summary>Total file bytes from the last size measurement; zero until first computed.</summary>
    public long SizeOnDisk
    {
        get => _sizeOnDisk.GetValueOrDefault();
        internal set => _sizeOnDisk = value;
    }

    Func<long?>? _indexedSizeOnDisk;

    /// <summary>Hands the next size phase the pending discovery result, in place of a directory walk.</summary>
    internal void OfferIndexedSizes(Task<IReadOnlyDictionary<string, long>> sizes, string path) =>
        _indexedSizeOnDisk = () => sizes.GetAwaiter().GetResult().TryGetValue(path, out var bytes) ? bytes : null;

    /// <summary>Takes back an offered size so a later refresh measures the files again.</summary>
    internal long? WithdrawIndexedSize()
    {
        var offered = _indexedSizeOnDisk;
        _indexedSizeOnDisk = null;
        if (offered is null)
        {
            return null;
        }

        try
        {
            return offered();
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Exception reading the discovery index size for {WorkingDirectory}; walking the directory instead.");
            return null;
        }
    }

    /// <summary>Distinguishes an unmeasured repository from a measured zero without changing report JSON.</summary>
    [JsonIgnore]
    public bool HasComputedSizeOnDisk => _sizeOnDisk.HasValue;
}
