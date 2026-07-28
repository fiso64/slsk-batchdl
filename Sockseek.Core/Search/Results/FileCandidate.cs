using Soulseek;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;
    public class FileCandidate
    {
        internal SearchResponse Response { get; }
        internal Soulseek.File File { get; }

        public string Username => persistedUsername ?? Response.Username;
        public string Filename => persistedFilename ?? File.Filename;
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

        private readonly string? persistedUsername;
        private readonly string? persistedFilename;

        internal FileCandidate(SearchResponse response, Soulseek.File file)
        {
            Response = response;
            File = file;
            Size = file.Size;
            BitRate = file.BitRate;
            BitDepth = file.BitDepth;
            ResponseFileCount = response.Files.Count;
            SampleRate = file.SampleRate;
            Length = file.Length;
            Extension = file.Extension;
            UploadSpeed = response.UploadSpeed;
            HasFreeUploadSlot = response.HasFreeUploadSlot;
            Attributes = file.Attributes == null
                ? null
                : Array.AsReadOnly(file.Attributes.Select(attribute =>
                    new FileAttributeSnapshot(attribute.Type.ToString(), attribute.Value, (int)attribute.Type)).ToArray());
        }

        public FileCandidate(
            string username,
            string filename,
            long size,
            int? bitRate,
            int? bitDepth,
            int responseFileCount,
            int? sampleRate,
            int? length,
            string extension,
            int? uploadSpeed,
            bool? hasFreeUploadSlot,
            IReadOnlyList<FileAttributeSnapshot>? attributes)
        {
            persistedUsername = username;
            persistedFilename = filename;
            Response = null!;
            File = null!;
            Size = size;
            BitRate = bitRate;
            BitDepth = bitDepth;
            ResponseFileCount = responseFileCount;
            SampleRate = sampleRate;
            Length = length;
            Extension = extension;
            UploadSpeed = uploadSpeed;
            HasFreeUploadSlot = hasFreeUploadSlot;
            Attributes = attributes;
        }

        public SearchProjectionInput ToProjectionInput(
            long sequence = 0,
            int revision = 0,
            DateTimeOffset? observedAtUtc = null)
            => new(
                sequence, revision, Username, ResponseFileCount, Filename, Size, BitRate, BitDepth,
                SampleRate, Length, Extension, UploadSpeed, HasFreeUploadSlot, Attributes,
                observedAtUtc ?? DateTimeOffset.UnixEpoch);
    }
