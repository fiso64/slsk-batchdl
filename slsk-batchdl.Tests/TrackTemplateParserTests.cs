using Microsoft.VisualStudio.TestTools.UnitTesting;
using Models;

namespace Tests.TrackParserTest
{
    [TestClass]
    public class TrackTemplateParserTests
    {
        // --- Tests for CreateFromString ---

        [TestMethod]
        public void CreateFromString_Basic_Success()
        {
            // Simple case with standard fields
            string input = "Artist Name - Track Title";
            string template = "{artist} - {title}";
            Track? track = TrackTemplateParser.CreateFromString(input, template);

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
            Track? track = TrackTemplateParser.CreateFromString(input, template);

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
            Track? track1 = TrackTemplateParser.CreateFromString("Just some random text", template);
            Assert.IsNull(track1, "Should return null for completely mismatched input.");

            // Scenario 2: Mismatched literal characters
            Track? track3 = TrackTemplateParser.CreateFromString("Artist Name / Title", template); // Uses '/' instead of '-'
            Assert.IsNull(track3, "Should return null for mismatched literal characters.");
        }

        [TestMethod]
        public void CreateFromString_TemplateWithUnknownField_IgnoresUnknownAndPopulatesKnown()
        {
            // Verifies that placeholders not matching Track fields are ignored,
            // while known fields are still processed correctly.
            string input = "Valid Artist - Valid Title [Some Extra Data]";
            string template = "{artist} - {title} [{unknown_field}]"; // Contains a field not in Track model
            Track? track = TrackTemplateParser.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Valid Artist", track.Artist); // Known field should be populated
            Assert.AreEqual("Valid Title", track.Title);   // Known field should be populated
            Assert.IsTrue(string.IsNullOrEmpty(track.Album)); // Album not in template or input structure for it
        }


        // --- Tests for TryUpdateTrack ---

        [TestMethod]
        public void TryUpdateTrack_BasicUpdate_Success()
        {
            // Simple case updating one field, ensuring others are untouched
            string input = "New Artist Name";
            string template = "{artist}";
            Track track = new Track { Artist = "Old Artist", Title = "Old Title", Album = "Old Album" };

            bool result = TrackTemplateParser.TryUpdateTrack(input, template, track);

            Assert.IsTrue(result);
            Assert.AreEqual("New Artist Name", track.Artist); // Updated
            Assert.AreEqual("Old Title", track.Title);       // Unchanged
            Assert.AreEqual("Old Album", track.Album);       // Unchanged
        }

        [TestMethod]
        public void TryUpdateTrack_NoMatch_ReturnsFalseAndDoesNotUpdate()
        {
            // Verifies the specific failure behavior of TryUpdateTrack
            string input = "This does not match the pattern";
            string template = "{artist} / {title}";
            Track track = new Track { Artist = "Original Artist", Title = "Original Title", Album = "Original Album" };
            // Create a shallow copy for comparison
            Track originalTrack = new Track { Artist = track.Artist, Title = track.Title, Album = track.Album };

            bool result = TrackTemplateParser.TryUpdateTrack(input, template, track);

            Assert.IsFalse(result);
            // Verify track object is unchanged by comparing field by field
            Assert.AreEqual(originalTrack.Artist, track.Artist, "Artist should not change on failed update.");
            Assert.AreEqual(originalTrack.Title, track.Title, "Title should not change on failed update.");
            Assert.AreEqual(originalTrack.Album, track.Album, "Album should not change on failed update.");
        }
    }
}
