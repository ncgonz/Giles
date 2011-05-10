using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Policy;
using System.Timers;
using Giles.Core.Configuration;
using Giles.Core.IO;
using Giles.Core.Runners;
using Giles.Core.Utility;

namespace Giles.Core.Watchers
{
    public class SourceWatcher : IDisposable
    {
        readonly IBuildRunner buildRunner;
        readonly Timer buildTimer;
        readonly IFileSystem fileSystem;
        readonly IFileWatcherFactory fileWatcherFactory;
        readonly GilesConfig config;
        readonly ITestRunner testRunner;


        public SourceWatcher(IBuildRunner buildRunner, ITestRunner testRunner, IFileSystem fileSystem,
                             IFileWatcherFactory fileWatcherFactory, GilesConfig config)
        {
            FileWatchers = new List<FileSystemWatcher>();
            this.fileSystem = fileSystem;
            this.buildRunner = buildRunner;
            this.fileWatcherFactory = fileWatcherFactory;
            this.config = config;
            this.testRunner = testRunner;
            buildTimer = new Timer { AutoReset = false, Enabled = false, Interval = config.BuildDelay };
            config.PropertyChanged += config_PropertyChanged;
            buildTimer.Elapsed += buildTimer_Elapsed;
        }

        void config_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "BuildDelay")
                buildTimer.Interval = (sender as GilesConfig).BuildDelay;
        }

        public List<FileSystemWatcher> FileWatchers { get; set; }

        #region IDisposable Members

        public void Dispose()
        {
            FileWatchers.ToList().ForEach(x => x.Dispose());
        }

        #endregion

        void buildTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            RunNow();
        }

        public void Watch(string solutionPath, string filter)
        {
            var solutionFolder = fileSystem.GetDirectoryName(solutionPath);
            var fileSystemWatcher = fileWatcherFactory.Build(solutionFolder, filter, ChangeAction, null,
                                                                           ErrorAction);
            fileSystemWatcher.EnableRaisingEvents = true;
            fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite;
            fileSystemWatcher.IncludeSubdirectories = true;

            FileWatchers.Add(fileSystemWatcher);
        }

        public void ErrorAction(object sender, ErrorEventArgs e)
        {
            throw new NotImplementedException();
        }

        public void ChangeAction(object sender, FileSystemEventArgs e)
        {

            if (buildTimer.Enabled)
            {
                buildTimer.Enabled = false;
                buildTimer.Enabled = true;
            }
            else
                buildTimer.Enabled = true;
        }

        public void RunNow()
        {
            if (!buildRunner.Run())
                return;

            var listener = new GilesTestListener(config);

            var manager = new GilesAppDomainManager();
            var runResult = manager.Run(config.TestAssemblyPath);
            runResult.Each(result =>
                               {
                                   result.Messages.Each(m => listener.WriteLine(m, "Output"));
                                   result.TestResults.Each(listener.AddTestSummary);
                               });

            listener.DisplayResults();
        }
    }

    public class GilesAppDomainManager
    {
        public SessionResults SessionResults { get; set; }

        public IEnumerable<SessionResults> Run(string testAssemblyPath)
        {
            Console.WriteLine("Setting up new app domain");
            var domainInfo = new AppDomainSetup
                                 {
                                     ApplicationBase = Path.GetDirectoryName(testAssemblyPath)
                                 };

            Console.WriteLine("Copying Giles.Core to ApplicationBase: {0}", domainInfo.ApplicationBase);

            CopyGilesToTargetApplicationBase(domainInfo);

            var appDomain = AppDomain.CreateDomain("GilesAppDomainRunner", AppDomain.CurrentDomain.Evidence, domainInfo);

            var runType = typeof (GilesAppDomainRunner);
            //var runType = typeof(ICanHazNewAppDomain);

            Console.WriteLine("Creating instance of GilesAppDomainRunner in new app domain: {0} with a base of {1}", appDomain.FriendlyName, appDomain.SetupInformation.ApplicationBase);
            var foo =
                appDomain.CreateInstanceAndUnwrap(runType.Assembly.FullName, runType.FullName) as GilesAppDomainRunner;

            Console.WriteLine("Running the tests");
            var results = foo.Run(testAssemblyPath);

            Console.WriteLine("Unloading the app domain");
            AppDomain.Unload(appDomain);
            GC.Collect(1, GCCollectionMode.Forced);
            Console.WriteLine("Run complete!");
            return results;
        }

        private static void CopyGilesToTargetApplicationBase(AppDomainSetup domainInfo)
        {
            var fileSystem = new FileSystem();
            var filesToCopy = GetAssembliesToCopy();

            if (domainInfo == null) throw new ArgumentNullException("domainInfo");
            
            filesToCopy.Each(file =>
                                 {
                                     var targetPath = Path.Combine(domainInfo.ApplicationBase, fileSystem.GetFileName(file));
                                     Console.WriteLine("Copying to {0}", targetPath);
                                     if (fileSystem.FileExists(targetPath))
                                         fileSystem.DeleteFile(targetPath);

                                     fileSystem.CopyFile(file, targetPath);
                                 });
        }

        private static IEnumerable<string> GetAssembliesToCopy()
        {
            return new[]
                       {
                           typeof(GilesAppDomainRunner).Assembly.Location,
                           Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Giles.Runner.Machine.Specifications.dll")
                       };
        }
    }


    public class ICanHazNewAppDomain : MarshalByRefObject
    {
        public IEnumerable<SessionResults> Run(string testAssemblyPath)
        {
            Console.WriteLine("ICanHazNewAppDomain is current running in the app domain {0} with base of {1}", AppDomain.CurrentDomain.FriendlyName, AppDomain.CurrentDomain.SetupInformation.ApplicationBase);
            return Enumerable.Empty<SessionResults>();
        }
    }

    public class GilesAppDomainRunner : MarshalByRefObject
    {
        public IEnumerable<SessionResults> Run(string testAssemblyPath)
        {
            Console.WriteLine("GilesAppDomainRunner is current running in the app domain {0} with base of {1}", AppDomain.CurrentDomain.FriendlyName, AppDomain.CurrentDomain.SetupInformation.ApplicationBase);
            
            var testAssembly = Assembly.LoadFrom(testAssemblyPath);

            var testFrameworkRunner = new TestRunnerResolver().Resolve(testAssembly).ToList();

            var result = new List<SessionResults>();
            testFrameworkRunner.ForEach(x => result.Add(x.RunAssembly(testAssembly)));
            return result;
        }
    }
}