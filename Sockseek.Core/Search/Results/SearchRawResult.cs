using Soulseek;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

public sealed record SearchRawResult
{
    internal SearchResponse Response { get; }
    internal Soulseek.File File { get; }
    public SearchProjectionInput ProjectionInput { get; }

    public long Sequence { get; }
    public int Revision { get; }
    public string Username { get; }
    public string Filename { get; }
    public long Size { get; }
    public int? BitRate { get; }
    public int? BitDepth { get; }
    public int ResponseFileCount { get; }
    public int? SampleRate { get; }
    public int? Length { get; }
    public string Extension { get; }
    public int? UploadSpeed { get; }
    public bool? HasFreeUploadSlot { get; }
    public IReadOnlyList<FileAttributeSnapshot>? Attributes { get; }
    public DateTimeOffset ObservedAtUtc { get; }

    internal SearchRawResult(long sequence, int revision, SearchResponse response, Soulseek.File file, DateTimeOffset observedAtUtc)
    {
        Response = response;
        File = file;
        Sequence = sequence;
        Revision = revision;
        Username = response.Username;
        Filename = file.Filename;
        Size = file.Size;
        BitRate = file.BitRate;
        BitDepth = file.BitDepth;
        ResponseFileCount = response.Files.Count;
        SampleRate = file.SampleRate;
        Length = file.Length;
        Extension = file.Extension;
        UploadSpeed = response.UploadSpeed;
        HasFreeUploadSlot = response.HasFreeUploadSlot;
        ObservedAtUtc = observedAtUtc;
        Attributes = file.Attributes == null
            ? null
            : Array.AsReadOnly(file.Attributes
                .Select(attribute => new FileAttributeSnapshot(attribute.Type.ToString(), attribute.Value, (int)attribute.Type))
                .ToArray());
        ProjectionInput = SearchProjectionInput.FromLive(
            Sequence, Revision, response, file, ObservedAtUtc);
    }
}
