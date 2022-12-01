using GitWizard;
using System.Diagnostics;

namespace GitWizardUI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        const string SessionStartMessage = @"Session Start Message
=======================================================================================================================
GitWizardUI Session Started
=======================================================================================================================";

        static App()
        {
            GitWizardLog.LogMethod = message => Debug.WriteLine(message);
            GitWizardLog.Log(SessionStartMessage);
        }
    }
}
