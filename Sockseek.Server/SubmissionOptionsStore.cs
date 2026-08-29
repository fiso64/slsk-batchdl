using System.Collections.Concurrent;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public sealed class SubmissionOptionsStore
{
    private readonly ConcurrentDictionary<Guid, WorkflowOptionsEntry> workflowOptions = [];
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> jobOptions = [];
    private readonly ConcurrentDictionary<Guid, string> jobOutputParentDirs = [];

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
    {
        if (options == null)
        {
            workflowOptions.TryAdd(
                workflowId,
                new WorkflowOptionsEntry(new SubmissionOptionsDto(), Version: 1));
            return;
        }

        workflowOptions.AddOrUpdate(
            workflowId,
            _ => new WorkflowOptionsEntry(options, Version: 1),
            (_, current) => IsWorkflowOnly(options)
                ? current
                : new WorkflowOptionsEntry(options, checked(current.Version + 1)));
    }

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => jobOptions[jobId] = options ?? new SubmissionOptionsDto();

    public void RemoveWorkflowOptions(Guid workflowId)
        => workflowOptions.TryRemove(workflowId, out _);

    public long CaptureWorkflowVersion(Guid workflowId)
        => workflowOptions.TryGetValue(workflowId, out var entry)
            ? entry.Version
            : 0;

    public void RetireWorkflow(
        Guid workflowId,
        IReadOnlyCollection<Guid> jobIds,
        long expectedVersion)
    {
        foreach (Guid jobId in jobIds)
        {
            jobOptions.TryRemove(jobId, out _);
            jobOutputParentDirs.TryRemove(jobId, out _);
        }

        if (workflowOptions.TryGetValue(workflowId, out var entry)
            && entry.Version == expectedVersion)
        {
            workflowOptions.TryRemove(
                new KeyValuePair<Guid, WorkflowOptionsEntry>(workflowId, entry));
        }
    }

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
    {
        if (!string.IsNullOrWhiteSpace(outputParentDir))
            jobOutputParentDirs[jobId] = outputParentDir;
    }

    public SubmissionOptionsDto? GetOptions(Job job)
    {
        if (jobOptions.TryGetValue(job.Id, out var options))
            return options;

        return workflowOptions.TryGetValue(job.WorkflowId, out var entry)
            ? entry.Options
            : null;
    }

    public string? GetJobOutputParentDir(Guid jobId)
        => jobOutputParentDirs.TryGetValue(jobId, out var outputParentDir)
            ? outputParentDir
            : null;

    internal (int Workflows, int Jobs, int OutputPaths) RetainedStateCounts
        => (workflowOptions.Count, jobOptions.Count, jobOutputParentDirs.Count);

    public void ApplyTo(DownloadSettings settings, SubmissionOptionsDto? options, Guid jobId)
    {
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);

        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;

        if (GetJobOutputParentDir(jobId) is { } outputParentDir)
            settings.Output.ParentDir = outputParentDir;
    }

    public static void PreserveInheritedSearchConstraints(DownloadSettings settings, DownloadSettings inherited)
    {
        settings.Search.NecessaryCond = settings.Search.NecessaryCond.With(inherited.Search.NecessaryCond);
        settings.Search.PreferredCond = settings.Search.PreferredCond.With(inherited.Search.PreferredCond);
        settings.Search.NecessaryFolderCond = MergeFolderConditions(settings.Search.NecessaryFolderCond, inherited.Search.NecessaryFolderCond);
        settings.Search.PreferredFolderCond = MergeFolderConditions(settings.Search.PreferredFolderCond, inherited.Search.PreferredFolderCond);
    }

    private static FolderConditions MergeFolderConditions(FolderConditions current, FolderConditions inherited)
    {
        var result = new FolderConditions(current)
        {
            MinTrackCount = inherited.MinTrackCount ?? current.MinTrackCount,
            MaxTrackCount = inherited.MaxTrackCount ?? current.MaxTrackCount,
        };
        result.AddRequiredTrackTitles(inherited.RequiredTrackTitles);
        return result;
    }

    private static bool IsWorkflowOnly(SubmissionOptionsDto options)
        => options.OutputParentDir == null
        && options.ProfileNames == null
        && options.ProfileContext == null
        && options.DownloadSettings == null;

    private sealed record WorkflowOptionsEntry(SubmissionOptionsDto Options, long Version);
}

public sealed class SubmissionOptionsJobSettingsResolver(
    IJobSettingsResolver inner,
    SubmissionOptionsStore? optionsStore = null,
    Action<DownloadSettings>? normalize = null)
    : IJobSettingsResolver, IWorkflowSettingsLifetime
{
    public SubmissionOptionsStore Options { get; } = optionsStore ?? new SubmissionOptionsStore();

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
        => Options.SetWorkflowOptions(workflowId, options);

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => Options.SetJobOptions(jobId, options);

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
        => Options.SetJobOutputParentDir(jobId, outputParentDir);

    public long CaptureWorkflowVersion(Guid workflowId)
        => Options.CaptureWorkflowVersion(workflowId);

    public void RetireWorkflow(Guid workflowId, IReadOnlyCollection<Guid> jobIds, long expectedVersion)
        => Options.RetireWorkflow(workflowId, jobIds, expectedVersion);

    public DownloadSettings Resolve(DownloadSettings inherited, Job job)
    {
        var settings = inner.Resolve(inherited, job);
        SubmissionOptionsStore.PreserveInheritedSearchConstraints(settings, inherited);
        Options.ApplyTo(settings, Options.GetOptions(job), job.Id);
        normalize?.Invoke(settings);
        return settings;
    }
}
