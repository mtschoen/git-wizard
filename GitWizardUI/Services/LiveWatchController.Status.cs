using GitWizard.Watch;

namespace GitWizardUI.Services;

public sealed partial class LiveWatchController
{
    internal RepositoryWatchService? Service
    {
        get
        {
            lock (_gate)
            {
                return _service;
            }
        }
    }

    public WatchReadiness? Readiness
    {
        get
        {
            lock (_gate)
            {
                return _service?.Readiness;
            }
        }
    }

    public IReadOnlyDictionary<string, string> ScanErrors
    {
        get
        {
            lock (_gate)
            {
                return _service?.ScanErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyCollection<JournalWarning> ScanWarnings
    {
        get
        {
            lock (_gate)
            {
                return _service?.ScanWarnings ?? Array.Empty<JournalWarning>();
            }
        }
    }

    public IVolumeChangeSource? Source
    {
        get
        {
            lock (_gate)
            {
                return (_lifetime as WatchStartupResult)?.Source;
            }
        }
    }
}
