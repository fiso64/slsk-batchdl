using Sockseek.Core.Snapshots;
using Soulseek;

using System.Text.Json.Serialization;

namespace Sockseek.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SearchResultVisibility>))]
public enum SearchResultVisibility
{
    Public,
    Locked,
}

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
    DateTimeOffset ObservedAtUtc,
    int? QueueLength = null,
    SearchResultVisibility Visibility = SearchResultVisibility.Public)
{
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
            new SearchPeerSnapshot(
                Username,
                ResponseFileCount,
                UploadSpeed,
                HasFreeUploadSlot,
                QueueLength,
                ObservedAtUtc),
            new FileSearchEvidence(Sequence, Revision, ObservedAtUtc, Visibility));

    internal static SearchProjectionInput FromLive(
        long sequence,
        int revision,
        SearchResponse response,
        Soulseek.File file,
        DateTimeOffset observedAtUtc,
        SearchResultVisibility visibility = SearchResultVisibility.Public)
        => new(
            sequence,
            revision,
            response.Username,
            visibility == SearchResultVisibility.Public
                ? response.Files.Count
                : response.LockedFiles.Count,
            file.Filename, file.Size, file.BitRate, file.BitDepth,
            file.SampleRate, file.Length, file.Extension,
            response.UploadSpeed, response.HasFreeUploadSlot,
            file.Attributes is null
                ? null
                : Array.AsReadOnly(file.Attributes.Select(attribute => new FileAttributeSnapshot(
                    attribute.Type.ToString(), attribute.Value, (int)attribute.Type)).ToArray()),
            observedAtUtc,
            response.QueueLength,
            visibility);
}
