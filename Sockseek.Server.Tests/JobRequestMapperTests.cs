using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class JobRequestMapperTests
{
    [TestMethod]
    public void ApplySelectedFolderSnapshot_RejectsFileOutsideRequestedFolder()
    {
        var request = new StartFolderDownloadRequestDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            SelectedFolder: FolderDto(
                @"Artist\Album",
                [FileDto(@"Artist\Other\01. Artist - Track.mp3")]));

        Assert.ThrowsException<ArgumentException>(() =>
            JobRequestMapper.ApplySelectedFolderSnapshot(ResolvedFolder(), request));
    }

    [TestMethod]
    public void ApplySelectedFolderSnapshot_RejectsFileFromDifferentUser()
    {
        var request = new StartFolderDownloadRequestDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            SelectedFolder: FolderDto(
                @"Artist\Album",
                [FileDto(@"Artist\Album\01. Artist - Track.mp3", username: "other")]));

        Assert.ThrowsException<ArgumentException>(() =>
            JobRequestMapper.ApplySelectedFolderSnapshot(ResolvedFolder(), request));
    }

    [TestMethod]
    public void RemoteDirectoryDraft_RequiresExactlyOneCompleteSourceCase()
    {
        var plan = new DirectoryTransferPlanDto(
            "Root",
            [new DirectoryTransferEntryDto(
                new PeerFileTargetDto("Peer", @"Root\File.bin", 10, ".bin"),
                [])],
            10);

        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto(Username: "Peer")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            JobRequestMapper.CreateJob(new RemoteDirectoryJobDraftDto("Peer", "Root", plan)));

        var peer = (RemoteDirectoryJob)JobRequestMapper.CreateJob(
            new RemoteDirectoryJobDraftDto("Peer", "Root"));
        var resolved = (RemoteDirectoryJob)JobRequestMapper.CreateJob(
            new RemoteDirectoryJobDraftDto(Plan: plan));

        Assert.IsInstanceOfType<RemoteDirectorySource.PeerDirectory>(peer.Source);
        Assert.IsInstanceOfType<RemoteDirectorySource.Resolved>(resolved.Source);
    }

    private static AlbumFolder ResolvedFolder()
        => new("local", @"Artist\Album", []);

    private static AlbumFolderDto FolderDto(string folderPath, IReadOnlyList<FileCandidateDto> files)
        => new(
            new AlbumFolderRefDto("local", folderPath),
            "local",
            folderPath,
            new PeerInfoDto("local"),
            files.Count,
            files.Count,
            files,
            IsFullyRetrieved: true);

    private static FileCandidateDto FileDto(string filename, string username = "local")
        => new(
            new FileCandidateRefDto(username, filename),
            username,
            filename,
            new PeerInfoDto(username),
            new FileMetadataDto(
                Path.GetFileName(filename),
                Size: 123,
                Extension: ".mp3",
                BitRate: null,
                BitDepth: null,
                SampleRate: null,
                Length: null));
}
