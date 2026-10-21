namespace GitWizard.Watch;

// Every _usableDrives access goes through these, under a leaf lock that calls nothing else.
public sealed partial class IndexVolumeChangeSource
{
    bool HasUsableDrives()
    {
        lock (_usableDrivesLock)
        {
            return _usableDrives.Count > 0;
        }
    }

    char[] CaptureUsableDrives()
    {
        lock (_usableDrivesLock)
        {
            return [.. _usableDrives];
        }
    }

    void AddUsableDrive(char driveLetter)
    {
        lock (_usableDrivesLock)
        {
            _usableDrives.Add(char.ToUpperInvariant(driveLetter));
        }
    }

    bool RemoveUsableDrive(char driveLetter)
    {
        lock (_usableDrivesLock)
        {
            _usableDrives.Remove(char.ToUpperInvariant(driveLetter));
            return _usableDrives.Count > 0;
        }
    }

    bool ContainsUsableDrive(char driveLetter)
    {
        lock (_usableDrivesLock)
        {
            return _usableDrives.Contains(char.ToUpperInvariant(driveLetter));
        }
    }
}
