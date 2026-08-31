using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;
    public class FileCandidate
    {
        public PeerFileTarget Target { get; }
        public SearchPeerSnapshot Peer { get; }
        public FileSearchEvidence Evidence { get; }

        public string Username => Target.Username;
        public string Filename => Target.Filename;
        public long Size => Target.Size ?? -1;
        public int? BitRate => Target.BitRate;
        public int? BitDepth => Target.BitDepth;
        public int ResponseFileCount => Peer.ResponseFileCount;
        public int? SampleRate => Target.SampleRate;
        public int? Length => Target.Length;
        public string Extension => Target.Extension ?? "";
        public int? UploadSpeed => Peer.UploadSpeed;
        public bool? HasFreeUploadSlot => Peer.HasFreeUploadSlot;
        public IReadOnlyList<FileAttributeSnapshot>? Attributes => Target.Attributes;

        public FileCandidate(
            PeerFileTarget target,
            SearchPeerSnapshot peer,
            FileSearchEvidence? evidence = null)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(peer);
            if (!StringComparer.Ordinal.Equals(target.Username, peer.Username))
                throw new ArgumentException("Search peer username must exactly match the target username.", nameof(peer));

            Target = target;
            Peer = peer;
            Evidence = evidence ?? FileSearchEvidence.Unspecified;
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
