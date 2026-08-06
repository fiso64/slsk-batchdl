namespace Sockseek.Persistence.Write;

internal abstract class AwaitablePersistenceCommand
{
    internal abstract Task ApplyAsync(SockseekDbContext context, CancellationToken cancellationToken);
    internal abstract void Complete();
    internal abstract void Fail(Exception exception);
}

internal sealed class AwaitablePersistenceCommand<TResult>(
    Func<SockseekDbContext, CancellationToken, Task<TResult>> apply)
    : AwaitablePersistenceCommand
{
    private readonly TaskCompletionSource<TResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TResult? result;

    public Task<TResult> Task => completion.Task;

    internal override async Task ApplyAsync(
        SockseekDbContext context,
        CancellationToken cancellationToken)
        => result = await apply(context, cancellationToken).ConfigureAwait(false);

    internal override void Complete()
        => completion.TrySetResult(result!);

    internal override void Fail(Exception exception)
        => completion.TrySetException(exception);
}
