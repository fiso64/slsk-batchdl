using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

public sealed class StaleDownloadException : TimeoutException
{
    private const string MessagePrefix = "Download attempt became stale after ";

    public StaleDownloadException(FileCandidate candidate, int maxStaleTimeMs)
        : base(
            $"{MessagePrefix}{maxStaleTimeMs}ms without peer transfer activity: " +
            $"{candidate.Username}\\{candidate.Filename}")
    {
        Candidate = candidate;
        MaxStaleTimeMs = maxStaleTimeMs;
    }

    public FileCandidate Candidate { get; }
    public int MaxStaleTimeMs { get; }

    public static bool IsStaleFailureMessage(string? message)
        => message?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true;
}
