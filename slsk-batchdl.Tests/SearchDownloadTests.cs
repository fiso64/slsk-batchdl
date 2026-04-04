using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Jobs;
using Soulseek;

// TODO: Move this basic test into SearcherTests.cs and delete this file as it doesn't test anything download-related.
namespace Tests.SearchDownloadTests
{
    [TestClass]
    public class MockSoulseekClientTests
    {
        [TestMethod]
        public async Task SearchAlbum_ReturnsMatchingResults()
        {
            // Arrange
            var index = TestHelpers.CreateTestIndex();
            var config = new Config();
            var client = new ClientTests.MockSoulseekClient(index);
            var clientManager = TestHelpers.CreateMockClientManager(client, config);
            var registry = TestHelpers.CreateSessionRegistry();
            var engine = new DownloadEngine(config, clientManager, Utilities.NullProgressReporter.Instance);
            var searcher = new Searcher(client, registry, registry, Utilities.NullProgressReporter.Instance, 999, 1);
            var job = new AlbumJob(new AlbumQuery { Album = "testalbum", Artist = "testartist" });

            // Act
            await searcher.SearchAlbum(job, new Config(), new ResponseData(), CancellationToken.None);

            // Assert: the testuser folder (4 files) should be found
            var testUserFolder = job.FoundFolders.First(f => f.Username == "testuser");
            Assert.AreEqual(4, testUserFolder.Files.Count);
            CollectionAssert.AreEqual(
                index.First(x => x.Username == "testuser").Files.Select(x => x.Filename).ToList(),
                testUserFolder.Files.Select(x => x.Candidate.File.Filename).ToList());
        }
    }
}
