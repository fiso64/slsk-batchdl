using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class EngineEventDtoAdapterTests
{
    [TestMethod]
    public void Attach_MapsDownloadProgressToSharedServerEventDto()
    {
        var events = new DownloadEvents();
        var published = new List<(string Type, object Payload)>();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
        var transferId = Guid.NewGuid();
        var file = new Soulseek.File(1, "Music/Artist/Title.mp3", 100, ".mp3");
        var candidate = new FileCandidate(new Soulseek.SearchResponse("user", 1, true, 100_000, 0, [file]), file);
        Attach(events, published);

        Raise(events, "RaiseDownloadProgress", transferId, song, candidate, "C:/downloads/Title.mp3", 42L, 100L);

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        var progress = (DownloadProgressEventDto)published[0].Payload;
        Assert.AreEqual(song.Id, progress.JobId);
        Assert.AreEqual(transferId, progress.TransferId);
        Assert.AreEqual(42, progress.BytesTransferred);
        Assert.AreEqual(100, progress.TotalBytes);
    }

    [TestMethod]
    public void Attach_MapsSongSearchingToSharedServerEventDto()
    {
        var events = new DownloadEvents();
        var published = new List<(string Type, object Payload)>();
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title", Album = "Album" });
        Attach(events, published);

        song.UpdateActivity(JobActivityPhase.Searching);
        Raise(events, "RaiseJobStateChanged", song);

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("song.searching", published[0].Type);
        var searching = (SongSearchingEventDto)published[0].Payload;
        Assert.AreEqual(song.Id, searching.JobId);
        Assert.AreEqual("Artist", searching.Query.Artist);
        Assert.AreEqual("Title", searching.Query.Title);
        Assert.AreEqual("Album", searching.Query.Album);
    }

    [TestMethod]
    public void Attach_MapsSearchRateLimitedToSharedServerEventDto()
    {
        var downloadEvents = new DownloadEvents();
        var searchEvents = new SearchEvents();
        var published = new List<(string Type, object Payload)>();
        var resetsAt = DateTimeOffset.UtcNow.AddSeconds(30);
        new EngineEventDtoAdapter(SummaryFor, (type, payload) => published.Add((type, payload)))
            .Attach(downloadEvents, searchEvents);

        Raise(searchEvents, "RaiseSearchRateLimited", resetsAt);

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("search.rate-limited", published[0].Type);
        var rateLimited = (SearchRateLimitedEventDto)published[0].Payload;
        Assert.AreEqual(resetsAt, rateLimited.ResetsAt);
    }

    private static JobSummaryDto SummaryFor(JobSnapshot job)
        => ServerSnapshotMapper.ToJobSummary(job);

    private static void Attach(DownloadEvents events, List<(string Type, object Payload)> published)
        => new EngineEventDtoAdapter(SummaryFor, (type, payload) => published.Add((type, payload)))
            .Attach(events, new SearchEvents());

    private static void Raise(DownloadEvents events, string methodName, params object[] args)
    {
        var method = typeof(DownloadEvents).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(DownloadEvents), methodName);
        method.Invoke(events, args);
    }

    private static void Raise(SearchEvents events, string methodName, params object[] args)
    {
        var method = typeof(SearchEvents).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SearchEvents), methodName);
        method.Invoke(events, args);
    }
}
