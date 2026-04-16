namespace Sldl.Cli;

/// Parsed representation of a sldl.conf file.
public record ConfigFile(
    string Path,
    Dictionary<string, ProfileEntry> Profiles,
    bool HasAutoProfiles = false
);

/// One profile's token list and optional auto-apply condition expression.
public record ProfileEntry(List<string> Tokens, string? Condition);
