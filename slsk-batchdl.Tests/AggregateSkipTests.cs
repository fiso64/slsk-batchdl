using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Models;
using Jobs;
using Enums;
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

                var testArgs = new string[]
                {
                    "--config",      "none",
                    "--input",       "Artist1 - Song1",
                    "--aggregate",
                    "--path",        outputDir,
                    "--skip-existing",
                    "--skip-mode-output-dir", "name",
                    "--min-shares-aggregate", "1",
                    "--user",        "test_user",
                    "--pass",        "test_pass",
                    "--no-write-index",
                    "--no-write-playlist"
                };

                var config = new Config(testArgs);
                var app    = new DownloadEngine(config, testClient);
                await app.RunAsync();

                // Check the job queue to see if the track was marked as already existing
                Assert.IsNotNull(app.Queue, "Queue should not be null");
                var aggregateJob = app.Queue.Jobs.OfType<AggregateJob>().FirstOrDefault();
                Assert.IsNotNull(aggregateJob, "Should have found an AggregateJob");
                

                var song = aggregateJob.Songs.FirstOrDefault(s => s.Query.ToString(true).Contains("Artist1"));
                Assert.IsNotNull(song, "Should have found the song in the aggregate job results");
                
                // This is where it will fail if the regression is present:
                // It will be 'Downloaded' instead of 'AlreadyExists' because it wasn't skipped.
                // (Or it might fail during download if it tries to overwrite or something, 
                // but the goal is to see it skipped before it even tries to download).
                Assert.AreEqual(TrackState.AlreadyExists, song.State, 
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
