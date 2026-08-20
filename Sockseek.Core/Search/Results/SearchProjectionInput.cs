using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Core.Models;

/// <summary>
/// Storage- and provider-neutral search input used at projection boundaries.
/// </summary>
public sealed record SearchProjectionInput(
    long Sequence,
    int Revision,
    string Username,
    int ResponseFileCount,
    string Filename,
    long Size,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    string Extension,
    int? UploadSpeed,
    bool? HasFreeUploadSlot,
    IReadOnlyList<FileAttributeSnapshot>? Attributes,
    DateTimeOffset ObservedAtUtc)
{
    internal SearchResponse? LiveResponse { get; init; }
    internal Soulseek.File? LiveFile { get; init; }

    public FileCandidate ToFileCandidate()
        => new(
            new PeerFileTarget(
                new PeerFileIdentity(Username, Filename),
                Size < 0 ? null : Size,
                Extension,
                BitRate,
                BitDepth,
                SampleRate,
                Length,
                Attributes),
            new SearchPeerSnapshot(Username, ResponseFileCount, UploadSpeed, HasFreeUploadSlot),
            new FileSearchEvidence(Sequence, Revision, ObservedAtUtc));

    internal static SearchProjectionInput FromLive(
        long sequence,
        int revision,
        SearchResponse response,
        Soulseek.File file,
        DateTimeOffset observedAtUtc)
        => new(
            sequence, revision, response.Username, response.Files.Count,
            file.Filename, file.Size, file.BitRate, file.BitDepth,
            file.SampleRate, file.Length, file.Extension,
            response.UploadSpeed, response.HasFreeUploadSlot,
            file.Attributes?.Select(attribute => new FileAttributeSnapshot(
                attribute.Type.ToString(), attribute.Value, (int)attribute.Type)).ToArray(),
            observedAtUtc)
        {
            LiveResponse = response,
            LiveFile = file,
        };
}
