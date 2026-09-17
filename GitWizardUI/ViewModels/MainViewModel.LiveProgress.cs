using System.Globalization;
using GitWizard.Watch;

namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    bool _isLiveScanProgressVisible;
    string _liveScanProgressText = string.Empty;
    double _liveScanProgressValue;
    bool _isLiveScanProgressIndeterminate;

    /// <summary>True while a Live start/restart is cold-scanning and a sample has arrived.</summary>
    public bool IsLiveScanProgressVisible
    {
        get => _isLiveScanProgressVisible;
        internal set
        {
            if (_isLiveScanProgressVisible != value)
            {
                _isLiveScanProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public string LiveScanProgressText
    {
        get => _liveScanProgressText;
        internal set
        {
            if (_liveScanProgressText != value)
            {
                _liveScanProgressText = value;
                OnPropertyChanged();
            }
        }
    }

    public double LiveScanProgressValue
    {
        get => _liveScanProgressValue;
        internal set
        {
            if (Math.Abs(_liveScanProgressValue - value) > 0.001)
            {
                _liveScanProgressValue = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLiveScanProgressIndeterminate
    {
        get => _isLiveScanProgressIndeterminate;
        internal set
        {
            if (_isLiveScanProgressIndeterminate != value)
            {
                _isLiveScanProgressIndeterminate = value;
                OnPropertyChanged();
            }
        }
    }

    void ApplyLiveScanProgress(VolumeScanProgress sample)
    {
        var driveText = $"{sample.Drive}: ({sample.DriveOrdinal} of {sample.DriveTotal})";
        if (sample.TotalRows is > 0)
        {
            LiveScanProgressText = $"Scanning {driveText}, {FormatRecordCount(sample.RowsWritten)} of {FormatRecordCount(sample.TotalRows.Value)} records";
            var driveFraction = Math.Min(1.0, sample.RowsWritten / (double)sample.TotalRows.Value);
            LiveScanProgressValue = (sample.DriveOrdinal - 1 + driveFraction) / Math.Max(1, sample.DriveTotal);
            IsLiveScanProgressIndeterminate = false;
        }
        else
        {
            LiveScanProgressText = $"Scanning {driveText}, {FormatRecordCount(sample.RowsWritten)} records";
            LiveScanProgressValue = 0;
            IsLiveScanProgressIndeterminate = true;
        }

        IsLiveScanProgressVisible = true;
    }

    void ClearLiveScanProgress()
    {
        IsLiveScanProgressVisible = false;
        IsLiveScanProgressIndeterminate = false;
        LiveScanProgressValue = 0;
        LiveScanProgressText = string.Empty;
    }

    internal static string FormatRecordCount(uint count) => count switch
    {
        < 1_000u => count.ToString(CultureInfo.InvariantCulture),
        < 1_000_000u => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000.0:F1}k"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000.0:F1}M")
    };
}
