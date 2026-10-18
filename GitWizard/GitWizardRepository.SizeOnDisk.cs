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

    /// <summary>Distinguishes an unmeasured repository from a measured zero without changing report JSON.</summary>
    [JsonIgnore]
    public bool HasComputedSizeOnDisk => _sizeOnDisk.HasValue;
}
