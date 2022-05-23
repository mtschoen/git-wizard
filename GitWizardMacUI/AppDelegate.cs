using System;
using System.Linq;
using AppKit;
using Foundation;
using GitWizard;

namespace GitWizardMacUI
{
	[Register ("AppDelegate")]
	public class AppDelegate : NSApplicationDelegate
	{
		public AppDelegate ()
		{
		}

		public override void DidFinishLaunching (NSNotification notification)
		{
			// TODO: Share code for parsing CLI arguments
			if (Environment.GetCommandLineArgs().Contains("-v"))
				GitWizardLog.VerboseMode = true;
		}

		public override void WillTerminate (NSNotification notification)
		{
			// Insert code here to tear down your application
		}
	}
}

