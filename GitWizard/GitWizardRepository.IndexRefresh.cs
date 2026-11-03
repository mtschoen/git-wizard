using System.Text.Json.Serialization;

namespace GitWizard;

public partial class GitWizardRepository
{
    [JsonInclude]
    public RepositoryIndexRefreshReason? IndexRefreshReason { get; internal set; }

    internal void RefreshIndex(RepositoryIndexRefreshService service)
    {
        lock (_refreshGate)
        {
            try
            {
                service.RefreshLocked(this);
            }
            catch (Exception exception)
            {
                RefreshError = exception.Message;
                throw;
            }
        }
    }
}
