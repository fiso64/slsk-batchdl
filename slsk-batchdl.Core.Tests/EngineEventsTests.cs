using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.Eventing
{
    [TestClass]
    public class EngineEventsTests
    {
        [ClassInitialize]
        public static void ClassSetup(TestContext _)
        {
            Logger.AddConsole(Logger.LogLevel.Fatal);
        }

        [TestMethod]
        public async Task EngineEvents_ReportGraphStateChangesAndCompletion()
        {
            var listFile = Path.GetTempFileName();
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-events-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);

            try
            {
                File.WriteAllLines(listFile, new[]
                {
                    "\"Artist One - Track One\"",
                    "\"Artist Two - Track Two\"",
                });

                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var downloadSettings = new DownloadSettings();
                downloadSettings.Extraction.Input = listFile;
                downloadSettings.Extraction.InputType = InputType.List;
                downloadSettings.Output.ParentDir = outputDir;

                var client = new ClientTests.MockSoulseekClient(new List<Soulseek.SearchResponse>());
                var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
                var engine = new DownloadEngine(engineSettings, clientManager);

                var registered = new List<(Job Job, Job? Parent)>();
                var stateChanges = new List<(Job Job, JobState State)>();
                var createdResults = new List<(ExtractJob ExtractJob, Job Result)>();
                JobList? completedQueue = null;
                object gate = new();

                engine.Events.JobRegistered += (job, parent) =>
                {
                    lock (gate) registered.Add((job, parent));
                };
                engine.Events.JobStateChanged += (job, state) =>
                {
                    lock (gate) stateChanges.Add((job, state));
                };
                engine.Events.JobResultCreated += (extractJob, result) =>
                {
                    lock (gate) createdResults.Add((extractJob, result));
                };
                engine.Events.EngineCompleted += queue => completedQueue = queue;

                engine.Enqueue(new ExtractJob(downloadSettings.Extraction.Input!, downloadSettings.Extraction.InputType), downloadSettings);
                engine.CompleteEnqueue();

                await engine.RunAsync(CancellationToken.None);

                Assert.AreSame(engine.Queue, completedQueue, "EngineCompleted should publish the completed root queue.");

                var rootExtract = engine.Queue.Jobs.OfType<ExtractJob>().Single();
                Assert.IsInstanceOfType(rootExtract.Result, typeof(JobList));
                var rootList = (JobList)rootExtract.Result!;
                var childExtracts = rootList.Jobs.OfType<ExtractJob>().ToList();
                Assert.AreEqual(2, childExtracts.Count, "List extraction should create child extract jobs.");

                Assert.IsTrue(registered.Any(e => ReferenceEquals(e.Job, rootExtract) && e.Parent == null),
                    "Root ExtractJob should be registered without a parent.");
                Assert.IsTrue(registered.Any(e => ReferenceEquals(e.Job, rootList) && e.Parent == null),
                    "The extracted root JobList should be registered as a root-level replacement.");
                Assert.IsTrue(childExtracts.All(child => registered.Any(e => ReferenceEquals(e.Job, child) && ReferenceEquals(e.Parent, rootList))),
                    "Child ExtractJobs should be registered under the extracted JobList.");

                foreach (var child in childExtracts)
                    Assert.IsInstanceOfType(child.Result, typeof(SongJob));
                var childSongs = childExtracts.Select(e => (SongJob)e.Result!).ToList();
                Assert.IsTrue(childSongs.All(song => registered.Any(e => ReferenceEquals(e.Job, song) && ReferenceEquals(e.Parent, rootList))),
                    "Results of child ExtractJobs should be registered under the JobList, not under the transient ExtractJob.");

                Assert.IsTrue(createdResults.Any(e => ReferenceEquals(e.ExtractJob, rootExtract) && ReferenceEquals(e.Result, rootList)),
                    "JobResultCreated should link the root ExtractJob to its extracted JobList.");
                Assert.IsTrue(childExtracts.All(child => createdResults.Any(e => ReferenceEquals(e.ExtractJob, child) && ReferenceEquals(e.Result, child.Result))),
                    "JobResultCreated should link each child ExtractJob to its extracted SongJob.");

                Assert.IsTrue(stateChanges.Any(e => ReferenceEquals(e.Job, rootExtract) && e.State == JobState.Extracting),
                    "JobStateChanged should report Extracting for the root ExtractJob.");
                Assert.IsTrue(stateChanges.Any(e => ReferenceEquals(e.Job, rootExtract) && e.State == JobState.Done),
                    "JobStateChanged should report Done for the root ExtractJob.");
                Assert.IsTrue(childSongs.All(song => stateChanges.Any(e => ReferenceEquals(e.Job, song) && e.State == JobState.Failed)),
                    "JobStateChanged should report the terminal state for child SongJobs.");
            }
            finally
            {
                if (File.Exists(listFile)) File.Delete(listFile);
                if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }
    }
}
