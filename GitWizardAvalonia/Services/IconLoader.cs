using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace GitWizardAvalonia.Services;

static class IconLoader
{
    static WindowIcon? _cached;

    public static WindowIcon Load()
    {
        if (_cached is not null) return _cached;
        using var stream = AssetLoader.Open(new Uri("avares://GitWizardAvalonia/Assets/appicon.png"));
        _cached = new WindowIcon(stream);
        return _cached;
    }
}
