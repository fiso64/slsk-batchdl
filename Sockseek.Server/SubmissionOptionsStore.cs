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
    private readonly ConcurrentDictionary<Guid, byte> isolatedJobOptions = [];
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
    {
        isolatedJobOptions.TryRemove(jobId, out _);
        jobOptions[jobId] = options ?? new SubmissionOptionsDto();
    }

    public void SetIsolatedJobOptions(Guid jobId, SubmissionOptionsDto? options)
    {
        jobOptions[jobId] = options ?? new SubmissionOptionsDto();
        isolatedJobOptions[jobId] = 0;
    }

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
            isolatedJobOptions.TryRemove(jobId, out _);
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
        workflowOptions.TryGetValue(job.WorkflowId, out var workflowEntry);
        jobOptions.TryGetValue(job.Id, out var itemOptions);
        if (isolatedJobOptions.ContainsKey(job.Id))
            return itemOptions;
        return Merge(workflowEntry?.Options, itemOptions);
    }

    public string? GetJobOutputParentDir(Guid jobId)
        => jobOutputParentDirs.TryGetValue(jobId, out var outputParentDir)
            ? outputParentDir
            : null;

    internal (int Workflows, int Jobs, int OutputPaths) RetainedStateCounts
        => (workflowOptions.Count, jobOptions.Count, jobOutputParentDirs.Count);

    internal static SubmissionOptionsDto? Merge(
        SubmissionOptionsDto? submission,
        SubmissionOptionsDto? item)
    {
        if (submission == null)
            return item;
        if (item == null)
            return submission;

        IReadOnlyDictionary<string, bool>? context = submission.ProfileContext;
        if (item.ProfileContext != null)
        {
            var merged = submission.ProfileContext == null
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : new Dictionary<string, bool>(submission.ProfileContext, StringComparer.Ordinal);
            foreach (var (key, value) in item.ProfileContext)
                merged[key] = value;
            context = merged;
        }

        return new SubmissionOptionsDto(
            item.WorkflowId ?? submission.WorkflowId,
            item.OutputParentDir ?? submission.OutputParentDir,
            item.ProfileNames ?? submission.ProfileNames,
            context,
            DownloadSettingsPatchDtoMapper.Combine(
                submission.DownloadSettings,
                item.DownloadSettings));
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

    public void SetIsolatedJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => Options.SetIsolatedJobOptions(jobId, options);

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
        => Options.SetJobOutputParentDir(jobId, outputParentDir);

    public void RemoveWorkflowOptions(Guid workflowId)
        => Options.RemoveWorkflowOptions(workflowId);

    public long CaptureWorkflowVersion(Guid workflowId)
        => Options.CaptureWorkflowVersion(workflowId);

    public void RetireWorkflow(Guid workflowId, IReadOnlyCollection<Guid> jobIds, long expectedVersion)
        => Options.RetireWorkflow(workflowId, jobIds, expectedVersion);

    public DownloadSettings Resolve(
        DownloadSettings inherited,
        Job job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None)
    {
        SubmissionOptionsDto? options = Options.GetOptions(job);
        string? outputParentDir = Options.GetJobOutputParentDir(job.Id);
        if (inner is IJobSettingsRequestResolver requestResolver)
        {
            return requestResolver.Resolve(
                inherited,
                job,
                inheritance,
                CreateRequestLayers(options, outputParentDir));
        }

        var settings = inner.Resolve(inherited, job, inheritance);
        if (inheritance == JobSettingsInheritance.SearchConstraints)
            JobSettingsComposer.PreserveInheritedSearchConstraints(settings, inherited);
        ApplyTo(settings, options, outputParentDir);
        normalize?.Invoke(settings);
        return settings;
    }

    public DownloadSettings ResolveFollowUp(Job job, SubmissionOptionsDto? options)
        => ResolveWithOptions(
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic),
            job,
            options,
            JobSettingsInheritance.None);

    public JobSettingsCompositionResult ResolveDetailed(
        Job job,
        SubmissionOptionsDto? options)
        => ResolveDetailed(
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic),
            job,
            options,
            JobSettingsInheritance.None);

    public JobSettingsCompositionResult ResolveDetailed(
        DownloadSettings inherited,
        Job job,
        SubmissionOptionsDto? options,
        JobSettingsInheritance inheritance)
    {
        if (inner is not IDetailedJobSettingsRequestResolver detailed)
        {
            throw new NotSupportedException(
                $"{inner.GetType().Name} does not expose detailed settings composition.");
        }

        return detailed.ResolveDetailed(
            inherited,
            job,
            inheritance,
            CreateRequestLayers(options));
    }

    private DownloadSettings ResolveWithOptions(
        DownloadSettings inherited,
        Job job,
        SubmissionOptionsDto? options,
        JobSettingsInheritance inheritance)
    {
        if (inner is IJobSettingsRequestResolver requestResolver)
        {
            return requestResolver.Resolve(
                inherited,
                job,
                inheritance,
                CreateRequestLayers(options));
        }

        DownloadSettings settings = inner.Resolve(inherited, job, inheritance);
        ApplyTo(settings, options, outputParentDir: null);
        normalize?.Invoke(settings);
        return settings;
    }

    private static JobSettingsRequestLayers CreateRequestLayers(
        SubmissionOptionsDto? options,
        string? outputParentDir = null)
    {
        var requestPatch = new DownloadSettingsPatch();
        var explicitFields = new HashSet<string>(
            DownloadSettingsPatchDtoMapper.ExplicitFields(options?.DownloadSettings),
            StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir)
            || outputParentDir != null)
        {
            explicitFields.Add("Output.ParentDir");
        }
        requestPatch.Add(
            settings => ApplyTo(settings, options, outputParentDir),
            explicitFields);
        return new JobSettingsRequestLayers(
            options?.ProfileNames,
            ToProfileContext(options?.ProfileContext),
            requestPatch);
    }

    private static void ApplyTo(
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        string? outputParentDir)
    {
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
        if (outputParentDir != null)
            settings.Output.ParentDir = outputParentDir;
    }

    private static ProfileContext ToProfileContext(IReadOnlyDictionary<string, bool>? values)
    {
        var context = new ProfileContext();
        if (values != null)
        {
            foreach (var (key, value) in values)
                context.Values[key] = value;
        }

        return context;
    }
}
