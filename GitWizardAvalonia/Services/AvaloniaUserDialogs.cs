using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public sealed class AvaloniaUserDialogs : IUserDialogs
{
    public Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
        => ShowDialogAsync(title, message, okLabel, cancelLabel: null).ContinueWith(_ => { });

    public Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
        => ShowDialogAsync(title, message, acceptLabel, cancelLabel);

    static async Task<bool> ShowDialogAsync(string title, string message, string acceptLabel, string? cancelLabel)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return false;

        var tcs = new TaskCompletionSource<bool>();
        var accept = new Button { Content = acceptLabel, Margin = new Thickness(4) };
        var cancel = cancelLabel is null ? null : new Button { Content = cancelLabel, Margin = new Thickness(4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        if (cancel is not null) buttons.Children.Add(cancel);

        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                    buttons,
                }
            }
        };

        accept.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        if (cancel is not null) cancel.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        await window.ShowDialog(owner);
        return await tcs.Task;
    }
}
