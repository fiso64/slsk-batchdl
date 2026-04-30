using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;
using Tests.ClientTests;

namespace Tests.EndToEnd
{
    [TestClass]
    public class AggregateSkipTests
    {
        [TestMethod]
        public async Task AggregateDownload_SkipExisting_CorrectlySkipsAfterSearch()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Debug);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-music-" + Guid.NewGuid());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-skip-agg-out-" + Guid.NewGuid());
            Directory.CreateDirectory(musicRoot);
            Directory.CreateDirectory(outputDir);

            try
            {
                // File that exists in "Soulseek" (mock) and in our local output dir
                string fileName = "Artist1 - Song1.mp3";
                string existingFilePath = Path.Combine(outputDir, fileName);
                File.WriteAllBytes(Path.Combine(musicRoot, fileName), TestHelpers.EmptyMp3Bytes);
                File.WriteAllBytes(existingFilePath, TestHelpers.EmptyMp3Bytes);

                var testClient = MockSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

                var eng = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var dl = new DownloadSettings();
                dl.Extraction.Input = "Artist1 - Song1";
                dl.Output.ParentDir = outputDir;
                dl.Output.WriteIndex = false;
                dl.Output.WritePlaylist = false;
                dl.Skip.SkipExisting = true;
                dl.Skip.SkipMode = SkipMode.Name;
                dl.Search.IsAggregate = true;
                dl.Search.MinSharesAggregate = 1;

                var clientManager = TestHelpers.CreateMockClientManager(testClient, eng);
                var app = new DownloadEngine(eng, clientManager);
                app.Enqueue(new ExtractJob(dl.Extraction.Input!, dl.Extraction.InputType), dl);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                // Check the job queue to see if the track was marked as already existing
                Assert.IsNotNull(app.Queue, "Queue should not be null");
                var aggregateJob = app.Queue.AllJobs().OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggregateJob, "Should have found an AggregateJob");
                

                var song = aggregateJob.Songs.FirstOrDefault(s => s.Query.ToString(true).Contains("Artist1"));
                Assert.IsNotNull(song, "Should have found the song in the aggregate job results");
                
                // This is where it will fail if the regression is present:
                // It will be 'Downloaded' instead of 'AlreadyExists' because it wasn't skipped.
                // (Or it might fail during download if it tries to overwrite or something, 
                // but the goal is to see it skipped before it even tries to download).
                Assert.AreEqual(JobState.AlreadyExists, song.State, 
                    $"Song should have been skipped. Current state: {song.State}. Failure reason: {song.FailureReason}");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            }
        }
    }
}
