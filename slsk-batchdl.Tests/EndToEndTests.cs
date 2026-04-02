using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace Tests.EndToEnd
{
    [TestClass]
    public class ProgramTests
    {
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

            var config = new Config(testArgs);
            var app = new DownloaderApplication(config, testClient);

            try
            {
                await app.RunAsync();

                // Assertions
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

    }
}
