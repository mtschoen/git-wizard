using Avalonia.Controls;
using Avalonia.Platform;

namespace GitWizardUI.Services;

static class IconLoader
{
    static WindowIcon? _cached;

    public static WindowIcon Load()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var rawStream = AssetLoader.Open(new Uri("avares://GitWizardUI/Assets/appicon.png"));
        try
        {
            _cached = new WindowIcon(rawStream);
            return _cached;
        }
        finally
        {
            ((IDisposable)rawStream).Dispose();
        }
    }
}
