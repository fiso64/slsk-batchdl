using System.Collections.Concurrent;

namespace Sockseek.Core.Services;

public interface IUserSuccessStats
{
    ConcurrentDictionary<string, int> UserSuccessCounts { get; }
}

public sealed class UserSuccessTracker : IUserSuccessStats
{
    public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();

    public void RecordSuccess(string username)
        => UserSuccessCounts.AddOrUpdate(username, 1, (_, count) => count + 1);
}
