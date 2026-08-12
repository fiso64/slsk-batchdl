using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Chat;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class ChatContractsTests
{
    [DataTestMethod]
    [DataRow("hello alice", "alice", true)]
    [DataRow("hello @Alice!", "alice", true)]
    [DataRow("malice", "alice", false)]
    [DataRow("alice_2", "alice", false)]
    [DataRow("alice-b", "alice", false)]
    public void MentionDetectionUsesWholeTokens(string message, string username, bool expected)
        => Assert.AreEqual(expected, MentionDetector.ContainsWholeUsername(message, username));

    [TestMethod]
    public void ChatSettingsNormalizeRoomsAndRemoveExactDuplicates()
    {
        var settings = new EngineSettings();
        settings.Chat.AutoJoinRooms.AddRange(["  indie ", "indie", "Indie"]);

        ChatSettingsValidator.NormalizeAndValidate(settings);

        CollectionAssert.AreEqual(new[] { "indie", "Indie" }, settings.Chat.AutoJoinRooms);
    }

    [TestMethod]
    public void MessageValidationPreservesWhitespaceButRejectsBlankAndNul()
    {
        Assert.AreEqual(" hello\n", ChatIdentity.ValidateMessage(" hello\n"));
        Assert.ThrowsException<ArgumentException>(() => ChatIdentity.ValidateMessage(" \t"));
        Assert.ThrowsException<ArgumentException>(() => ChatIdentity.ValidateMessage("hello\0world"));
    }

    [TestMethod]
    public void ChatTextValidationUsesUtf8BoundsAndRejectsMalformedUtf16()
    {
        Assert.ThrowsException<ArgumentException>(() => ChatIdentity.ValidateMessage(
            new string('x', ChatLimits.MaximumMessageUtf8Bytes + 1)));
        Assert.ThrowsException<ArgumentException>(() => ChatIdentity.ValidateMessage("hello\ud800"));
        Assert.ThrowsException<ArgumentException>(() => ChatIdentity.NormalizeRoom("room\udc00"));
    }

    [TestMethod]
    public void UsernameValidationPreservesExactSoulseekSpelling()
    {
        string decomposed = " Cafe\u0301 ";

        Assert.AreEqual(decomposed, ChatIdentity.ValidateUsername(decomposed));
        Assert.AreNotEqual(
            ChatIdentity.ValidateUsername("Alice"),
            ChatIdentity.ValidateUsername("alice"));
    }

    [TestMethod]
    public void CanonicallyEquivalentRoomNamesNormalizeToOneIdentity()
    {
        Assert.AreEqual("caf\u00e9", ChatIdentity.NormalizeRoom(" cafe\u0301 "));
    }

    [TestMethod]
    public void DuplicateConfiguredRoomsCountOnceBeforeCapacityValidation()
    {
        var settings = new EngineSettings();
        settings.Chat.AutoJoinRooms.AddRange(
            Enumerable.Repeat("indie", ChatLimits.MaximumDesiredRooms + 1));

        ChatSettingsValidator.NormalizeAndValidate(settings);

        CollectionAssert.AreEqual(new[] { "indie" }, settings.Chat.AutoJoinRooms);
    }
}
