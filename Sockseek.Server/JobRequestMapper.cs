using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Api;

namespace Sockseek.Server;

public static class JobRequestMapper
{
    public const int MaxSearchTextLength = 1024;
    public const int MaxSearchUriLength = 4096;

    public static ExtractJob CreateExtractJob(SubmitExtractJobRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            throw new ArgumentException("input is required for extract jobs");

        InputType? inputType = null;
        if (!string.IsNullOrWhiteSpace(request.InputType))
        {
            if (!Enum.TryParse<InputType>(request.InputType, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unsupported inputType '{request.InputType}'");
            inputType = parsed;
        }

        var job = new ExtractJob(request.Input, inputType)
        {
            ArtifactId = request.ArtifactId,
        };
        if (request.ResultDownloadBehavior != null)
            job.ResultDownloadBehaviorPolicy = ToDownloadBehaviorPolicy(request.ResultDownloadBehavior);

        return job;
    }

    public static SearchJob CreateSearchJob(SubmitSearchJobRequestDto request)
    {
        ValidateSearchText(request.QueryText, nameof(request.QueryText));
        return new(request.QueryText);
    }

    public static SearchJob CreateTrackSearchJob(SubmitTrackSearchJobRequestDto request)
        => new(ToSongQuery(request.SongQuery), request.IncludeFullResults);

    public static SearchJob CreateAlbumSearchJob(SubmitAlbumSearchJobRequestDto request)
        => new(ToAlbumQuery(request.AlbumQuery));

    public static SongJob CreateSongJob(SubmitSongJobRequestDto request)
        => ApplyDownloadBehavior(new SongJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumJob CreateAlbumJob(SubmitAlbumJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

    public static AggregateJob CreateAggregateJob(SubmitAggregateJobRequestDto request)
        => ApplyDownloadBehavior(new AggregateJob(ToSongQuery(request.SongQuery)), request.DownloadBehavior);

    public static AlbumAggregateJob CreateAlbumAggregateJob(SubmitAlbumAggregateJobRequestDto request)
        => ApplyDownloadBehavior(new AlbumAggregateJob(ToAlbumQuery(request.AlbumQuery)), request.DownloadBehavior);

    public static JobList CreateJobList(SubmitJobListRequestDto request)
        => CreateJobList(request.Name, request.Jobs);

    public static void AssignWorkflowId(Job job, Guid workflowId)
    {
        job.WorkflowId = workflowId;

        switch (job)
        {
            case JobList list:
                foreach (var child in list.Jobs)
                    AssignWorkflowId(child, workflowId);
                break;
            case ExtractJob extract when extract.Result != null:
                AssignWorkflowId(extract.Result, workflowId);
                break;
            case AggregateJob aggregate:
                foreach (var song in aggregate.Songs)
                    AssignWorkflowId(song, workflowId);
                break;
            case AlbumAggregateJob aggregate:
                foreach (var album in aggregate.Albums)
                    AssignWorkflowId(album, workflowId);
                break;
        }
    }

    public static SongQuery ToSongQuery(SongQueryDto dto)
    {
        ValidateSearchText(dto.Artist, nameof(dto.Artist), allowEmpty: true);
        ValidateSearchText(dto.Title, nameof(dto.Title), allowEmpty: true);
        ValidateSearchText(dto.Album, nameof(dto.Album), allowEmpty: true);
        ValidateSearchText(dto.Uri, nameof(dto.Uri), maxLength: MaxSearchUriLength, allowEmpty: true);

        return new()
        {
            Artist = dto.Artist ?? "",
            Title = dto.Title ?? "",
            Album = dto.Album ?? "",
            URI = dto.Uri ?? "",
            Length = dto.Length ?? -1,
            ArtistMaybeWrong = dto.ArtistMaybeWrong,
        };
    }

    public static AlbumQuery ToAlbumQuery(AlbumQueryDto dto)
    {
        ValidateSearchText(dto.Artist, nameof(dto.Artist), allowEmpty: true);
        ValidateSearchText(dto.Album, nameof(dto.Album), allowEmpty: true);
        ValidateSearchText(dto.SearchHint, nameof(dto.SearchHint), allowEmpty: true);
        ValidateSearchText(dto.Uri, nameof(dto.Uri), maxLength: MaxSearchUriLength, allowEmpty: true);

        return new()
        {
            Artist = dto.Artist ?? "",
            Album = dto.Album ?? "",
            SearchHint = dto.SearchHint ?? "",
            URI = dto.Uri ?? "",
            ArtistMaybeWrong = dto.ArtistMaybeWrong,
        };
    }

    private static void ValidateSearchText(
        string? value,
        string fieldName,
        int maxLength = MaxSearchTextLength,
        bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (allowEmpty)
                return;
            throw new ArgumentException($"{fieldName} is required");
        }

        if (value.Length > maxLength)
            throw new ArgumentException($"{fieldName} is too long; maximum length is {maxLength} characters");
    }

    public static Job CreateJob(JobDraftDto item)
        => item switch
        {
            ExtractJobDraftDto extract => ApplyProvenance(CreateExtractJob(new SubmitExtractJobRequestDto(
                extract.Input,
                extract.InputType,
                ResultDownloadBehavior: extract.ResultDownloadBehavior,
                ArtifactId: extract.ArtifactId)), extract.Provenance),
            TrackSearchJobDraftDto search => ApplyProvenance(new SearchJob(ToSongQuery(search.SongQuery), search.IncludeFullResults), search.Provenance),
            AlbumSearchJobDraftDto search => ApplyProvenance(new SearchJob(ToAlbumQuery(search.AlbumQuery)), search.Provenance),
            SongJobDraftDto song => ApplyProvenance(ApplyDownloadBehavior(new SongJob(ToSongQuery(song.SongQuery)), song.DownloadBehavior), song.Provenance),
            AlbumJobDraftDto album => ApplyProvenance(ApplyDownloadBehavior(new AlbumJob(ToAlbumQuery(album.AlbumQuery)), album.DownloadBehavior), album.Provenance),
            AggregateJobDraftDto aggregate => ApplyProvenance(ApplyDownloadBehavior(new AggregateJob(ToSongQuery(aggregate.SongQuery)), aggregate.DownloadBehavior), aggregate.Provenance),
            AlbumAggregateJobDraftDto aggregate => ApplyProvenance(ApplyDownloadBehavior(new AlbumAggregateJob(ToAlbumQuery(aggregate.AlbumQuery)), aggregate.DownloadBehavior), aggregate.Provenance),
            JobListJobDraftDto list => ApplyProvenance(CreateJobList(list.Name, list.Jobs), list.Provenance),
            RemoteFileJobDraftDto remoteFile => ApplyProvenance(CreateRemoteFileJob(remoteFile), remoteFile.Provenance),
            RemoteDirectoryJobDraftDto remoteDirectory => ApplyProvenance(CreateRemoteDirectoryJob(remoteDirectory), remoteDirectory.Provenance),
            _ => throw new ArgumentException($"Unsupported job draft type '{item.GetType().Name}'")
        };

    public static DownloadSettingsPatchDto? DraftDownloadSettings(JobDraftDto draft)
        => draft switch
        {
            ExtractJobDraftDto typed => typed.DownloadSettings,
            TrackSearchJobDraftDto typed => typed.DownloadSettings,
            AlbumSearchJobDraftDto typed => typed.DownloadSettings,
            SongJobDraftDto typed => typed.DownloadSettings,
            AlbumJobDraftDto typed => typed.DownloadSettings,
            AggregateJobDraftDto typed => typed.DownloadSettings,
            AlbumAggregateJobDraftDto typed => typed.DownloadSettings,
            JobListJobDraftDto typed => typed.DownloadSettings,
            RemoteFileJobDraftDto typed => typed.DownloadSettings,
            RemoteDirectoryJobDraftDto typed => typed.DownloadSettings,
            _ => null,
        };

    public static void ApplyDraftDownloadSettings(
        JobList jobs,
        IReadOnlyList<JobDraftDto> drafts,
        Action<Job, DownloadSettingsPatchDto> apply)
    {
        for (int index = 0; index < jobs.Jobs.Count && index < drafts.Count; index++)
            ApplyDraftDownloadSettings(jobs.Jobs[index], drafts[index], apply);
    }

    public static void ApplyDraftDownloadSettings(
        Job job,
        JobDraftDto draft,
        Action<Job, DownloadSettingsPatchDto> apply)
    {
        if (DraftDownloadSettings(draft) is { } patch)
            apply(job, patch);
        if (job is JobList children && draft is JobListJobDraftDto childDraft)
            ApplyDraftDownloadSettings(children, childDraft.Jobs, apply);
    }

    private static RemoteFileJob CreateRemoteFileJob(RemoteFileJobDraftDto draft)
        => new(
            ToPeerFileTarget(draft.Target),
            draft.OutputPathComponents == null
                ? null
                : new RelativeOutputPath(draft.OutputPathComponents));

    private static RemoteDirectoryJob CreateRemoteDirectoryJob(RemoteDirectoryJobDraftDto draft)
    {
        bool hasDirectory = draft.Username != null || draft.FolderPath != null;
        if (hasDirectory == (draft.Plan != null))
            throw new ArgumentException("A remote directory draft requires exactly one peer-directory or resolved-plan source.");
        if (hasDirectory)
        {
            if (draft.Username == null || draft.FolderPath == null)
                throw new ArgumentException("Both username and folderPath are required for a peer-directory source.");
            return new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                new PeerDirectoryIdentity(draft.Username, draft.FolderPath)));
        }

        return new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            ToDirectoryTransferPlan(draft.Plan!)));
    }

    private static PeerFileTarget ToPeerFileTarget(PeerFileTargetDto target)
        => new(
            new PeerFileIdentity(target.Username, target.Filename),
            target.Size,
            target.Extension,
            target.BitRate,
            target.BitDepth,
            target.SampleRate,
            target.Length,
            target.Attributes?.Select(attribute =>
                new FileAttributeSnapshot(attribute.Type, attribute.Value)).ToList());

    private static DirectoryTransferPlan ToDirectoryTransferPlan(DirectoryTransferPlanDto plan)
    {
        var entries = new List<DirectoryTransferEntry>(plan.Entries.Count);
        foreach (DirectoryTransferEntryDto entry in plan.Entries)
        {
            if (entry?.Target is null || entry.RelativeDirectoryComponents is null)
                continue;
            try
            {
                entries.Add(new DirectoryTransferEntry(
                    ToPeerFileTarget(entry.Target),
                    entry.RelativeDirectoryComponents));
            }
            catch (ArgumentException)
            {
                // A malformed resolved entry cannot escape placement validation,
                // but it does not invalidate independently downloadable siblings.
            }
        }
        return new DirectoryTransferPlan(plan.DisplayRoot, entries);
    }

    private static TJob ApplyProvenance<TJob>(TJob job, JobProvenanceDto? provenance)
        where TJob : Job
    {
        if (provenance == null)
            return job;

        job.ItemNumber = provenance.ItemNumber;
        job.LineNumber = provenance.LineNumber;
        job.SourceMutation = ToSourceMutation(provenance.SourceMutation);
        return job;
    }

    private static SourceMutation? ToSourceMutation(SourceMutationDto? dto)
    {
        if (dto == null)
            return null;
        if (!Enum.TryParse<SourceMutationKind>(dto.Kind, ignoreCase: true, out var kind))
            throw new ArgumentException($"Unsupported source mutation kind '{dto.Kind}'");

        return new SourceMutation(kind, dto.Source, dto.LineNumber, dto.ItemNumber, dto.CsvColumnCount, dto.TrackUri);
    }

    public static DownloadBehaviorPolicy ToDownloadBehaviorPolicy(DownloadBehaviorPolicyDto dto) => new()
    {
        Default = dto.Default.ToCore(),
        Song = dto.Song?.ToCore(),
        Album = dto.Album?.ToCore(),
        Aggregate = dto.Aggregate?.ToCore(),
        AlbumAggregate = dto.AlbumAggregate?.ToCore(),
    };

    public static TJob ApplyDownloadBehavior<TJob>(TJob job, DownloadBehaviorPolicyDto? policy)
        where TJob : Job
    {
        if (policy != null)
            job.DownloadBehaviorPolicy = ToDownloadBehaviorPolicy(policy);
        return job;
    }


    public static List<AlbumFolder> ProjectAlbumJobFolders(AlbumJob albumJob, ConcurrentDictionary<string, int>? userSuccessCounts = null)
    {
        if (albumJob.Config == null || albumJob.Results.Count == 0)
            return albumJob.Results.ToList();

        // Search results are already projected/ranked by the engine; re-project only
        // after folder retrieval has changed the visible folder contents.
        if (albumJob.Results.All(folder => !folder.IsFullyRetrieved))
            return albumJob.Results.ToList();

        var rawResults = albumJob.Results
            .SelectMany(folder => folder.Files)
            .Select(file => file.Candidate)
            .Select(candidate => candidate.ToProjectionInput())
            .ToList();

        if (rawResults.Count == 0)
            return [];

        var projected = SearchResultProjector.AlbumFolders(
            rawResults,
            albumJob.Query,
            albumJob.Config.Search,
            userSuccessCounts);

        // Once a folder has been explicitly browsed, preserve the full browsed contents
        // in result projections. Re-projecting the raw files through the original search
        // conditions can hide the newly discovered files, which makes interactive `r`
        // appear to do nothing even though retrieval succeeded.
        foreach (var retrieved in albumJob.Results.Where(folder => folder.IsFullyRetrieved))
        {
            var fullFolder = new AlbumFolder(retrieved.Username, retrieved.FolderPath, retrieved.Files.ToList())
            {
                IsFullyRetrieved = true,
            };
            int index = projected.FindIndex(folder =>
                string.Equals(folder.Username, retrieved.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, retrieved.FolderPath, StringComparison.Ordinal));
            if (index >= 0)
                projected[index] = fullFolder;
            else
                projected.Add(fullFolder);
        }

        return projected;
    }

    public static AlbumFolder? FindProjectedAlbumFolder(AlbumJob albumJob, AlbumFolderRefDto folderRef, ConcurrentDictionary<string, int>? userSuccessCounts = null)
        => ProjectAlbumJobFolders(albumJob, userSuccessCounts)
            .FirstOrDefault(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));

    public static AlbumFolder? BuildRelatedFolder(AlbumFolderRefDto folderRef, IEnumerable<AlbumFolder> knownFolders)
    {
        var seedFiles = knownFolders
            .Where(folder => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
                && PathsAreRelated(folder.FolderPath, folderRef.FolderPath))
            .SelectMany(folder => folder.Files)
            .Where(file => file.Filename.StartsWith(folderRef.FolderPath + "\\", StringComparison.Ordinal))
            .ToList();

        return seedFiles.Count == 0
            ? null
            : new AlbumFolder(folderRef.Username, folderRef.FolderPath, seedFiles);
    }

    public static AlbumFolder? FindAlbumFolderForRetrieval(
        Job sourceJob,
        AlbumFolderRefDto folderRef,
        ConcurrentDictionary<string, int> userSuccessCounts,
        AlbumQueryDto? albumQuery = null)
    {
        if (sourceJob is AlbumJob albumJob)
        {
            return albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? FindAlbumFolder(sourceJob, folderRef, userSuccessCounts, albumQuery);
        }

        return FindAlbumFolder(sourceJob, folderRef, userSuccessCounts, albumQuery);
    }

    public static AlbumFolder? FindAlbumFolder(
        Job sourceJob,
        AlbumFolderRefDto folderRef,
        ConcurrentDictionary<string, int> userSuccessCounts,
        AlbumQueryDto? albumQuery = null)
    {
        if (sourceJob is SearchJob searchJob)
        {
            if (searchJob.Config == null)
                return null;

            FolderSearchProjection? projection = albumQuery != null
                ? new FolderSearchProjection(ToAlbumQuery(albumQuery))
                : searchJob.DefaultFolderProjection;
            if (projection == null)
                return null;

            IReadOnlyList<AlbumFolder> folders = searchJob
                .GetAlbumFolders(projection, searchJob.Config.Search)
                .Items;
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, folders);
        }

        if (sourceJob is AlbumJob albumJob)
        {
            return FindProjectedAlbumFolder(albumJob, folderRef, userSuccessCounts)
                ?? albumJob.Results.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, albumJob.Results);
        }

        if (sourceJob is AlbumAggregateJob aggregateJob)
        {
            AlbumQuery? query = albumQuery == null ? null : ToAlbumQuery(albumQuery);
            List<AlbumFolder> folders = aggregateJob.Albums
                .Where(album => query == null || AlbumQueriesEqual(album.Query, query))
                .SelectMany(album => album.Results)
                .ToList();
            return folders.FirstOrDefault(folder => Matches(folder, folderRef))
                ?? BuildRelatedFolder(folderRef, folders);
        }

        return null;
    }

    public static FileCandidate? FindFileCandidate(
        Job sourceJob,
        FileCandidateRefDto candidateRef,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        if (sourceJob is SearchJob searchJob)
            return FindSearchFileCandidate(searchJob, candidateRef, userSuccessCounts);

        IEnumerable<FileCandidate> candidates = sourceJob switch
        {
            SongJob song => song.Candidates ?? [],
            AggregateJob aggregate => aggregate.Songs
                .SelectMany(song => song.Candidates ?? []),
            AlbumJob album => album.Results
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate),
            AlbumAggregateJob aggregate => aggregate.Albums
                .SelectMany(album => album.Results)
                .SelectMany(folder => folder.Files)
                .Select(file => file.Candidate),
            _ => [],
        };
        return candidates.FirstOrDefault(candidate => Matches(candidate, candidateRef));
    }

    public static Job CreateFileSelectionFollowUp(
        Job sourceJob,
        FileCandidate candidate,
        ExtractionMode? requestedMode)
    {
        Job followUp = requestedMode == ExtractionMode.General
            ? new RemoteFileJob(candidate.Target)
            : new SongJob(new SongQuery(sourceJob switch
            {
                SearchJob search => search.DefaultFileProjection?.Query
                    ?? Searcher.InferSongQuery(
                        candidate.Filename,
                        new SongQuery { Title = search.QueryText }),
                SongJob song => song.Query,
                AggregateJob aggregate => aggregate.Songs
                    .FirstOrDefault(song => song.Candidates?.Contains(candidate) == true)?.Query
                    ?? Searcher.InferSongQuery(candidate.Filename, aggregate.Query),
                _ => Searcher.InferSongQuery(
                    candidate.Filename,
                    sourceJob.QueryTrack ?? new SongQuery()),
            }))
            {
                ResolvedTarget = candidate,
            };
        followUp.ItemName = sourceJob.ItemName;
        followUp.WorkflowId = sourceJob.WorkflowId;
        return followUp;
    }

    public static void PropagateSourceMutationToFollowUp(Job sourceJob, Job followUpJob)
    {
        // Aggregate-album children already retain the source mutation that owns
        // the shared aggregate input; copying it to a separate follow-up would
        // let that follow-up retire the same source twice.
        if (sourceJob is not AlbumAggregateJob)
            followUpJob.CopySourceMutationFrom(sourceJob);
    }

    public static void ApplyManualSongSelection(
        SongJob song,
        FileCandidate candidate)
    {
        song.ResolvedTarget = candidate;
        song.Candidates ??= [candidate];
        if (!song.Candidates.Contains(candidate))
            song.Candidates.Insert(0, candidate);
        song.ResetToPending();
        song.EnsureDisplayId();
    }

    public static AlbumQuery? ResolveFolderSelectionQuery(
        Job sourceJob,
        AlbumQueryDto? query)
        => query != null
            ? ToAlbumQuery(query)
            : sourceJob switch
            {
                SearchJob search => search.DefaultFolderProjection?.Query,
                AlbumJob album => album.Query,
                AlbumAggregateJob aggregate => aggregate.Query,
                _ => null,
            };

    public static AlbumJob CreateAlbumSelectionFollowUp(
        Job sourceJob,
        AlbumFolder folder,
        AlbumQuery query,
        AlbumFolderDownloadSelectionDto? selection)
    {
        string? itemName = sourceJob is SearchJob
            {
                DefaultAggregateAlbumProjection: not null,
            } && !string.IsNullOrWhiteSpace(folder.FolderPath)
                ? Utils.GetBaseNameSlsk(folder.FolderPath)
                : sourceJob.ItemName;
        var followUp = new AlbumJob(new AlbumQuery(query))
        {
            ResolvedTarget = folder,
            ItemName = itemName,
            WorkflowId = sourceJob.WorkflowId,
            DownloadBehaviorPolicy = new DownloadBehaviorPolicy(),
        };
        ApplyFolderDownloadSelection(followUp, selection);
        return followUp;
    }

    private static FileCandidate? FindSearchFileCandidate(
        SearchJob searchJob,
        FileCandidateRefDto candidateRef,
        ConcurrentDictionary<string, int> userSuccessCounts)
    {
        if (searchJob.Config == null)
            return null;

        FileCandidate? candidate = searchJob
            .GetSortedTrackCandidates(searchJob.Config.Search, userSuccessCounts)
            .Items
            .FirstOrDefault(item => Matches(item, candidateRef));
        if (candidate != null || searchJob.DefaultFolderProjection == null)
        {
            return candidate
                ?? searchJob.RawSnapshot()
                    .Select(result => result.ProjectionInput.ToFileCandidate())
                    .FirstOrDefault(item => Matches(item, candidateRef));
        }

        return searchJob.GetAlbumFolders(searchJob.Config.Search)
            .Items
            .SelectMany(folder => folder.Files)
            .Select(file => file.Candidate)
            .FirstOrDefault(item => Matches(item, candidateRef));
    }

    private static bool Matches(AlbumFolder folder, AlbumFolderRefDto folderRef)
        => string.Equals(folder.Username, folderRef.Username, StringComparison.Ordinal)
            && string.Equals(folder.FolderPath, folderRef.FolderPath, StringComparison.Ordinal);

    private static bool Matches(FileCandidate candidate, FileCandidateRefDto candidateRef)
        => string.Equals(candidate.Username, candidateRef.Username, StringComparison.Ordinal)
            && string.Equals(candidate.Filename, candidateRef.Filename, StringComparison.Ordinal);

    private static bool AlbumQueriesEqual(AlbumQuery left, AlbumQuery right)
        => string.Equals(left.Artist, right.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Album, right.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.SearchHint, right.SearchHint, StringComparison.OrdinalIgnoreCase);

    private static bool PathsAreRelated(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsInFolderPath(string filename, string folderPath)
        => filename.StartsWith(folderPath.TrimEnd('\\') + "\\", StringComparison.Ordinal)
            || filename.Equals(folderPath.TrimEnd('\\'), StringComparison.Ordinal);

    public static AlbumFolder ApplyFolderDownloadSelection(AlbumFolder folder, AlbumFolderDownloadSelectionDto? selection)
    {
        if (selection?.ExactFiles == true && selection.Files is not { Count: > 0 })
            throw new ArgumentException("Exact folder downloads require at least one selected file.");

        if (selection?.Files is not { Count: > 0 } selectedFiles)
            return folder;

        var selected = selectedFiles
            .Select(file => (file.Username, file.Filename))
            .ToHashSet();

        var files = folder.Files
            .Where(file => selected.Contains((file.Candidate.Username, file.Candidate.Filename)))
            .ToList();

        if (files.Count != selected.Count)
            throw new ArgumentException("One or more selected files were not found in the requested folder.");

        return new AlbumFolder(folder.Username, folder.FolderPath, files)
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };
    }

    public static void ApplyFolderDownloadSelection(AlbumJob job, AlbumFolderDownloadSelectionDto? selection)
    {
        job.DirectoryResolutionPolicy = selection?.ExactFiles == true
            ? AlbumDirectoryResolutionPolicy.UseSelectedSnapshot
            : AlbumDirectoryResolutionPolicy.CompleteIfNeeded;
        job.ValidationRequirement = selection?.SkipTrackCountVerification == true
            ? AlbumValidationRequirement.UserAccepted
            : AlbumValidationRequirement.Standard;
    }

    public static RemoteDirectoryJob CreateRemoteDirectoryDownload(
        AlbumFolder folder,
        AlbumFolderDownloadSelectionDto? selection)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.IsFullyRetrieved && selection?.ExactFiles != true)
        {
            return new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                folder.DirectoryIdentity));
        }

        var selectedSnapshot = new PeerDirectorySnapshot(
            folder.DirectoryIdentity,
            folder.Files.Select(file => file.Candidate.Target).ToArray(),
            isComplete: true);
        return new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            DirectoryTransferPlanner.FromSnapshot(selectedSnapshot)));
    }

    private static JobList CreateJobList(string? name, IReadOnlyList<JobDraftDto> jobs)
    {
        if (jobs.Count == 0)
            throw new ArgumentException("job-list must contain at least one child job");

        return new JobList(name, jobs.Select(CreateJob));
    }
}
