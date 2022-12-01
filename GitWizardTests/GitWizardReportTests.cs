using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GitWizard;
using NUnit.Framework;

namespace GitWizardTests
{
    public class GitWizardReportTests
    {
        [Test]
        public void RefreshConcurrencyTest()
        {
            GitWizardLog.SilentMode = true;
            var configuration = GitWizardConfiguration.CreateDefaultConfiguration();
            var repositoryPaths = new SortedSet<string>();
            var report = new GitWizardReport(configuration);
            report.GetRepositoryPaths(repositoryPaths);
            Parallel.For(0, 10, _ =>
            {
                report.Refresh(repositoryPaths);
            });
        }
    }
}