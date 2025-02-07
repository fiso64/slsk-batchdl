using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;

using Directory = Soulseek.Directory;
using File = Soulseek.File;

namespace Tests.ClientTests
{
    [TestClass]
    public class TestSoulseekClientTests
    {
        private List<(SearchResponse, List<Directory>)> CreateTestIndex()
        {
            var files = new List<File>
            {
                new File(1, "test.mp3", 1000, ".mp3"),
                new File(2, "another.mp3", 2000, ".mp3")
            };

            var directory = new Directory("Music", files);
            var searchResponse = new SearchResponse(
                username: "testUser",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList: files
            );

            return new List<(SearchResponse, List<Directory>)>
            {
                (searchResponse, new List<Directory> { directory })
            };
        }

        [TestMethod]
        public async Task BrowseAsync_ExistingUser_ReturnsCorrectFiles()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);

            // Act
            var result = await client.BrowseAsync("testUser");

            // Assert
            Assert.AreEqual(1, result.DirectoryCount);
            Assert.AreEqual("Music", result.Directories.First().Name);
            Assert.AreEqual(2, result.Directories.First().FileCount);
        }

        [TestMethod]
        [ExpectedException(typeof(UserNotFoundException))]
        public async Task BrowseAsync_NonExistentUser_ThrowsUserNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);

            // Act
            await client.BrowseAsync("nonexistentUser");
        }

        [TestMethod]
        public async Task SearchAsync_MatchingQuery_ReturnsResults()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);
            var query = new SearchQuery("test");

            // Act
            var result = await client.SearchAsync(query);

            // Assert
            Assert.AreEqual(1, result.Responses.Count);
            Assert.AreEqual(1, result.Responses.First().FileCount);
            Assert.AreEqual("test.mp3", result.Responses.First().Files.First().Filename);
        }

        [TestMethod]
        public async Task SearchAsync_NoMatches_ReturnsEmptyResults()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);
            var query = new SearchQuery("nonexistent");

            // Act
            var result = await client.SearchAsync(query);

            // Assert
            Assert.AreEqual(0, result.Responses.Count);
        }

        [TestMethod]
        public async Task DownloadAsync_ExistingFile_StartsTransfer()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);
            var tempFile = Path.GetTempFileName();

            try
            {
                // Act
                var transfer = await client.DownloadAsync(
                    username: "testUser",
                    remoteFilename: "Music\\test.mp3",
                    localFilename: tempFile);

                // Assert
                Assert.AreEqual(TransferStates.Queued, transfer.State);
                Assert.AreEqual("Music\\test.mp3", transfer.Filename);
                Assert.AreEqual(1000, transfer.Size);
                Assert.IsTrue(System.IO.File.Exists(tempFile), "Downloaded file should exist");
                Assert.AreEqual(1000, new System.IO.FileInfo(tempFile).Length, "File size should match expected size");
            }
            finally
            {
                // Cleanup
                if (System.IO.File.Exists(tempFile))
                {
                    System.IO.File.Delete(tempFile);
                }
            }
        }

        [TestMethod]
        [ExpectedException(typeof(UserNotFoundException))]
        public async Task DownloadAsync_NonexistentUser_ThrowsUserNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);

            // Act
            await client.DownloadAsync(
                username: "nonexistentUser",
                remoteFilename: "Music\\test.mp3",
                localFilename: "test.mp3");
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public async Task DownloadAsync_NonexistentFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new TestSoulseekClient(index);

            // Act
            await client.DownloadAsync(
                username: "testUser",
                remoteFilename: "Music\\nonexistent.mp3",
                localFilename: "test.mp3");
        }
    }
}