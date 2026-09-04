using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Planning;

/// <summary>
/// Owns the discovery-free extraction step shared by runtime Start and planning
/// consumers. It resolves the extractor, executes it, and applies the exact
/// semantic transforms that turn extractor output into a runnable job tree.
/// </summary>
public static class JobExtractionPlanner
{
    public static async Task<JobExtractionPlanResult> ExtractAsync(
        ExtractJob job,
        ExtractorContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Config == null)
            throw new InvalidOperationException("Effective settings must be resolved before extraction planning.");

        (InputType inputType, IExtractor extractor) = ExtractorRegistry.GetMatchingExtractor(
            job.Input,
            job.InputType ?? InputType.None,
            job.Config);
        job.InputType = inputType;
        job.Config.Extraction.InputType = inputType;

        cancellationToken.ThrowIfCancellationRequested();
        ExtractionSettings extraction = EffectiveExtractionSettings(job);
        Job extracted = await extractor.GetTracks(
            job.Input,
            extraction,
            context ?? ExtractorContext.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        extracted = NormalizeExtractedResult(job, extracted, extraction.UpgradeToAlbum);
        return new JobExtractionPlanResult(extracted, inputType, extractor);
    }

    public static ExtractionSettings EffectiveExtractionSettings(ExtractJob job)
    {
        if (job.RequestedModeOverride == null)
            return job.Config.Extraction;

        var extraction = SettingsCloner.Clone(job.Config.Extraction);
        extraction.RequestedMode = job.RequestedModeOverride;
        return extraction;
    }

    public static Job NormalizeExtractedResult(
        ExtractJob job,
        Job extracted,
        bool forceAlbumUpgrade)
    {
        job.Result = extracted;

        if (extracted is IUpgradeable upgradeable)
        {
            List<Job> upgraded = upgradeable
                .Upgrade(forceAlbumUpgrade, job.Config.Search.IsAggregate)
                .ToList();
            if (upgraded.Count == 1)
            {
                job.Result = upgraded[0];
                extracted = job.Result;
            }
            else
            {
                job.Result = new JobList(extracted.ItemName, upgraded);
                extracted = job.Result;
                extracted.CopySharedFieldsFrom(upgradeable as Job ?? extracted);
            }
        }

        AssignWorkflowId(extracted, job.WorkflowId);
        AssignSourceInputType(extracted, job.InputType);
        AssignArtifactId(extracted, job.ArtifactId);
        if (job.ArtifactId != null)
            ClearSourceMutation(extracted);
        SubmissionIdentity.AssignGeneratedResult(job, extracted);
        if (job.ResultDownloadBehaviorPolicy != null)
            JobOrchestrator.ApplyDownloadBehaviorPolicy(extracted, job.ResultDownloadBehaviorPolicy);

        if (extracted.LineNumber == 0)
            extracted.LineNumber = job.LineNumber;
        extracted.ItemNumber = job.ItemNumber;
        extracted.SourceMutation ??= job.SourceMutation;
        if (job.EnablesIndexByDefault)
            extracted.EnablesIndexByDefault = true;

        extracted.ExtractorCond = FileConditionPatch.Merge(extracted.ExtractorCond, job.ExtractorCond);
        extracted.ExtractorPrefCond = FileConditionPatch.Merge(extracted.ExtractorPrefCond, job.ExtractorPrefCond);
        extracted.ExtractorFolderCond = FolderConditionPatch.Merge(extracted.ExtractorFolderCond, job.ExtractorFolderCond);
        extracted.ExtractorPrefFolderCond = FolderConditionPatch.Merge(extracted.ExtractorPrefFolderCond, job.ExtractorPrefFolderCond);

        if (extracted is JobList list
            && list.Jobs.Count == 1
            && list.Jobs[0] is SongJob innerSong
            && innerSong.LineNumber == 0)
        {
            innerSong.LineNumber = job.LineNumber;
            innerSong.ItemNumber = job.ItemNumber;
            innerSong.SourceMutation ??= job.SourceMutation;
            if (job.EnablesIndexByDefault)
                innerSong.EnablesIndexByDefault = true;
        }

        return extracted;
    }

    private static void AssignWorkflowId(Job job, Guid workflowId)
        => ApplyToExtractedTree(job, item => item.WorkflowId = workflowId);

    private static void AssignSourceInputType(Job job, InputType? inputType)
        => ApplyToExtractedTree(job, item => item.SourceInputType = inputType);

    private static void AssignArtifactId(Job job, string? artifactId)
        => ApplyToExtractedTree(job, item => item.ArtifactId = artifactId);

    private static void ClearSourceMutation(Job job)
        => ApplyToExtractedTree(job, item => item.SourceMutation = null);

    private static void ApplyToExtractedTree(Job job, Action<Job> apply)
    {
        apply(job);
        switch (job)
        {
            case JobList list:
                foreach (Job child in list.Jobs)
                    ApplyToExtractedTree(child, apply);
                break;
            case AggregateJob aggregate:
                foreach (SongJob song in aggregate.Songs)
                    ApplyToExtractedTree(song, apply);
                break;
            case AlbumAggregateJob aggregate:
                foreach (AlbumJob album in aggregate.Albums)
                    ApplyToExtractedTree(album, apply);
                break;
        }
    }
}

public sealed record JobExtractionPlanResult(
    Job Result,
    InputType InputType,
    IExtractor Extractor);
