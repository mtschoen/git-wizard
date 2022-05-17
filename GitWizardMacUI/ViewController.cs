﻿using System;
using System.Collections.Generic;
using System.Linq;
using AppKit;
using Foundation;
using GitWizard;

namespace GitWizardMacUI
{
	public partial class ViewController : NSViewController
	{
		class BrowserDelegate : NSBrowserDelegate
        {
			public IList<string> Paths { get; set; }

			public BrowserDelegate(IList<string> paths)
            {
				Paths = paths;
            }

            public override nint RowsInColumn(NSBrowser sender, nint column)
            {
				return Paths.Count;
            }

            public override void WillDisplayCell(NSBrowser sender, NSObject cell, nint row, nint column)
            {
				var browserCell = (NSBrowserCell)cell;
				browserCell.Title = Paths[(int)row];
				browserCell.Leaf = true;
            }
        }

		GitWizardConfiguration _configuration;
		BrowserDelegate _SearchDirectoriesBrowserDelegate;
		BrowserDelegate _IgnoredDirectoriesBrowserDelegate;

		public ViewController (IntPtr handle) : base (handle)
		{
		}

		public override void ViewDidLoad ()
		{
			base.ViewDidLoad ();

			_configuration = GitWizardConfiguration.GetGlobalConfiguration();
			_SearchDirectoriesBrowserDelegate  = new BrowserDelegate(_configuration.SearchPaths.ToList());
			SearchDirectoriesBrowser.Delegate = _SearchDirectoriesBrowserDelegate;

			_IgnoredDirectoriesBrowserDelegate = new BrowserDelegate(_configuration.IgnoredPaths.ToList());
			IgnoredDirectoriesBrowser.Delegate = _IgnoredDirectoriesBrowserDelegate;
		}

		public override NSObject RepresentedObject {
			get {
				return base.RepresentedObject;
			}
			set {
				base.RepresentedObject = value;
				// Update the view, if already loaded.
			}
		}

		partial void IgnoredDirectoriesAddButtonClicked(NSObject sender)
        {
            AddButtonClicked(IgnoredDirectoriesBrowser, _IgnoredDirectoriesBrowserDelegate, IgnoredDirectoriesTextBox.StringValue, _configuration.IgnoredPaths);
        }

        partial void IgnoredDirectoriesBrowseButtonClicked(NSObject sender)
		{
			BrowseButtonClicked("New Ignored Directory", IgnoredDirectoriesBrowser, _IgnoredDirectoriesBrowserDelegate, _configuration.IgnoredPaths);
		}

        partial void IgnoredDirectoriesDeleteButtonClicked(NSObject sender)
        {
            DeleteButtonClicked(IgnoredDirectoriesBrowser, _IgnoredDirectoriesBrowserDelegate, _configuration.IgnoredPaths);
        }

		partial void SearchDirectoriesAddButtonClicked(NSObject sender)
		{
			AddButtonClicked(SearchDirectoriesBrowser, _SearchDirectoriesBrowserDelegate, SearchDirectoriesTextBox.StringValue, _configuration.SearchPaths);
		}

		partial void SearchDirectoriesBrowseButtonClicked(NSObject sender)
        {
            BrowseButtonClicked("New Search Directory", SearchDirectoriesBrowser, _SearchDirectoriesBrowserDelegate, _configuration.SearchPaths);
        }

        partial void SearchDirectoriesDeleteButtonClicked(NSObject sender)
        {
			DeleteButtonClicked(SearchDirectoriesBrowser, _SearchDirectoriesBrowserDelegate, _configuration.SearchPaths);
		}

		private void AddButtonClicked(NSBrowser browser, BrowserDelegate @delegate, string path, SortedSet<string> paths)
		{
			if (string.IsNullOrEmpty(path))
				return;

			paths.Add(path);
			GitWizardConfiguration.SaveGlobalConfiguration(_configuration);
			@delegate.Paths = paths.ToList();
			browser.LoadColumnZero();
		}

		private void BrowseButtonClicked(string title, NSBrowser browser, BrowserDelegate @delegate, SortedSet<string> paths)
		{
			var openFilePanel = new NSOpenPanel
			{
				Title = title,
				CanChooseDirectories = true,
				CanChooseFiles = false
			};

			openFilePanel.RunModal();
			var url = openFilePanel.Url;
			if (url == null)
				return;

			var path = url.Path;
			paths.Add(path);
			GitWizardConfiguration.SaveGlobalConfiguration(_configuration);
			@delegate.Paths = paths.ToList();
			browser.LoadColumnZero();
		}

		private void DeleteButtonClicked(NSBrowser browser, BrowserDelegate @delegate, SortedSet<string> paths)
        {
			var selected = (int)browser.SelectedRow(0);
			if (selected < 0 || selected >= paths.Count)
				return;

			paths.Remove(@delegate.Paths[selected]);
			GitWizardConfiguration.SaveGlobalConfiguration(_configuration);
			@delegate.Paths.RemoveAt(selected);
			browser.LoadColumnZero();
		}
	}
}
