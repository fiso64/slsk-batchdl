using System.Collections.Concurrent;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Server;

public sealed class ServerProgressLogReporter
{
    private readonly ConcurrentDictionary<Job, string> jobStatusLines = new();

    public ServerProgressLogReporter(EngineSupervisor supervisor)
    {
        supervisor.EngineCreated += Attach;
    }

    private void Attach(DownloadEngine engine)
    {
        engine.Events.JobStarted += ReportJobStarted;
        engine.Events.JobCompleted += ReportJobCompleted;
        engine.Events.JobStatus += ReportJobStatus;
        engine.Events.StateChanged += ReportStateChanged;
        engine.Events.DownloadStarted += ReportDownloadStarted;
        engine.Events.AlbumDownloadStarted += (job, folder) => ReportAlbumDownloadStarted((AlbumJob)job, folder);
        engine.Events.AlbumTrackDownloadStarted += (job, folder) => ReportAlbumTrackDownloadStarted((AlbumJob)job, folder);
        engine.Events.AlbumDownloadCompleted += job => ReportAlbumDownloadCompleted((AlbumJob)job);
    }

    private void ReportJobStarted(Job job)
    {
        if (job is SongJob song && song.ResolvedTarget != null)
            return;

        string status = job is RetrieveFolderJob ? "retrieving folder" : "searching";
        Log($"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}");
    }

    private static void ReportJobCompleted(Job job, bool found, int lockedFiles)
    {
        string status = found
            ? (job is RetrieveFolderJob ? "found additional files in" : "found results")
            : (job is RetrieveFolderJob ? "no additional files found" : "no results found");
        string lockedMsg = !found && lockedFiles > 0 ? $" (Found {lockedFiles} locked files)" : "";
        Log($"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}{lockedMsg}");
    }

    private static void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        Log($"[{job.DisplayId}] AlbumJob: downloading: {job.ToString(true)}");
    }

    private static void ReportAlbumTrackDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        string folderName = string.IsNullOrWhiteSpace(folder.FolderPath)
            ? job.ToString(true)
            : folder.FolderPath;
        Log($"[{job.DisplayId}] AlbumJob: downloading tracks: {job.ToString(true)} - {folderName}");
    }

    private static void ReportAlbumDownloadCompleted(AlbumJob job)
    {
        Log($"[{job.DisplayId}] AlbumJob: {TerminalLabel(job)}: {job.ToString(true)}");
    }

    private static void ReportDownloadStarted(SongJob song, FileCandidate candidate)
    {
        Log($"Downloading: {DownloadDisplay(candidate)}");
    }

    private static void ReportStateChanged(SongJob song)
    {
        if (!IsTerminal(song.State))
            return;

        Log($"{TerminalLabel(song)}: {SongDisplay(song)}");
    }

    private void ReportJobStatus(Job job, string status)
    {
        var line = $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {job.ToString(true)}";
        if (jobStatusLines.TryGetValue(job, out var previous) && previous == line)
            return;

        jobStatusLines[job] = line;
        Log(line);
    }

    private static string GetJobTypePrefix(Job job)
        => job switch
        {
            RetrieveFolderJob => "RetrieveFolderJob: ",
            _ => job.GetType().Name + ": ",
        };

    private static bool IsTerminal(JobState state)
        => state is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped or JobState.NotFoundLastTime;

    private static string SongDisplay(SongJob song)
        => song.ChosenCandidate != null
            ? DownloadDisplay(song.ChosenCandidate)
            : $"[{song.DisplayId}] {song}";

    private static string DownloadDisplay(FileCandidate candidate)
        => $"{candidate.Username}\\..\\{Path.GetFileName(candidate.Filename)}";

    private static string TerminalLabel(Job job)
    {
        if (job.State is JobState.Done or JobState.AlreadyExists)
            return "Succeeded";

        string reason = FailureReasonLabel(job.FailureReason);
        return reason.Length > 0 ? $"Failed [{reason}]" : "Failed";
    }

    private static string FailureReasonLabel(FailureReason reason)
        => reason == FailureReason.None ? "" : reason.ToString();

    private static void Log(string message)
        => Logger.LogNonConsole(Logger.LogLevel.Info, message);
}
