using System;

using AppKit;
using Foundation;

namespace GitWizardMacUI
{
	public partial class ViewController : NSViewController
	{
		public ViewController (IntPtr handle) : base (handle)
		{
		}

		public override void ViewDidLoad ()
		{
			base.ViewDidLoad ();

			//var config = GitWizardConfig.GetGlobalConfig();
			//SearchList.ItemsSource = config.SearchPaths;
			//IgnoredList.ItemsSource = config.IgnoredPaths;

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

        partial void SearchDirectoriesAddButtonClicked(NSObject sender)
        {
        }
    }
}
