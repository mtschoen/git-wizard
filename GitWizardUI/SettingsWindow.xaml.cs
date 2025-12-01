using System;
using GitWizard;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GitWizardUI;

/// <summary>
/// Interaction logic for SettingsWindow.xaml
/// </summary>
public partial class SettingsWindow
{
    readonly GitWizardConfiguration _configuration;
    readonly List<string> _searchPaths;
    readonly List<string> _ignoredPaths;

    public event Action? WindowClosed;

    public SettingsWindow()
    {
        InitializeComponent();
        _configuration = GitWizardConfiguration.GetGlobalConfiguration();
        _searchPaths = _configuration.SearchPaths.ToList();
        _ignoredPaths = _configuration.IgnoredPaths.ToList();
        SearchList.ItemsSource = _searchPaths;
        IgnoredList.ItemsSource = _ignoredPaths;
    }

    void BrowseSearchPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var selectedPath = dialog.SelectedPath;
            SearchPathTextBox.Text = selectedPath;
            AddSearchPath(selectedPath);
        }
    }

    void AddSearchPathButton_Click(object sender, RoutedEventArgs e)
    {
        AddSearchPath(SearchPathTextBox.Text);
    }

    void AddSearchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var searchPaths = _configuration.SearchPaths;
        searchPaths.Add(path);
        _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());

        _searchPaths.Clear();
        _searchPaths.AddRange(searchPaths);
        SearchList.Items.Refresh();
    }

    void BrowseIgnoredPathButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var selectedPath = dialog.SelectedPath;
            IgnoredPathTextBox.Text = selectedPath;
            AddIgnoredPath(selectedPath);
        }
    }

    void AddIgnoredPathButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        AddIgnoredPath(IgnoredPathTextBox.Text);
    }

    void AddIgnoredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var ignoredPaths = _configuration.IgnoredPaths;
        ignoredPaths.Add(path);
        _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());

        _ignoredPaths.Clear();
        _ignoredPaths.AddRange(ignoredPaths);
        IgnoredList.Items.Refresh();
    }

    void IgnoredList_KeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete)
            return;

        var ignoredPaths = _configuration.IgnoredPaths;
        ignoredPaths.Remove((string)IgnoredList.SelectedValue);
        _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());

        _ignoredPaths.Clear();
        _ignoredPaths.AddRange(ignoredPaths);
        IgnoredList.Items.Refresh();
    }

    void SearchList_KeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete)
            return;

        var searchPaths = _configuration.SearchPaths;
        searchPaths.Remove((string)SearchList.SelectedValue);
        _configuration.Save(GitWizardConfiguration.GetGlobalConfigurationPath());

        _searchPaths.Clear();
        _searchPaths.AddRange(searchPaths);
        SearchList.Items.Refresh();
    }

    void IgnoredPathTextBox_KeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
            return;

        AddIgnoredPath(IgnoredPathTextBox.Text);
    }

    void SearchPathTextBox_KeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
            return;

        AddSearchPath(SearchPathTextBox.Text);
    }

    void SettingsWindow_Closed(object sender, EventArgs eventArgs)
    {
        WindowClosed?.Invoke();
    }
}