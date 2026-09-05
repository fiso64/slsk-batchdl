using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Planning;

public enum PlannedJobRole
{
    UserRoot,
    SemanticResult,
    Orchestration,
    ExecutionChild,
}

public enum PlannedJobState
{
    Ready,
    Failed,
}

/// <summary>
/// Fixed-size record emitted by <see cref="JobPlanner"/>. RuntimeJob and
/// EffectiveSettings are Core values for immediate consumers; daemon adapters
/// persist normalized command/settings projections rather than object graphs.
/// </summary>
public sealed record PlannedJobNode(
    string Ref,
    string? ParentRef,
    PlannedJobRole Role,
    PlannedJobState State,
    Job RuntimeJob,
    DownloadSettings? EffectiveSettings,
    int DirectChildCount,
    string? FailureCode = null,
    string? FailureMessage = null,
    SubmissionSourceRevision? SourceRevision = null);

/// <summary>
/// Storage-agnostic recursive planner shared by local presentation and daemon
/// preview/submission adapters. It performs extraction but never Soulseek
/// discovery or download, and streams records as each independent node resolves.
/// </summary>
public sealed class JobPlanner(
    IJobSettingsResolver settingsResolver,
    ExtractorContext? extractorContext = null)
{
    public IAsyncEnumerable<PlannedJobNode> PlanAsync(
        Job root,
        DownloadSettings inherited,
        CancellationToken cancellationToken = default)
        => PlanNodeAsync(
            root,
            inherited,
            "0",
            parentRef: null,
            PlannedJobRole.UserRoot,
            JobSettingsInheritance.None,
            sourceRevision: null,
            cancellationToken);

    private async IAsyncEnumerable<PlannedJobNode> PlanNodeAsync(
        Job job,
        DownloadSettings inherited,
        string nodeRef,
        string? parentRef,
        PlannedJobRole role,
        JobSettingsInheritance inheritance,
        SubmissionSourceRevision? sourceRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadSettings? settings = null;
        Exception? settingsFailure = null;
        try
        {
            settings = JobPreparer.ResolveSemanticSettings(
                job,
                inherited,
                settingsResolver,
                inheritance);
            job.Config = settings;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            settingsFailure = exception;
        }
        if (settingsFailure != null)
        {
            yield return Failed(nodeRef, parentRef, role, job, "settings", settingsFailure);
            yield break;
        }
        DownloadSettings effectiveSettings = settings!;
        job.PlannedEffectiveSettings = SettingsCloner.Clone(effectiveSettings);

        if (job is ExtractJob extract)
        {
            JobExtractionPlanResult? extraction = null;
            Exception? extractionFailure = null;
            try
            {
                extraction = await JobExtractionPlanner.ExtractAsync(
                    extract,
                    extractorContext,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                extractionFailure = exception;
            }
            if (extractionFailure != null)
            {
                extract.HasPlannedExtraction = true;
                extract.PlannedExtractionFailure = extractionFailure.Message;
                yield return Failed(
                    nodeRef,
                    parentRef,
                    PlannedJobRole.Orchestration,
                    job,
                    "extraction",
                    extractionFailure);
                yield break;
            }

            extract.HasPlannedExtraction = true;
            extract.PlannedExtractionFailure = null;
            SubmissionSourceRevision? extractedSourceRevision =
                await CaptureSourceRevisionAsync(extract, cancellationToken)
                    .ConfigureAwait(false);
            extract.PlannedSourceRevision = extractedSourceRevision;

            yield return new PlannedJobNode(
                nodeRef,
                parentRef,
                PlannedJobRole.Orchestration,
                PlannedJobState.Ready,
                job,
                SettingsCloner.Clone(effectiveSettings),
                1,
                SourceRevision: extractedSourceRevision);
            await foreach (PlannedJobNode node in PlanNodeAsync(
                extraction!.Result,
                effectiveSettings,
                nodeRef + "/result",
                nodeRef,
                PlannedJobRole.SemanticResult,
                JobSettingsInheritance.SearchConstraints,
                extractedSourceRevision,
                cancellationToken).ConfigureAwait(false))
            {
                yield return node;
            }
            yield break;
        }

        if (job is JobList list)
        {
            yield return new PlannedJobNode(
                nodeRef,
                parentRef,
                role == PlannedJobRole.UserRoot ? role : PlannedJobRole.Orchestration,
                PlannedJobState.Ready,
                job,
                SettingsCloner.Clone(effectiveSettings),
                list.Jobs.Count,
                SourceRevision: sourceRevision);
            for (int index = 0; index < list.Jobs.Count; index++)
            {
                await foreach (PlannedJobNode node in PlanNodeAsync(
                    list.Jobs[index],
                    effectiveSettings,
                    $"{nodeRef}/{index}",
                    nodeRef,
                    PlannedJobRole.ExecutionChild,
                    JobSettingsInheritance.SearchConstraints,
                    sourceRevision,
                    cancellationToken).ConfigureAwait(false))
                {
                    yield return node;
                }
            }
            yield break;
        }

        yield return new PlannedJobNode(
            nodeRef,
            parentRef,
            role,
            PlannedJobState.Ready,
            job,
            SettingsCloner.Clone(effectiveSettings),
            DirectChildCount(job),
            SourceRevision: sourceRevision);
    }

    private static PlannedJobNode Failed(
        string nodeRef,
        string? parentRef,
        PlannedJobRole role,
        Job job,
        string code,
        Exception exception)
        => new(
            nodeRef,
            parentRef,
            role,
            PlannedJobState.Failed,
            job,
            null,
            0,
            code,
            exception.Message);

    private static int DirectChildCount(Job job) => job switch
    {
        AggregateJob aggregate => aggregate.Songs.Count,
        AlbumAggregateJob aggregate => aggregate.Albums.Count,
        AlbumJob album => album.TrackJobs.Count,
        RemoteDirectoryJob directory => directory.FileJobs.Count,
        _ => 0,
    };

    private static async Task<SubmissionSourceRevision?> CaptureSourceRevisionAsync(
        ExtractJob job,
        CancellationToken cancellationToken)
    {
        if (job.PlannedSourceRevision != null)
            return job.PlannedSourceRevision;
        if (!File.Exists(job.Input))
            return null;

        string path = Path.GetFullPath(job.Input);
        var info = new FileInfo(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new SubmissionSourceRevision(
            "local-file",
            path,
            Convert.ToHexString(digest).ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc);
    }
}
