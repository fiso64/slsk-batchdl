using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public static class RemoteTransferSubmissionPolicy
{
    public static void NormalizeInheritedSettings(
        Job job,
        DownloadSettings settings,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings>
            resolveChildSettings)
    {
        if (IsOrdinaryRemoteTransfer(job, settings))
            RemoteTransferNameFormatPolicy.ApplyInherited(settings.Output);

        if (job is not JobList list)
            return;

        foreach (Job child in list.Jobs)
        {
            DownloadSettings childSettings = resolveChildSettings(settings, child, null);
            NormalizeInheritedSettings(child, childSettings, resolveChildSettings);
        }
    }

    public static bool ContainsOrdinaryRemoteTransfer(
        Job job,
        DownloadSettings settings,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings>
            resolveChildSettings)
        => IsOrdinaryRemoteTransfer(job, settings)
            || job is JobList list && list.Jobs.Any(child =>
                ContainsOrdinaryRemoteTransfer(
                    child,
                    resolveChildSettings(settings, child, null),
                    resolveChildSettings));

    public static void ValidateChildOverrides(
        JobList list,
        IReadOnlyList<JobDraftDto> drafts,
        DownloadSettings parentSettings,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings>
            resolveChildSettings)
    {
        for (int index = 0; index < list.Jobs.Count && index < drafts.Count; index++)
        {
            Job child = list.Jobs[index];
            JobDraftDto draft = drafts[index];
            DownloadSettingsPatchDto? patch = JobRequestMapper.DraftDownloadSettings(draft);
            DownloadSettings childSettings = resolveChildSettings(
                parentSettings,
                child,
                patch);
            ValidateNode(child, draft, patch, childSettings, resolveChildSettings);
        }
    }

    private static bool ValidateNode(
        Job job,
        JobDraftDto draft,
        DownloadSettingsPatchDto? patch,
        DownloadSettings settings,
        Func<DownloadSettings, Job, DownloadSettingsPatchDto?, DownloadSettings>
            resolveChildSettings)
    {
        bool containsRemoteTransfer = IsOrdinaryRemoteTransfer(job, settings);
        if (job is JobList list && draft is JobListJobDraftDto listDraft)
        {
            for (int index = 0;
                 index < list.Jobs.Count && index < listDraft.Jobs.Count;
                 index++)
            {
                Job child = list.Jobs[index];
                JobDraftDto childDraft = listDraft.Jobs[index];
                DownloadSettingsPatchDto? childPatch =
                    JobRequestMapper.DraftDownloadSettings(childDraft);
                DownloadSettings childSettings = resolveChildSettings(
                    settings,
                    child,
                    childPatch);
                containsRemoteTransfer |= ValidateNode(
                    child,
                    childDraft,
                    childPatch,
                    childSettings,
                    resolveChildSettings);
            }
        }

        if (containsRemoteTransfer && patch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(patch);
        return containsRemoteTransfer;
    }

    public static bool IsOrdinaryRemoteTransfer(Job job, DownloadSettings settings)
        => job is RemoteFileJob or RemoteDirectoryJob
            || job is ExtractJob extract
            && Sockseek.Core.Extractors.SoulseekExtractor.InputMatches(extract.Input)
            && settings.Extraction.RequestedMode is null or ExtractionMode.General;
}
