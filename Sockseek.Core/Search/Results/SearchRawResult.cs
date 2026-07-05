using Soulseek;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

public sealed record SearchRawResult
{
    internal SearchResponse Response { get; }
    internal Soulseek.File File { get; }

    public long Sequence { get; }
    public int Revision { get; }
    public string Username { get; }
    public string Filename { get; }
    public long Size { get; }
    public int? BitRate { get; }
    public int? SampleRate { get; }
    public int? Length { get; }
    public string Extension { get; }
    public int? UploadSpeed { get; }
    public bool? HasFreeUploadSlot { get; }
    public IReadOnlyList<FileAttributeSnapshot>? Attributes { get; }

    internal SearchRawResult(long sequence, int revision, SearchResponse response, Soulseek.File file)
    {
        Response = response;
        File = file;
        Sequence = sequence;
        Revision = revision;
        Username = response.Username;
        Filename = file.Filename;
        Size = file.Size;
        BitRate = file.BitRate;
        SampleRate = file.SampleRate;
        Length = file.Length;
        Extension = file.Extension;
        UploadSpeed = response.UploadSpeed;
        HasFreeUploadSlot = response.HasFreeUploadSlot;
        Attributes = file.Attributes == null
            ? null
            : Array.AsReadOnly(file.Attributes
                .Select(attribute => new FileAttributeSnapshot(attribute.Type.ToString(), attribute.Value))
                .ToArray());
    }
}
