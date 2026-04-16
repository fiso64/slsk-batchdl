using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Services;
using Sldl.Cli;

namespace Tests.EndToEnd;

[TestClass]
public class CliEndToEndTests
{
    [TestMethod]
    public async Task AlbumDownload_CliPath_Completes()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "slsk-mock-music-cli-" + Guid.NewGuid());
        var albumDir  = Path.Combine(musicRoot, "TestArtist", "TestAlbum");
        var outputDir = Path.Combine(Path.GetTempPath(), "slsk-mock-out-cli-" + Guid.NewGuid());
        Directory.CreateDirectory(albumDir);
        Directory.CreateDirectory(outputDir);

        File.WriteAllBytes(Path.Combine(albumDir, "01. Track1.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(albumDir, "02. Track2.mp3"), [4, 5, 6]);

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

        try
        {
            var configFile = ConfigManager.Load("none");
            var (engineSettings, rootSettings, _) = ConfigManager.Bind(configFile, testArgs);

            var clientManager = new SoulseekClientManager(engineSettings);
            var app           = new DownloadEngine(engineSettings, clientManager);
            app.Enqueue(new ExtractJob(rootSettings.Extraction.Input!, rootSettings.Extraction.InputType), rootSettings);
            app.CompleteEnqueue();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.RunAsync(cts.Token);

            Assert.IsFalse(cts.IsCancellationRequested, "RunAsync timed out; connection was never initiated");

            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.IsTrue(files.Length >= 2, $"Expected >=2 downloaded files, got {files.Length}");
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
}
