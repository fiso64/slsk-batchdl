using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using Sldl.Core.Jobs;
using Sldl.Core.Services;

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

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = "TestArtist TestAlbum";
                rootSettings.Extraction.IsAlbum = true;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";

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

            var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
            var rootSettings = new DownloadSettings();
            rootSettings.Extraction.Input = "testartist - testalbum";
            rootSettings.Extraction.IsAlbum = true;
            rootSettings.Output.ParentDir = outputDir;
            rootSettings.Output.NameFormat = "{foldername}/{filename}";

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
        public async Task InteractiveAlbumSelection_FromList_IsSerialized()
        {
            Console.ResetColor();
            Console.OutputEncoding = Encoding.UTF8;
            Logger.SetupExceptionHandling();
            Logger.AddConsole();
            Logger.SetConsoleLogLevel(Logger.LogLevel.Error);

            var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-interactive-" + Guid.NewGuid());
            var albumOneDir = Path.Combine(musicRoot, "Artist One", "Album One");
            var albumTwoDir = Path.Combine(musicRoot, "Artist Two", "Album Two");
            var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-interactive-" + Guid.NewGuid());
            var listPath = Path.Combine(Path.GetTempPath(), "slsk-list-" + Guid.NewGuid() + ".txt");

            System.IO.Directory.CreateDirectory(albumOneDir);
            System.IO.Directory.CreateDirectory(albumTwoDir);
            System.IO.Directory.CreateDirectory(outputDir);

            System.IO.File.WriteAllBytes(Path.Combine(albumOneDir, "01. Artist One - Track One.mp3"), TestHelpers.EmptyMp3Bytes);
            System.IO.File.WriteAllBytes(Path.Combine(albumTwoDir, "01. Artist Two - Track Two.mp3"), TestHelpers.EmptyMp3Bytes);
            System.IO.File.WriteAllText(listPath,
                "a:\"Artist One - Album One\"" + Environment.NewLine +
                "a:\"Artist Two - Album Two\"");

            var testClient = ClientTests.MockSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, musicRoot);

            try
            {
                var engineSettings = new EngineSettings { Username = "test_user", Password = "test_pass" };
                var rootSettings = new DownloadSettings();
                rootSettings.Extraction.Input = listPath;
                rootSettings.Extraction.InputType = InputType.List;
                rootSettings.Search.NoBrowseFolder = true;
                rootSettings.Output.ParentDir = outputDir;
                rootSettings.Output.NameFormat = "{foldername}/{filename}";

                var clientManager = TestHelpers.CreateMockClientManager(testClient, engineSettings);
                var app = new DownloadEngine(engineSettings, clientManager);

                var activePickers = 0;
                var maxActivePickers = 0;
                var pickerCalls = 0;

                app.SelectAlbumVersion = async job =>
                {
                    var active = Interlocked.Increment(ref activePickers);
                    int observed;
                    do
                    {
                        observed = maxActivePickers;
                        if (active <= observed) break;
                    }
                    while (Interlocked.CompareExchange(ref maxActivePickers, active, observed) != observed);

                    try
                    {
                        await Task.Delay(150);
                        Interlocked.Increment(ref pickerCalls);
                        return job.Results.FirstOrDefault();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activePickers);
                    }
                };

                app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
                app.CompleteEnqueue();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await app.RunAsync(cts.Token);

                Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out");
                Assert.AreEqual(2, pickerCalls, "Both list album jobs should reach the interactive picker");
                Assert.AreEqual(1, maxActivePickers, "Interactive album prompts must not overlap");
            }
            finally
            {
                if (System.IO.File.Exists(listPath)) System.IO.File.Delete(listPath);
                if (System.IO.Directory.Exists(musicRoot)) System.IO.Directory.Delete(musicRoot, true);
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

    }
}
