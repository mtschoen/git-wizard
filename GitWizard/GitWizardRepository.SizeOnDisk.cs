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

    long? _indexedSizeOnDisk;

    /// <summary>Hands the next size refresh the bytes the discovery index counted, in place of a directory walk.</summary>
    internal void OfferIndexedSize(long bytes) => _indexedSizeOnDisk = bytes;

    /// <summary>Takes back an offered size so a later refresh measures the files again.</summary>
    internal long? WithdrawIndexedSize()
    {
        var offered = _indexedSizeOnDisk;
        _indexedSizeOnDisk = null;
        return offered;
    }

    /// <summary>Distinguishes an unmeasured repository from a measured zero without changing report JSON.</summary>
    [JsonIgnore]
    public bool HasComputedSizeOnDisk => _sizeOnDisk.HasValue;
}
