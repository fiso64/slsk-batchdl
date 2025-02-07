using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Soulseek;

using Models;
using Enums;
using Extractors;
using Services;
using Tests;

using Directory = Soulseek.Directory;
using File = Soulseek.File;

namespace Tests.EndToEnd
{
    [TestClass]
    public class ProgramTests
    {
        private ISoulseekClient _originalClient;
        private IExtractor _originalExtractor;
        private TrackLists _originalTrackLists;

        [TestInitialize]
        public void TestInitialize()
        {
            // Store the original values
            _originalClient = Program.client;
            _originalExtractor = Program.extractor;
            _originalTrackLists = Program.trackLists;

            // Reset static state that Main touches, if necessary
            Program.searches.Clear();
            Program.downloads.Clear();
            Program.userSuccessCounts.Clear();
            Program.skipUpdate = false;
            Program.interceptKeys = false;
            Program.initialized = false; // Reset the initialized flag
        }

        [TestCleanup]
        public void TestCleanup()
        {
            // Restore original values after each test to prevent state leakage
            Program.client = _originalClient;
            Program.extractor = _originalExtractor;
            Program.trackLists = _originalTrackLists;

            // Reset static state
            Program.searches.Clear();
            Program.downloads.Clear();
            Program.userSuccessCounts.Clear();
            Program.skipUpdate = false;
            Program.interceptKeys = false;
            Program.initialized = false;
        }

        [TestMethod]
        public async Task Main_E2E_Test()
        {
            var testClient = new ClientTests.MockSoulseekClient(CreateTestIndex());

            Program.client = testClient;

            var results = await testClient.SearchAsync(new SearchQuery("testartist - testalbum"));

            var testArgs = new string[]
            {
                "--input", "testartist testalbum",
                "--album",
                "--user", "test_user",
                "--pass", "test_pass",
                "--print-results",
            };

            var originalConsoleOut = Console.Out;
            using var stringWriter = new StringWriter();
            Console.SetOut(stringWriter);

            try
            {
                await Program.Main(testArgs);
            }
            finally
            {
                Console.SetOut(originalConsoleOut);
            }

            var consoleOutput = stringWriter.ToString();

            Console.WriteLine("Captured console output:\n" + consoleOutput);
        }

        private List<SearchResponse> CreateTestIndex()
        {
            var index = new List<SearchResponse>();

            var user1SearchResponse = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList: new List<File>
                {
                    new File(1, "Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3", 10000, ".mp3"),
                    new File(2, "Music\\Artist1\\Album1\\Artist1 - Album1 - 02 - Track2.mp3", 12000, ".mp3"),
                    new File(3, "Music\\Artist1\\Album1\\Artist1 - Album1 - 03 - Track3.flac", 25000, ".flac"),
                    new File(4, "Music\\Artist1\\Album1\\Artist1 - Album1 - cover.jpg", 500, ".jpg"),
                    new File(5, "Music\\Artist1\\Album1\\Artist1 - Album1.cue", 100, ".cue"),
                    new File(6, "Music\\Artist1\\Album2\\Artist1 - Album2 - 01 - Track1.mp3", 11000, ".mp3"),
                    new File(7, "Music\\Artist1\\Album2\\Artist1 - Album2 - 02 - Track2.flac", 28000, ".flac"),
                    new File(8, "Music\\Artist1\\Album2\\Artist1 - Album2 - cover.jpg", 600, ".jpg")
                }
            );

            var user2SearchResponse = new SearchResponse(
                username: "user2",
                token: 2,
                hasFreeUploadSlot: false,
                uploadSpeed: 50,
                queueLength: 5,
                fileList: new List<File>
                {
                    new File(9,  "Music\\Artist2\\Album1\\Artist2 - Album1 - 01 - Track1.mp3", 9000, ".mp3"),
                    new File(10, "Music\\Artist2\\Album1\\Artist2 - Album1 - 02 - Track2.mp3", 11000, ".mp3"),
                    new File(11, "Music\\Artist2\\Album1\\Artist2 - Album1 - cover.jpg", 400, ".jpg"),
                    new File(12, "Music\\Artist2\\Artist2 - Single.flac", 30000, ".flac"),
                    new File(13, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\01 Track 1.mp3", 8000, ".mp3"),
                    new File(14, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\02 Track 2.mp3", 8000, ".mp3"),
                    new File(15, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\cover.jpg", 300, ".jpg"),
                }
            );

            var user3SearchResponse = new SearchResponse(
                username: "user3",
                token: 3,
                hasFreeUploadSlot: true,
                uploadSpeed: 75,
                queueLength: 2,
                fileList: new List<File>
                {
                    new File(16, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0101. testartist - testsong.mp3", 15000, ".mp3"),
                    new File(17, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0102. testartist - testsong2.mp3", 16000, ".mp3"),
                    new File(18, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0103. testartist - testsong3.mp3", 17000, ".mp3"),
                    new File(19, "Music\\music\\testartist\\(2011) testalbum [MP3]\\cover.jpg", 700, ".jpg")
                }
            );

            index.Add(user1SearchResponse);
            index.Add(user2SearchResponse);
            index.Add(user3SearchResponse);

            return index;
        }

    }
}
