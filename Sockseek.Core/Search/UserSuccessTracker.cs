using System.Collections.Concurrent;

namespace Sockseek.Core.Services;

public interface IUserSuccessStats
{
    ConcurrentDictionary<string, int> UserSuccessCounts { get; }
}

// TODO [V4]: Define the ownership, lifetime, persistence/decay, and ranking
// reproducibility semantics of per-user success counts. They currently live only
// for one engine process even though search ranking consumes them.
public sealed class UserSuccessTracker : IUserSuccessStats
{
    public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();

    public void RecordSuccess(string username)
        => UserSuccessCounts.AddOrUpdate(username, 1, (_, count) => count + 1);
}
