namespace Sockseek.Core.Models;

public sealed record SearchProjectionSnapshot<T>(
    int Revision,
    IReadOnlyList<T> Items,
    bool IsComplete);
