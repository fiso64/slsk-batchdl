using Sldl.Server;

namespace Sldl.Cli;

internal interface ICliBackend
{
    event Action<ServerEventEnvelopeDto>? EventReceived;

    Task<JobSummaryDto> SubmitJobAsync(SubmitJobRequestDto request, CancellationToken ct = default);
    Task<IReadOnlyList<JobSummaryDto>> GetJobsAsync(JobQuery query, CancellationToken ct = default);
    Task<JobDetailDto?> GetJobDetailAsync(Guid jobId, CancellationToken ct = default);
    Task<WorkflowDetailDto?> GetWorkflowAsync(Guid workflowId, CancellationToken ct = default);
    Task<SearchProjectionSnapshotDto<FileCandidateDto>?> GetTrackProjectionAsync(Guid jobId, CancellationToken ct = default);
    Task<SearchProjectionSnapshotDto<AlbumFolderDto>?> GetAlbumProjectionAsync(Guid jobId, bool includeFiles, CancellationToken ct = default);
    Task<SearchProjectionSnapshotDto<AggregateTrackCandidateDto>?> GetAggregateTrackProjectionAsync(Guid jobId, CancellationToken ct = default);
    Task<SearchProjectionSnapshotDto<AggregateAlbumCandidateDto>?> GetAggregateAlbumProjectionAsync(Guid jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobSummaryDto>?> StartExtractedResultAsync(Guid extractJobId, StartExtractedResultRequestDto request, CancellationToken ct = default);
    Task<JobSummaryDto?> StartRetrieveFolderAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default);
    Task<int> RetrieveFolderAndWaitAsync(Guid searchJobId, RetrieveFolderRequestDto request, CancellationToken ct = default);
    Task<JobSummaryDto?> StartSongDownloadAsync(Guid searchJobId, StartSongDownloadRequestDto request, CancellationToken ct = default);
    Task<JobSummaryDto?> StartAlbumDownloadAsync(Guid searchJobId, StartAlbumDownloadRequestDto request, CancellationToken ct = default);
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default);
    Task<bool> CancelJobByDisplayIdAsync(int displayId, CancellationToken ct = default);
    Task<int> CancelWorkflowAsync(Guid workflowId, CancellationToken ct = default);
}
