using GitWizard;

namespace GitWizardUI
{
    public partial class MainPage : ContentPage, IUpdateProgressString
    {
        Task getPathsTask;

        public MainPage()
        {
            InitializeComponent();

#if MACCATALYST
            RootPath.Text = "~";
#endif
        }

        public List<string> ListRows { get; set; } = new List<string>();

        public void UpdateProgress(string message)
        {
            Dispatcher.Dispatch(() => StatusLabel.Text = message);
        }

        private void OnRefreshButtonClicked(object sender, EventArgs e)
        {
            if (getPathsTask != null && !getPathsTask.IsCompleted)
                return;

            lock(ListRows)
                ListRows.Clear();

            StatusLabel.Text = "Getting Repositories...";
            getPathsTask = Task.Run(() => GitWizardAPI.GetRepositoryPaths(RootPath.Text, ListRows, this));
        }
    }
}