using System.Collections.Concurrent;

namespace Sockseek.Core.Services;

public interface IUserSuccessStats
{
    ConcurrentDictionary<string, int> UserSuccessCounts { get; }
}

// TODO [V4]: Define the ownership, lifetime, persistence/decay, and ranking
// reproducibility semantics of per-user success counts in a long-running daemon.
// Preserve their existing within-workflow ranking effect for now; do not promote
// them to a shared or durable reputation resource until those semantics exist.
public sealed class UserSuccessTracker : IUserSuccessStats
{
    public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();

    public void RecordSuccess(string username)
        => UserSuccessCounts.AddOrUpdate(username, 1, (_, count) => count + 1);
}
