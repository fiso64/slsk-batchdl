using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using System.Text.Json;

namespace Tests.Cli;

[TestClass]
public class CliExitCodeTests
{
    [TestMethod]
    public async Task MainCore_UnknownFlag_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["--not-a-real-flag"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    public async Task MainCore_InvalidDaemonIp_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["daemon", "--no-config", "--server-ip", "999.1.1.1"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    public async Task MainCore_InvalidDaemonPort_ReturnsUsageError()
    {
        var exitCode = await Sockseek.Cli.Program.MainCore(["daemon", "--no-config", "--server-port", "70000"]);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
    }

    [TestMethod]
    public void EnsureDaemonEndpointAvailable_PortCollision_ThrowsConciseException()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var ex = Assert.ThrowsException<Sockseek.Cli.Program.DaemonEndpointUnavailableException>(() =>
                Sockseek.Cli.Program.EnsureDaemonEndpointAvailable(new DaemonSettings
                {
                    ListenIp = "127.0.0.1",
                    ListenPort = port,
                }));

            StringAssert.Contains(ex.Message, "Cannot start Sockseek daemon on");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void DetermineLocalExitCode_ManualSkipsOnly_ReturnsSuccess()
    {
        var skipped = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        skipped.SetSkipped(JobSkipReason.Manual);
        var queue = new JobList("root", [skipped]);
        queue.SetDone();

        var exitCode = Sockseek.Cli.Program.DetermineLocalExitCode(queue);

        Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exitCode);
    }

    [TestMethod]
    public async Task MainCore_NoSuitableFile_ReturnsWorkFailed()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-exit-empty-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-exit-empty-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);

        try
        {
            var exitCode = await Sockseek.Cli.Program.MainCore([
                "Definitely Missing Track",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--no-progress",
                "--path", outputDir,
            ]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task MainCore_SuccessfulDownload_ReturnsSuccess()
    {
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-exit-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-exit-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(musicRoot, "Artist - Song.mp3"), [1, 2, 3, 4]);

        try
        {
            var exitCode = await Sockseek.Cli.Program.MainCore([
                "Artist - Song",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--no-progress",
                "--path", outputDir,
            ]);

            Assert.AreEqual(Sockseek.Cli.Program.CliExitCode.Success, exitCode);
        }
        finally
        {
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    public async Task Main_UnknownFlag_WritesDiagnosticToStderr()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--not-a-real-flag"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.UsageError, exitCode);
            Assert.AreEqual("", stdout.ToString());
            StringAssert.Contains(stderr.ToString(), "Input error:");
            StringAssert.Contains(stderr.ToString(), "--not-a-real-flag");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
        }
    }

    [TestMethod]
    public async Task Main_MissingCredentials_WritesCleanErrorWithoutEmptyProgressSummary()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--no-config", "blah"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
            StringAssert.Contains(stdout.ToString(), "[cli] Starting CLI session in local mode");
            StringAssert.Contains(stderr.ToString(), "[error] [cli] Soulseek login failed: Missing Soulseek username and password.");
            var combined = stdout.ToString() + stderr.ToString();
            Assert.IsFalse(combined.Contains("0 active", StringComparison.Ordinal), "Missing credentials must not print an empty progress summary.");
            Assert.IsFalse(combined.Contains("--random-login", StringComparison.Ordinal), "Credential guidance must not advertise random login.");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
        }
    }

    [TestMethod]
    public async Task Main_ProgressJsonMissingCredentials_WritesHumanLogsToStderrOnly()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main(["--no-config", "blah", "--progress-json"]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.WorkFailed, exitCode);
            Assert.AreEqual("", stdout.ToString());
            StringAssert.Contains(stderr.ToString(), "[cli] Starting CLI session in local mode");
            StringAssert.Contains(stderr.ToString(), "[error] [cli] Soulseek login failed: Missing Soulseek username and password.");
            Assert.IsFalse(stderr.ToString().Contains("0 active", StringComparison.Ordinal), "Missing credentials must not print an empty progress summary.");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
        }
    }

    [TestMethod]
    public async Task Main_ProgressJson_WritesOnlyJsonLinesToStdout()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-json-music-" + Guid.NewGuid());
        var outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-json-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(Path.Combine(musicRoot, "Artist - Song.mp3"), [1, 2, 3, 4]);

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await Sockseek.Cli.Program.Main([
                "Artist - Song",
                "--song",
                "--mock-files-dir", musicRoot,
                "--mock-files-no-read-tags",
                "--no-config",
                "--progress-json",
                "--path", outputDir,
            ]);

            Assert.AreEqual((int)Sockseek.Cli.Program.CliExitCode.Success, exitCode);
            var lines = stdout.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.IsTrue(lines.Length > 0, "Expected at least one JSON progress line on stdout.");

            foreach (var line in lines)
                using (JsonDocument.Parse(line)) { }

            StringAssert.Contains(stderr.ToString(), "[soulseek]");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SockseekLog.RemoveNonFileOutputs();
            if (Directory.Exists(musicRoot)) Directory.Delete(musicRoot, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
}
