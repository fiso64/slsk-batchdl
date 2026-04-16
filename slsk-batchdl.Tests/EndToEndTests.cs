using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Cli;

namespace Tests.EndToEnd
{
    [TestClass]
    public class ProgramTests
    {
        // Regression: when --mock-files-dir is used, file paths in the index are absolute
        // (e.g. "C:\Users\fiso\Music\ELO\Time\01.flac"). FolderPath in AlbumFolder is
        // "local\\" + directory, so remoteBaseDir gets a "local/" prefix that the bare
        // Soulseek.File.Filename doesn't have. GetFolderName's Path.GetRelativePath call
        // was then comparing mismatched paths, causing the full directory tree to bleed into
        // {foldername}, producing "Music/Main/ELO/Time" instead of just "Time".
        [TestMethod]
        public async Task AlbumDownload_MockFilesDir_FoldernameIsLeafOnly()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Debug);

            // Create temp music dir with absolute paths (mirrors --mock-files-dir behaviour)
            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "Main", "TestArtist", "TestAlbum");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(albumDir);
            System.IO.Directory.CreateDirectory(outputDir);

            // Write placeholder mp3 bytes so the downloader can copy actual content
            System.IO.File.WriteAllBytes(Path.Combine(albumDir, "01. TestArtist - Track1.mp3"), TestHelpers.EmptyMp3Bytes);
            System.IO.File.WriteAllBytes(Path.Combine(albumDir, "02. TestArtist - Track2.mp3"), TestHelpers.EmptyMp3Bytes);

            // Build the mock client the same way SoulseekClientManager does for --mock-files-dir
            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, albumDir);

            var testArgs = new string[]
            {
                "--config",      "none",
                "--input",       "TestArtist TestAlbum",
                "--album",
                "--path",        outputDir,
                "--name-format", "{foldername}/{filename}",
                "--user",        "test_user",
                "--pass",        "test_pass",
            };

            try
            {
                var configFile = ConfigManager.Load("none");
                var (engineSettings, rootSettings, _) = ConfigManager.Bind(configFile, testArgs);
                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);
                app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
                app.CompleteEnqueue();
                await app.RunAsync(CancellationToken.None);

                // Files must land directly in outputDir/TestAlbum/, NOT buried inside a
                // full mirrored subtree like outputDir/TestAlbum/Main/TestArtist/TestAlbum/
                var expectedDir = Path.Combine(outputDir, "TestAlbum");
                Assert.IsTrue(System.IO.Directory.Exists(expectedDir),
                    $"Expected output folder '{expectedDir}' does not exist. " +
                    $"Actual tree: {string.Join(", ", System.IO.Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))}");

                var topLevelFiles = System.IO.Directory.GetFiles(expectedDir, "*", SearchOption.TopDirectoryOnly);
                Assert.IsTrue(topLevelFiles.Any(f => Path.GetFileName(f) == "01. TestArtist - Track1.mp3"),
                    "Track1 must be directly inside TestAlbum/, not in a subdirectory");
                Assert.IsTrue(topLevelFiles.Any(f => Path.GetFileName(f) == "02. TestArtist - Track2.mp3"),
                    "Track2 must be directly inside TestAlbum/, not in a subdirectory");
            }
            finally
            {
                if (System.IO.Directory.Exists(musicRoot)) System.IO.Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [TestMethod]
        public async Task AlbumDownload_E2E()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Debug);

            var testClient = new ClientTests.MockSoulseekClient(TestHelpers.CreateTestIndex());
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-batchdl-e2e", Guid.NewGuid().ToString());
            System.IO.Directory.CreateDirectory(outputDir);

            var testArgs = new string[]
            {
                // Don't use any global configs during testing!
                "--config", "none",
                "--input", "testartist - testalbum",
                "--album",
                "--path", outputDir,
                "--name-format", "{foldername}/{filename}",
                "--user", "test_user",
                "--pass", "test_pass",
            };

            var configFile = ConfigManager.Load("none");
            var (engineSettings, rootSettings, _) = ConfigManager.Bind(configFile, testArgs);
            var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
            var app = new DownloadEngine(engineSettings, clientManager);
            app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
            app.CompleteEnqueue();

            try
            {
                await app.RunAsync(CancellationToken.None);

                // Assertions
                Console.WriteLine($"[Trace] outputDir contents: {string.Join(", ", System.IO.Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Select(f => f.Replace(outputDir, "")))}");
                Console.WriteLine($"[Trace] Queue jobs: {app.Queue.Jobs.Count}, states: {string.Join(", ", app.Queue.Jobs.Select(j => $"{j.GetType().Name}:{j.State}"))}");
                var albumJob2 = app.Queue.Jobs.OfType<AlbumJob>().FirstOrDefault();
                if (albumJob2 != null)
                {
                    Console.WriteLine($"[Trace] AlbumJob state={albumJob2.State} failureReason={albumJob2.FailureReason} resolvedTarget={albumJob2.ResolvedTarget?.FolderPath} results={albumJob2.Results.Count}");
                    foreach (var f in albumJob2.Results.SelectMany(r => r.Files))
                        Console.WriteLine($"[Trace]   file: {f.Query.Title} state={f.State} dp={f.DownloadPath} candidates={f.Candidates?.Count} rt={f.ResolvedTarget?.Filename}");
                }
                var downloadedFiles = System.IO.Directory.GetFiles(Path.Combine(outputDir, "(2011) testalbum [MP3]"), "*", SearchOption.AllDirectories);
                Assert.AreEqual(4, downloadedFiles.Length, "Should download 4 files for the album.");
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0101. testartist - testsong.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0102. testartist - testsong2.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("0103. testartist - testsong3.mp3")));
                Assert.IsTrue(downloadedFiles.Any(f => f.EndsWith("cover.jpg")));
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir))
                {
                    System.IO.Directory.Delete(outputDir, true);
                }
            }
        }

        [TestMethod]
        public async Task AlbumDownload_CliPath_Completes()
        {
            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-cli-" + Guid.NewGuid());
            var albumDir  = Path.Combine(musicRoot, "TestArtist", "TestAlbum");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-cli-" + Guid.NewGuid());
            Directory.CreateDirectory(albumDir);
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(albumDir, "01. Track1.mp3"), TestHelpers.EmptyMp3Bytes);
            File.WriteAllBytes(Path.Combine(albumDir, "02. Track2.mp3"), TestHelpers.EmptyMp3Bytes);

            // Use the real CLI path: SoulseekClientManager created from config (not pre-injected).
            // mockFilesDir triggers LocalFilesSoulseekClient creation inside SoulseekClientManager.
            var testArgs = new string[]
            {
                "--config",              "none",
                "--input",               "TestArtist TestAlbum",
                "--album",
                "--path",                outputDir,
                "--mock-files-dir",      musicRoot,
                "--mock-files-no-read-tags",
                "--user",                "test_user",
                "--pass",                "test_pass",
            };

            var configFile = ConfigManager.Load("none");
            var (engineSettings, rootSettings, _) = ConfigManager.Bind(configFile, testArgs);

            var clientManager = new SoulseekClientManager(engineSettings);  // CLI path: no pre-injected client
            var app           = new DownloadEngine(engineSettings, clientManager);
            app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
            app.CompleteEnqueue();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out — connection was never initiated");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.IsTrue(files.Length >= 2, $"Expected >=2 downloaded files, got {files.Length}");
        }
    }
}
