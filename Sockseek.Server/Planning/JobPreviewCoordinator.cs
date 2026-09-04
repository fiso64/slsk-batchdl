using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

public sealed class JobPreviewUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class JobPreviewCoordinator(
    IOptions<ServerOptions> options,
    EngineSupervisor supervisor,
    SubmissionCommitCoordinator commits,
    JobPreviewCursorCodec cursors,
    ILogger<JobPreviewCoordinator> logger,
    InputArtifactCoordinator? artifacts = null) : IHostedService, IAsyncDisposable
{
    private readonly ServerOptions serverOptions = options.Value;
    private JobPreviewStore? store;
    private FileStream? spoolLease;
    private string? spoolDirectory;
    private Exception? initializationFailure;
    private readonly Channel<Guid> planningQueue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private Task? maintenance;
    internal event Action<Guid>? PreviewCompleted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string dataDirectory = SockseekDataPaths.ResolveDataDirectory(
            serverOptions.Persistence.DataDirectory);
        string spoolRoot = Path.Combine(dataDirectory, "planning", "job-preview-spools");
        string candidateDirectory = Path.Combine(spoolRoot, Guid.NewGuid().ToString("N"));
        FileStream? candidateLease = null;
        JobPreviewStore? candidate = null;
        try
        {
            CleanupInactiveSpools(spoolRoot);
            Directory.CreateDirectory(candidateDirectory);
            candidate = new JobPreviewStore(Path.Combine(candidateDirectory, "preview.db"));
            candidateLease = new FileStream(
                Path.Combine(candidateDirectory, ".active"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            await candidate.InitializeAsync(cancellationToken).ConfigureAwait(false);
            store = candidate;
            spoolLease = candidateLease;
            spoolDirectory = candidateDirectory;
            await RunMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
            worker = RunWorkerAsync(lifetime.Token);
            maintenance = RunMaintenanceAsync(lifetime.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            initializationFailure = exception;
            store = null;
            spoolLease = null;
            spoolDirectory = null;
            if (candidate != null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            candidateLease?.Dispose();
            DeleteSpoolDirectory(candidateDirectory);
            ServerLogMessages.JobPreviewUnavailable(logger, exception);
        }
        catch
        {
            store = null;
            spoolLease = null;
            spoolDirectory = null;
            if (candidate != null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            candidateLease?.Dispose();
            DeleteSpoolDirectory(candidateDirectory);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        planningQueue.Writer.TryComplete();
        lifetime.Cancel();
        if (worker != null || maintenance != null)
        {
            try
            {
                await Task.WhenAll(
                        worker ?? Task.CompletedTask,
                        maintenance ?? Task.CompletedTask)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await RunMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewMaintenanceDegraded(logger, exception);
            }
        }
    }

    private async Task RunMaintenanceOnceAsync(CancellationToken cancellationToken)
    {
        JobPreviewStore repository = RequiredStore();
        foreach (Guid previewId in await repository.ExpireDueAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            ServerLogMessages.JobPreviewExpired(logger, previewId);
        }
        IReadOnlyList<StoredJobPreviewCleanup> cleanup = await repository
            .PruneTombstonesAsync(
                TimeSpan.FromDays(1),
                TimeSpan.FromDays(30),
                cancellationToken).ConfigureAwait(false);
        if (artifacts == null)
            return;
        foreach (StoredJobPreviewCleanup row in cleanup)
            await ReleaseArtifactPinsAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreateJobPreviewResponseDto> CreateAsync(
        CreateJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JobPreviewStore repository = RequiredStore();
        if (ContainsInlineCredentials(request))
        {
            throw new ArgumentException(
                "Job Preview does not persist request credential values. Configure credential slots on the daemon and submit the preview without inline credentials.");
        }
        CreateJobPreviewRequestDto durableRequest = WithoutCredentials(request);
        IReadOnlyDictionary<string, InputArtifactLease> artifactLeases =
            await ResolveArtifactLeasesAsync(durableRequest.Job, cancellationToken)
                .ConfigureAwait(false);
        string requestJson = JsonSerializer.Serialize(
            durableRequest,
            SockseekApiJson.CreateSerializerOptions());
        StoredJobPreview preview = await repository.CreateAsync(
            requestJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (string artifactId in artifactLeases.Keys)
        {
            if (!await artifacts!.PinAsync(
                    artifactId,
                    "preview",
                    preview.Id,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Input artifact '{artifactId}' expired before the preview could retain it.");
            }
        }
        // The temporary planning record is now owned by this daemon session.
        // Request cancellation must not strand it between the spool and queue.
        await planningQueue.Writer.WriteAsync(preview.Id, CancellationToken.None)
            .ConfigureAwait(false);
        ServerLogMessages.JobPreviewCreated(logger, preview.Id);
        return new(ToDto(preview));
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (Guid previewId in planningQueue.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                StoredJobPreviewWork? work = await RequiredStore()
                    .GetPlanningWorkAsync(previewId, cancellationToken).ConfigureAwait(false);
                if (work == null)
                    continue;
                CreateJobPreviewRequestDto request = JsonSerializer.Deserialize<CreateJobPreviewRequestDto>(
                    work.RequestJson,
                    SockseekApiJson.CreateSerializerOptions())
                    ?? throw new InvalidDataException("A durable job-preview request is empty.");
                await PlanAsync(previewId, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewWorkLoadFailed(logger, exception, previewId);
            }
        }
    }

    private async Task PlanAsync(
        Guid previewId,
        CreateJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        JobPreviewStore repository = RequiredStore();
        StoredJobPreview preview = await repository.GetPreviewAsync(previewId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The job preview was not found.");
        var started = Stopwatch.StartNew();
        var batch = new List<StoredJobPreviewNode>(100);
        try
        {
            ResolvedArtifactPlan resolvedArtifacts = await ResolveArtifactsAsync(
                request.Job,
                cancellationToken).ConfigureAwait(false);
            await foreach (PlannedJobNode node in supervisor.PlanDraftAsync(
                resolvedArtifacts.Draft,
                request.Options,
                resolvedArtifacts.Revisions,
                cancellationToken).ConfigureAwait(false))
            {
                batch.Add(ToStoredNode(previewId, node));
                if (batch.Count >= 100)
                {
                    await repository.AppendNodesAsync(previewId, batch, cancellationToken)
                        .ConfigureAwait(false);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await repository.AppendNodesAsync(previewId, batch, cancellationToken)
                    .ConfigureAwait(false);
            preview = await repository.CompleteAsync(previewId, cancellationToken)
                .ConfigureAwait(false);
            ServerLogMessages.JobPreviewCompleted(
                logger,
                preview.Id,
                preview.State,
                started.ElapsedMilliseconds,
                preview.NodeCount,
                preview.ReadyNodeCount,
                preview.FailedNodeCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                if (batch.Count > 0)
                    await repository.AppendNodesAsync(previewId, batch, CancellationToken.None)
                        .ConfigureAwait(false);
                preview = await repository.CompleteAsync(previewId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The owner logs the original terminal failure once. Store
                // diagnostics remain available if the final state write worked.
            }
            ServerLogMessages.JobPreviewFailed(
                logger,
                exception,
                preview.Id,
                started.ElapsedMilliseconds,
                preview.NodeCount);
        }
        finally
        {
            InvokePreviewCompleted(previewId);
        }
    }

    private void InvokePreviewCompleted(Guid previewId)
    {
        if (PreviewCompleted == null)
            return;
        foreach (Action<Guid> observer in PreviewCompleted.GetInvocationList())
        {
            try
            {
                observer(previewId);
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewObserverFailed(logger, exception, previewId);
            }
        }
    }

    public async Task<JobPreviewSummaryDto?> GetAsync(
        Guid previewId,
        CancellationToken cancellationToken)
    {
        StoredJobPreview? preview = await RequiredStore()
            .GetPreviewAsync(previewId, cancellationToken).ConfigureAwait(false);
        return preview == null ? null : ToDto(preview);
    }

    public async Task<CursorPage<JobPreviewNodeDto>?> GetNodesAsync(
        Guid previewId,
        string? parentRef,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > JobPreviewStore.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        JobPreviewStore repository = RequiredStore();
        if (await repository.GetPreviewAsync(previewId, cancellationToken).ConfigureAwait(false) == null)
            return null;
        long after = cursor == null ? -1 : cursors.Decode(cursor, previewId, parentRef);
        IReadOnlyList<StoredJobPreviewNode> rows = await repository.GetNodesAsync(
            previewId,
            parentRef,
            after,
            limit,
            cancellationToken).ConfigureAwait(false);
        string? next = rows.Count == limit
            ? cursors.Encode(previewId, parentRef, rows[^1].Ordinal)
            : null;
        return new CursorPage<JobPreviewNodeDto>(rows.Select(ToDto).ToArray(), next);
    }

    public async Task<CommitJobPreviewResponseDto?> CommitAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        ValidateCommitRequest(request);
        string fingerprint = SubmissionCommitCoordinator.Fingerprint(
            "job-preview",
            previewId,
            request.Revision,
            request.Selection);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                CommitJobPreviewResponseDto receipt = await CommitCoreAsync(
                    previewId,
                    request,
                    fingerprint,
                    operationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The job preview was not found.");
                return new SubmissionCommitExecution<CommitJobPreviewResponseDto>(
                    receipt,
                    receipt.SubmissionId);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommitJobPreviewResponseDto?> CommitCoreAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        JobPreviewStore repository = RequiredStore();
        StoredPreviewCommit? resolved = await repository.ResolveCommitAsync(
            previewId,
            request.Revision,
            request.Selection.Mode.ToString(),
            refs,
            cancellationToken).ConfigureAwait(false);
        if (resolved == null)
            return null;

        int failedCount = resolved.Mode == RefSelectionMode.AllExcept.ToString()
            ? resolved.FailedNodeCount
            : 0;
        int rejectedBeforeSubmission = checked(failedCount + resolved.MissingRequestedRefCount);
        int requestedCount = checked(resolved.SelectedNodes.Count + rejectedBeforeSubmission);
        if (resolved.SelectedNodes.Count == 0)
        {
            IReadOnlyList<SubmissionReasonCountDto> reasons = RejectionReasons(
                failedCount,
                resolved.MissingRequestedRefCount,
                includeEmpty: rejectedBeforeSubmission == 0);
            return new(
                previewId,
                null,
                null,
                requestedCount,
                0,
                0,
                0,
                Math.Max(1, rejectedBeforeSubmission),
                reasons);
        }

        var specifications = resolved.SelectedNodes.Select(HydrateSpecification)
            .ToArray();
        var materialized = specifications
            .Select(specification => specification.MaterializeJob(supervisor.DefaultDownloadSettings))
            .ToArray();
        Job root;
        DownloadSettings rootSettings;
        SubmissionSpecification acceptedSpecification;
        if (materialized.Length == 1)
        {
            root = materialized[0];
            rootSettings = specifications[0].MaterializeSettings(supervisor.DefaultDownloadSettings);
            acceptedSpecification = specifications[0];
        }
        else
        {
            root = new JobList("Reviewed job preview", materialized);
            rootSettings = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic);
            root.PlannedEffectiveSettings = SettingsCloner.Clone(rootSettings);
            acceptedSpecification = SubmissionSpecification.Create(root, rootSettings);
        }
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);

        Guid submissionId = request.IdempotencyKey;
        if (!await repository.TryBeginCommitAsync(previewId, submissionId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("The job preview is already committed or no longer available.");
        }

        var started = Stopwatch.StartNew();
        JobSummaryDto summary;
        try
        {
            summary = await supervisor.QueuePlannedSubmissionAsync(
                root,
                rootSettings,
                acceptedSpecification,
                submissionId,
                previewId,
                commitFingerprint,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await repository.ReleaseCommitAsync(previewId, submissionId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        bool published = await repository.MarkCommittedAsync(
            previewId,
            submissionId,
            CancellationToken.None).ConfigureAwait(false);
        if (!published)
        {
            ServerLogMessages.JobPreviewCommitPublicationFailed(
                logger,
                previewId,
                submissionId);
        }
        else
        {
            StoredJobPreviewCleanup? released = await repository.DeleteCommittedAsync(
                previewId,
                submissionId,
                CancellationToken.None).ConfigureAwait(false);
            if (released != null)
                await ReleaseArtifactPinsAsync(released, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        ServerLogMessages.JobPreviewCommitted(
            logger,
            previewId,
            submissionId,
            started.ElapsedMilliseconds,
            requestedCount,
            materialized.Length,
            rejectedBeforeSubmission);
        return new(
            previewId,
            submissionId,
            summary.WorkflowId,
            requestedCount,
            materialized.Length,
            materialized.Length,
            0,
            rejectedBeforeSubmission,
            RejectionReasons(failedCount, resolved.MissingRequestedRefCount));
    }

    private static void ValidateCommitRequest(CommitJobPreviewRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.Selection.Refs);
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Preview revision cannot be negative.");
        if (request.Selection.Refs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selection refs cannot be empty.");
    }

    private static IReadOnlyList<SubmissionReasonCountDto> RejectionReasons(
        int failedCount,
        int missingCount,
        bool includeEmpty = false)
    {
        var reasons = new List<SubmissionReasonCountDto>(2);
        if (failedCount > 0)
            reasons.Add(new("planning-failed", failedCount));
        if (missingCount > 0)
            reasons.Add(new("missing-ref", missingCount));
        if (includeEmpty)
            reasons.Add(new("empty-selection", 1));
        return reasons;
    }

    private JobPreviewStore RequiredStore()
        => store ?? throw new JobPreviewUnavailableException(
            "Job Preview is unavailable; direct Start remains available.",
            initializationFailure);

    private static StoredJobPreviewNode ToStoredNode(Guid previewId, PlannedJobNode node)
    {
        bool ready = node.State == PlannedJobState.Ready && node.EffectiveSettings != null;
        bool selectable = ready && node.RuntimeJob is not (ExtractJob or JobList);
        SubmissionSpecification? completeSpecification = selectable
            ? SubmissionSpecification.Create(
                node.RuntimeJob,
                node.EffectiveSettings!,
                node.SourceRevision)
            : null;
        string? bindingsJson = completeSpecification == null
            ? null
            : JsonSerializer.Serialize(completeSpecification.CredentialBindings);
        string? settingsRef = completeSpecification == null
            ? null
            : EffectiveSettingsRef(
                completeSpecification.EffectiveSettingsJson,
                bindingsJson!);
        string? specification = completeSpecification == null
            ? null
            : SubmissionSpecificationCodec.Serialize(completeSpecification with
            {
                Command = completeSpecification.Command with
                {
                    PlannedEffectiveSettingsJson = null,
                    PlannedCredentialBindings = null,
                },
                EffectiveSettingsJson = "{}",
                CredentialBindings = [],
            });
        return new(
            previewId,
            0,
            node.Ref,
            node.ParentRef,
            node.Role.ToString(),
            node.State.ToString(),
            selectable,
            ServerSnapshotMapper.ToServerJobKind(node.RuntimeJob).ToString(),
            node.RuntimeJob.ItemName,
            node.RuntimeJob is SearchJob search ? search.QueryText : null,
            node.DirectChildCount,
            JsonSerializer.Serialize(
                node.EffectiveSettings?.AppliedAutoProfiles
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray() ?? []),
            specification,
            settingsRef,
            completeSpecification?.EffectiveSettingsJson,
            bindingsJson,
            node.FailureCode,
            node.FailureMessage);
    }

    private static SubmissionSpecification HydrateSpecification(StoredJobPreviewNode node)
    {
        if (node.SpecificationJson == null
            || node.EffectiveSettingsJson == null
            || node.CredentialBindingsJson == null)
        {
            throw new InvalidDataException(
                "A selected preview node has no retained specification or effective settings.");
        }
        SubmissionSpecification skeleton = SubmissionSpecificationCodec.Deserialize(
            node.SpecificationJson);
        string[] bindings = JsonSerializer.Deserialize<string[]>(
                node.CredentialBindingsJson)
            ?? throw new InvalidDataException(
                "A selected preview node has invalid credential bindings.");
        var hydrated = skeleton with
        {
            EffectiveSettingsJson = node.EffectiveSettingsJson,
            CredentialBindings = bindings,
        };
        hydrated.Validate();
        return hydrated;
    }

    private static string EffectiveSettingsRef(string settingsJson, string bindingsJson)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            settingsJson + "\0" + bindingsJson));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static JobPreviewSummaryDto ToDto(StoredJobPreview preview)
        => new(
            preview.Id,
            preview.CreatedAtUtc,
            preview.ExpiresAtUtc,
            Enum.Parse<JobPreviewState>(preview.State),
            preview.Revision,
            preview.NodeCount,
            preview.ReadyNodeCount,
            preview.FailedNodeCount,
            preview.SelectableNodeCount,
            preview.CommittedSubmissionId);

    private static JobPreviewNodeDto ToDto(StoredJobPreviewNode node)
    {
        NormalizedJobCommand? command = node.SpecificationJson == null
            ? null
            : SubmissionSpecificationCodec.Deserialize(node.SpecificationJson).Command;
        return new(
            node.Ref,
            node.ParentRef,
            Enum.Parse<ServerJobRole>(node.Role),
            node.State == PlannedJobState.Ready.ToString(),
            node.IsSelectable,
            Enum.Parse<ServerJobKind>(node.Kind),
            node.ItemName,
            node.QueryText,
            node.DirectChildCount,
            JsonSerializer.Deserialize<string[]>(node.AppliedAutoProfilesJson) ?? [],
            command?.SongQuery is { } songQuery
                ? ServerSnapshotMapper.ToSongQueryDto(songQuery)
                : null,
            command?.AlbumQuery is { } albumQuery
                ? ServerSnapshotMapper.ToAlbumQueryDto(albumQuery)
                : null,
            node.FailureCode,
            node.FailureMessage);
    }

    private static CreateJobPreviewRequestDto WithoutCredentials(
        CreateJobPreviewRequestDto request)
        => request with
        {
            Job = WithoutCredentials(request.Job),
            Options = request.Options == null
                ? null
                : request.Options with
                {
                    DownloadSettings = WithoutCredentials(
                        request.Options.DownloadSettings),
                },
        };

    private static bool ContainsInlineCredentials(CreateJobPreviewRequestDto request)
        => HasCredentials(request.Options?.DownloadSettings)
            || Drafts(request.Job).Any(draft =>
                HasCredentials(JobRequestMapper.DraftDownloadSettings(draft)));

    private static IEnumerable<JobDraftDto> Drafts(JobDraftDto root)
    {
        yield return root;
        if (root is JobListJobDraftDto list)
        {
            foreach (JobDraftDto child in list.Jobs)
            foreach (JobDraftDto descendant in Drafts(child))
                yield return descendant;
        }
    }

    private static bool HasCredentials(DownloadSettingsPatchDto? patch)
        => patch?.Spotify is
            {
                ClientId: not null,
            }
            or { ClientSecret: not null }
            or { Token: not null }
            or { Refresh: not null }
            || patch?.YouTube?.ApiKey != null;

    private static JobDraftDto WithoutCredentials(JobDraftDto draft)
        => draft switch
        {
            ExtractJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            TrackSearchJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumSearchJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            SongJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AggregateJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumAggregateJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            JobListJobDraftDto value => value with
            {
                Jobs = value.Jobs.Select(WithoutCredentials).ToArray(),
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            RemoteFileJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            RemoteDirectoryJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            _ => throw new ArgumentException(
                $"Unsupported job draft type '{draft.GetType().Name}'."),
        };

    private static DownloadSettingsPatchDto? WithoutCredentials(
        DownloadSettingsPatchDto? patch)
        => patch == null
            ? null
            : patch with
            {
                Spotify = null,
                YouTube = patch.YouTube == null
                    ? null
                    : patch.YouTube with { ApiKey = null },
            };

    private async Task<IReadOnlyDictionary<string, InputArtifactLease>> ResolveArtifactLeasesAsync(
        JobDraftDto draft,
        CancellationToken cancellationToken)
    {
        string[] ids = ArtifactIds(draft)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, InputArtifactLease>(StringComparer.Ordinal);
        if (artifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");
        var leases = new Dictionary<string, InputArtifactLease>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            InputArtifactLease? lease = await artifacts.ResolveAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (lease == null)
                throw new ArgumentException(
                    $"Input artifact '{id}' was not found or has expired.");
            leases.Add(id, lease);
        }
        return leases;
    }

    private async Task<ResolvedArtifactPlan> ResolveArtifactsAsync(
        JobDraftDto draft,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, InputArtifactLease> leases =
            await ResolveArtifactLeasesAsync(draft, cancellationToken).ConfigureAwait(false);
        if (leases.Count == 0)
            return new(draft, new Dictionary<string, SubmissionSourceRevision>(StringComparer.Ordinal));
        var revisions = leases.ToDictionary(
            pair => pair.Key,
            pair => InputArtifactCoordinator.Revision(pair.Value),
            StringComparer.Ordinal);
        return new(ResolveArtifactDraft(draft, leases), revisions);
    }

    private static JobDraftDto ResolveArtifactDraft(
        JobDraftDto draft,
        IReadOnlyDictionary<string, InputArtifactLease> leases)
        => draft switch
        {
            ExtractJobDraftDto extract when extract.ArtifactId is { } id =>
                ResolveArtifactExtract(extract, leases[id]),
            JobListJobDraftDto list => list with
            {
                Jobs = list.Jobs.Select(child => ResolveArtifactDraft(child, leases)).ToArray(),
            },
            _ => draft,
        };

    private static ExtractJobDraftDto ResolveArtifactExtract(
        ExtractJobDraftDto draft,
        InputArtifactLease lease)
    {
        var immutablePatch = new DownloadSettingsPatchDto(
            Extraction: new ExtractionSettingsPatchDto(RemoveTracksFromSource: false));
        string? inputType = draft.InputType;
        if (string.IsNullOrWhiteSpace(inputType))
        {
            inputType = string.Equals(
                Path.GetExtension(lease.Artifact.OriginalName),
                ".csv",
                StringComparison.OrdinalIgnoreCase)
                ? InputType.CSV.ToString()
                : InputType.List.ToString();
        }
        return draft with
        {
            Input = lease.Path,
            InputType = inputType,
            DownloadSettings = DownloadSettingsPatchDtoMapper.Combine(
                draft.DownloadSettings,
                immutablePatch),
            Provenance = draft.Provenance == null
                ? null
                : draft.Provenance with { SourceMutation = null },
        };
    }

    private static IEnumerable<string> ArtifactIds(JobDraftDto draft)
        => Drafts(draft)
            .OfType<ExtractJobDraftDto>()
            .Select(extract => extract.ArtifactId)
            .OfType<string>();

    private sealed record ResolvedArtifactPlan(
        JobDraftDto Draft,
        IReadOnlyDictionary<string, SubmissionSourceRevision> Revisions);

    private async Task ReleaseArtifactPinsAsync(
        StoredJobPreviewCleanup cleanup,
        CancellationToken cancellationToken)
    {
        if (artifacts == null)
            return;
        try
        {
            CreateJobPreviewRequestDto? request =
                JsonSerializer.Deserialize<CreateJobPreviewRequestDto>(
                    cleanup.RequestJson,
                    SockseekApiJson.CreateSerializerOptions());
            if (request == null)
                return;
            foreach (string artifactId in ArtifactIds(request.Job)
                .Distinct(StringComparer.Ordinal))
            {
                await artifacts.UnpinAsync(
                    artifactId,
                    "preview",
                    cleanup.PreviewId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ServerLogMessages.JobPreviewArtifactCleanupDegraded(
                logger,
                exception,
                cleanup.PreviewId);
        }
    }

    private static void CleanupInactiveSpools(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string leasePath = Path.Combine(directory, ".active");
            try
            {
                using (new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                }
                DeleteSpoolDirectory(directory);
            }
            catch (IOException)
            {
                // Another daemon session still owns this spool.
            }
            catch (UnauthorizedAccessException)
            {
                // Initialization will report a clear feature-level failure if
                // this also prevents creation of the new spool.
            }
        }
    }

    private static void DeleteSpoolDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        if (store != null)
            await store.DisposeAsync().ConfigureAwait(false);
        store = null;
        spoolLease?.Dispose();
        spoolLease = null;
        if (spoolDirectory != null)
            DeleteSpoolDirectory(spoolDirectory);
        spoolDirectory = null;
    }
}
