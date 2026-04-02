using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using Soulseek;
using Tests.ClientTests;

namespace Tests.SearchDownloadTests
{
    [TestClass]
    public class MockSoulseekClientTests
    {
        [TestMethod]
        public async Task GetAlbumDownloads_ReturnsMatchingResults()
        {
            // Arrange
            var index = TestHelpers.CreateTestIndex();
            var client = new MockSoulseekClient(index);
            var app = new DownloaderApplication(new Config(), client);
            var searcher = new Searcher(app, 999, 1);
            var album = new Track() { Album = "testalbum", Artist = "testartist", Type = Enums.TrackType.Album };

            // Act
            var results = await searcher.GetAlbumDownloads(album, new ResponseData(), new Config());

            // Assert
            Assert.AreEqual(4, results[0].Count);
            CollectionAssert.AreEqual(index.First(x => x.Username == "testuser").Files.Select(x => x.Filename).ToList(), results[0].Select(x => x.FirstDownload.Filename).ToList());
        }
    }
}
