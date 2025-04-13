using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;
using System;
using System.IO; // Keep this if Models namespace needs it, otherwise can be removed

namespace Tests.TrackFactoryTest
{
    [TestClass]
    public class TrackFactoryTests
    {
        // --- Tests for CreateFromString ---

        [TestMethod]
        public void CreateFromString_Basic_Success()
        {
            // Simple case with standard fields
            string input = "Artist Name - Track Title";
            string template = "{artist} - {title}";
            Track? track = TrackFactory.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Artist Name", track.Artist);
            Assert.AreEqual("Track Title", track.Title);
            Assert.IsTrue(string.IsNullOrEmpty(track.Album)); // Album not in template
        }

        [TestMethod]
        public void CreateFromString_ComplexScenario_Success()
        {
            // Covers: Multiple fields, different order, literal braces in template,
            // braces in input, leading/trailing whitespace, case-insensitivity in template
            string input = "  {The Title} by Artist {Name} on [Album Name]  ";
            string template = "{{TITLE}} by {Artist} on [{album}]"; // Case difference, literal braces, literal brackets
            Track? track = TrackFactory.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Artist {Name}", track.Artist); // Input braces captured, whitespace trimmed
            Assert.AreEqual("Album Name", track.Album);     // Literal brackets matched, whitespace trimmed
            Assert.AreEqual("The Title", track.Title);      // Literal braces matched, whitespace trimmed
        }

        [TestMethod]
        public void CreateFromString_NoMatch_ReturnsNull()
        {
            // Covers: Input doesn't match template structure, partial match
            string template = "{artist} - {title}";

            // Scenario 1: Completely different input
            Track? track1 = TrackFactory.CreateFromString("Just some random text", template);
            Assert.IsNull(track1, "Should return null for completely mismatched input.");

            // Scenario 2: Partial match (missing required part)
            Track? track2 = TrackFactory.CreateFromString("Artist Name - ", template);
            Assert.IsNull(track2, "Should return null for partial match.");

            // Scenario 3: Mismatched literal characters
            Track? track3 = TrackFactory.CreateFromString("Artist Name / Title", template); // Uses '/' instead of '-'
            Assert.IsNull(track3, "Should return null for mismatched literal characters.");
        }

        // --- Tests for TryUpdateTrack ---

        [TestMethod]
        public void TryUpdateTrack_BasicUpdate_Success()
        {
            // Simple case updating one field
            string input = "New Artist Name";
            string template = "{artist}";
            Track track = new Track { Artist = "Old Artist", Title = "Old Title", Album = "Old Album" };

            bool result = TrackFactory.TryUpdateTrack(input, template, track);

            Assert.IsTrue(result);
            Assert.AreEqual("New Artist Name", track.Artist); // Updated
            Assert.AreEqual("Old Title", track.Title);       // Unchanged
            Assert.AreEqual("Old Album", track.Album);       // Unchanged
        }


        [TestMethod]
        public void TryUpdateTrack_ComplexUpdate_Success()
        {
            // Covers: Updating multiple fields, leaving others, whitespace, braces, case-insensitivity
            string input = "  New Artist {Jr.} - [New Album]  ";
            string template = "{ARTIST} - [{Album}]"; // Case difference, literal brackets
            Track track = new Track { Artist = "Old Artist", Title = "Old Title", Album = "Old Album" };

            bool result = TrackFactory.TryUpdateTrack(input, template, track);

            Assert.IsTrue(result);
            Assert.AreEqual("New Artist {Jr.}", track.Artist); // Updated, whitespace trimmed, braces captured
            Assert.AreEqual("New Album", track.Album);         // Updated, literal brackets matched, whitespace trimmed
            Assert.AreEqual("Old Title", track.Title);         // Should remain unchanged
        }

        [TestMethod]
        public void TryUpdateTrack_NoMatch_ReturnsFalseAndDoesNotUpdate()
        {
            string input = "This does not match the pattern";
            string template = "{artist} / {title}";
            Track track = new Track { Artist = "Original Artist", Title = "Original Title", Album = "Original Album" };
            // Create a shallow copy for comparison
            Track originalTrack = new Track { Artist = track.Artist, Title = track.Title, Album = track.Album };

            bool result = TrackFactory.TryUpdateTrack(input, template, track);

            Assert.IsFalse(result);
            // Verify track object is unchanged by comparing field by field
            Assert.AreEqual(originalTrack.Artist, track.Artist, "Artist should not change on failed update.");
            Assert.AreEqual(originalTrack.Title, track.Title, "Title should not change on failed update.");
            Assert.AreEqual(originalTrack.Album, track.Album, "Album should not change on failed update.");
        }
    }
}
