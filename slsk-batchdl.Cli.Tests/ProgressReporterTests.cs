using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Cli;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Server;

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

    [TestMethod]
    public void RemoteAlbumFolderConversion_PreservesPayloadFileFailureState()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var fileJobId = Guid.NewGuid();
            var song = new SongJobPayloadDto(
                new SongQueryDto("Artist", "Track", "", "", -1, false, false),
                CandidateCount: 1,
                DownloadPath: null,
                ResolvedUsername: "user",
                ResolvedFilename: @"Artist\Album\01. Artist - Track.flac",
                ResolvedHasFreeUploadSlot: true,
                ResolvedUploadSpeed: 100,
                ResolvedSize: 100,
                ResolvedExtension: ".flac",
                ResolvedAttributes: null,
                JobId: fileJobId,
                DisplayId: 7,
                Candidates: null,
                State: nameof(JobState.Failed),
                FailureReason: nameof(FailureReason.Cancelled),
                FailureMessage: null);
            var folder = new AlbumFolderDto(
                new AlbumFolderRefDto("user", @"Artist\Album"),
                "user",
                @"Artist\Album",
                SearchFileCount: 1,
                SearchAudioFileCount: 1,
                SearchSortedAudioLengths: [],
                SearchRepresentativeAudioFilename: @"Artist\Album\01. Artist - Track.flac",
                HasSearchMetadata: true,
                Files: [song]);

            var converted = (AlbumFolder)InvokePrivate(reporter, "ToAlbumFolder", folder)!;

            Assert.AreEqual(1, converted.Files.Count);
            Assert.AreEqual(JobState.Failed, converted.Files[0].State);
            Assert.AreEqual(FailureReason.Cancelled, converted.Files[0].FailureReason);
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumTrackDownloadCompleted_ReconcilesLeftoverRequestedBars()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var workflowId = Guid.NewGuid();
            var summary = new JobSummaryDto(
                albumJobId,
                DisplayId: 6,
                WorkflowId: workflowId,
                Kind: "album",
                State: nameof(JobState.Downloading),
                ItemName: "Artist Album",
                QueryText: "Artist Album",
                FailureReason: null,
                FailureMessage: null,
                ParentJobId: null,
                ResultJobId: null,
                AppliedAutoProfiles: [],
                Presentation: new PresentationHintsDto(false, null, 6, null));
            var folder = new AlbumFolderDto(
                new AlbumFolderRefDto("local", @"Artist\Album"),
                "local",
                @"Artist\Album",
                SearchFileCount: 1,
                SearchAudioFileCount: 1,
                SearchSortedAudioLengths: [],
                SearchRepresentativeAudioFilename: @"Artist\Album\01. Artist - Track.flac",
                HasSearchMetadata: true,
                Files:
                [
                    new SongJobPayloadDto(
                        new SongQueryDto("Artist", "Track", "", "", -1, false, false),
                        CandidateCount: 1,
                        DownloadPath: null,
                        ResolvedUsername: "local",
                        ResolvedFilename: @"Artist\Album\01. Artist - Track.flac",
                        ResolvedHasFreeUploadSlot: true,
                        ResolvedUploadSpeed: 100,
                        ResolvedSize: 100,
                        ResolvedExtension: ".flac",
                        ResolvedAttributes: null,
                        JobId: fileJobId,
                        DisplayId: 7,
                        Candidates: null,
                        State: nameof(JobState.Pending),
                        FailureReason: null,
                        FailureMessage: null)
                ]);

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(summary, folder));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            var failedSummary = summary with
            {
                State = nameof(JobState.Failed),
                FailureReason = nameof(FailureReason.Cancelled),
            };
            InvokePrivate(reporter, "ReportAlbumDownloadCompleted", new AlbumDownloadCompletedEventDto(failedSummary));

            Assert.IsFalse(
                HasBackendBarData(reporter, fileJobId),
                "Album completion should reconcile and remove leftover requested bars.");
        }
        finally
        {
            reporter.Stop();
        }
    }

    [TestMethod]
    public void RemoteAlbumJobUpsertedTerminalState_ReconcilesLeftoverRequestedBars()
    {
        var reporter = new CliProgressReporter(new CliSettings());
        try
        {
            var albumJobId = Guid.NewGuid();
            var fileJobId = Guid.NewGuid();
            var summary = CreateAlbumSummary(albumJobId, nameof(JobState.Downloading), null);
            var folder = CreateSingleFileAlbumFolder(fileJobId, nameof(JobState.Pending), null);

            InvokePrivate(reporter, "ReportAlbumTrackDownloadStarted", new AlbumTrackDownloadStartedEventDto(summary, folder));
            Assert.IsTrue(HasBackendBarData(reporter, fileJobId));

            InvokePrivate(
                reporter,
                "ReportJobUpserted",
                CreateAlbumSummary(albumJobId, nameof(JobState.Failed), nameof(FailureReason.Cancelled)));

            Assert.IsFalse(
                HasBackendBarData(reporter, fileJobId),
                "Terminal album job upserts should reconcile leftover requested bars even if album.download-completed has not arrived.");
        }
        finally
        {
            reporter.Stop();
        }
    }

    private static object? InvokePrivate(object target, string name, params object[] args)
    {
        var argTypes = args.Select(a => a.GetType()).ToArray();
        return target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic, binder: null, types: argTypes, modifiers: null)!
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

    private static bool HasBackendBarData(CliProgressReporter reporter, Guid jobId)
    {
        var bars = typeof(CliProgressReporter)
            .GetField("_backendBars", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reporter)!;

        object?[] args = [jobId, null];
        return (bool)bars.GetType().GetMethod("TryGetValue")!.Invoke(bars, args)!;
    }

    private static JobSummaryDto CreateAlbumSummary(Guid jobId, string state, string? failureReason)
        => new(
            jobId,
            DisplayId: 6,
            WorkflowId: Guid.NewGuid(),
            Kind: "album",
            State: state,
            ItemName: "Artist Album",
            QueryText: "Artist Album",
            FailureReason: failureReason,
            FailureMessage: null,
            ParentJobId: null,
            ResultJobId: null,
            AppliedAutoProfiles: [],
            Presentation: new PresentationHintsDto(false, null, 6, null));

    private static AlbumFolderDto CreateSingleFileAlbumFolder(Guid fileJobId, string state, string? failureReason)
        => new(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            "local",
            @"Artist\Album",
            SearchFileCount: 1,
            SearchAudioFileCount: 1,
            SearchSortedAudioLengths: [],
            SearchRepresentativeAudioFilename: @"Artist\Album\01. Artist - Track.flac",
            HasSearchMetadata: true,
            Files:
            [
                new SongJobPayloadDto(
                    new SongQueryDto("Artist", "Track", "", "", -1, false, false),
                    CandidateCount: 1,
                    DownloadPath: null,
                    ResolvedUsername: "local",
                    ResolvedFilename: @"Artist\Album\01. Artist - Track.flac",
                    ResolvedHasFreeUploadSlot: true,
                    ResolvedUploadSpeed: 100,
                    ResolvedSize: 100,
                    ResolvedExtension: ".flac",
                    ResolvedAttributes: null,
                    JobId: fileJobId,
                    DisplayId: 7,
                    Candidates: null,
                    State: state,
                    FailureReason: failureReason,
                    FailureMessage: null)
            ]);
}
