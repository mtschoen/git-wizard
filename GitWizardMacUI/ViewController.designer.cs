// WARNING
//
// This file has been generated automatically by Visual Studio to store outlets and
// actions made in the UI designer. If it is removed, they will be lost.
// Manual changes to this file may not be handled correctly.
//
using Foundation;
using System.CodeDom.Compiler;

namespace GitWizardMacUI
{
	[Register ("ViewController")]
	partial class ViewController
	{
		[Outlet]
		AppKit.NSBrowser IgnoredDirectoriesBrowser { get; set; }

		[Outlet]
		AppKit.NSTextField IgnoredDirectoriesTextBox { get; set; }

		[Outlet]
		AppKit.NSStackView RepositoryStackView { get; set; }

		[Outlet]
		AppKit.NSBrowser SearchDirectoriesBrowser { get; set; }

		[Outlet]
		AppKit.NSTextField SearchDirectoriesTextBox { get; set; }

		[Outlet]
		AppKit.NSTextField StatusLabel { get; set; }

		[Action ("ClearCacheButtonClicked:")]
		partial void ClearCacheButtonClicked (Foundation.NSObject sender);

		[Action ("IgnoredDirectoriesAddButtonClicked:")]
		partial void IgnoredDirectoriesAddButtonClicked (Foundation.NSObject sender);

		[Action ("IgnoredDirectoriesBrowseButtonClicked:")]
		partial void IgnoredDirectoriesBrowseButtonClicked (Foundation.NSObject sender);

		[Action ("IgnoredDirectoriesDeleteButtonClicked:")]
		partial void IgnoredDirectoriesDeleteButtonClicked (Foundation.NSObject sender);

		[Action ("RefreshButtonClicked:")]
		partial void RefreshButtonClicked (Foundation.NSObject sender);

		[Action ("SearchDirectoriesAddButtonClicked:")]
		partial void SearchDirectoriesAddButtonClicked (Foundation.NSObject sender);

		[Action ("SearchDirectoriesBrowseButtonClicked:")]
		partial void SearchDirectoriesBrowseButtonClicked (Foundation.NSObject sender);

		[Action ("SearchDirectoriesDeleteButtonClicked:")]
		partial void SearchDirectoriesDeleteButtonClicked (Foundation.NSObject sender);
		
		void ReleaseDesignerOutlets ()
		{
			if (IgnoredDirectoriesBrowser != null) {
				IgnoredDirectoriesBrowser.Dispose ();
				IgnoredDirectoriesBrowser = null;
			}

			if (IgnoredDirectoriesTextBox != null) {
				IgnoredDirectoriesTextBox.Dispose ();
				IgnoredDirectoriesTextBox = null;
			}

			if (SearchDirectoriesBrowser != null) {
				SearchDirectoriesBrowser.Dispose ();
				SearchDirectoriesBrowser = null;
			}

			if (SearchDirectoriesTextBox != null) {
				SearchDirectoriesTextBox.Dispose ();
				SearchDirectoriesTextBox = null;
			}

			if (StatusLabel != null) {
				StatusLabel.Dispose ();
				StatusLabel = null;
			}

			if (RepositoryStackView != null) {
				RepositoryStackView.Dispose ();
				RepositoryStackView = null;
			}
		}
	}
}
