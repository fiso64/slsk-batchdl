using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Tests.ProgressReporterTests;

[TestClass]
public class CliProgressReporterTests
{
    [TestMethod]
    public void DownloadStart_NoProgress_DoesNotCreateProgressBar()
    {
        var reporter = new CliProgressReporter(new CliSettings { NoProgress = true });
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" });

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);

            Assert.IsFalse(HasBarData(reporter, song));
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void StateChanged_FailedPreResolvedSong_DoesNotRenderAsSucceeded()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var file = new Soulseek.File(1, @"Music\Artist\Song.flac", 100, ".flac");
            var response = new Soulseek.SearchResponse("user", 1, true, 100, 0, [file]);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" })
            {
                ResolvedTarget = candidate,
                State = JobState.Failed,
                FailureReason = FailureReason.Cancelled,
            };

            InvokePrivate(reporter, "ReportDownloadStart", song, candidate);
            var barData = GetBarData(reporter, song);

            InvokePrivate(reporter, "ReportStateChanged", song);

            Assert.AreEqual("Failed", GetField<string>(barData, "StateLabel"));
            Assert.AreNotEqual(100, GetField<int>(barData, "Pct"));
            StringAssert.Contains(GetField<string>(barData, "BaseText"), "Cancelled");
        }
        finally
        {
            reporter.Stop();
        }
    }

    private static void InvokePrivate(object target, string name, params object[] args)
    {
        var argTypes = args.Select(a => a.GetType()).ToArray();
        target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, types: argTypes, modifiers: null)!
            .Invoke(target, args);
    }

    private static object GetBarData(CliProgressReporter reporter, SongJob song)
    {
        Assert.IsTrue(TryGetBarData(reporter, song, out var barData));
        return barData!;
    }

    private static bool HasBarData(CliProgressReporter reporter, SongJob song)
    {
        return TryGetBarData(reporter, song, out _);
    }

    private static bool TryGetBarData(CliProgressReporter reporter, SongJob song, out object? barData)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_bars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [song, null];
        var found = (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
        barData = args[1];
        return found;
    }

    private static T GetField<T>(object target, string name)
    {
        return (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }
}
