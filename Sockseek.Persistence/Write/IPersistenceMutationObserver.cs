namespace Sockseek.Persistence.Write;

/// <summary>
/// Observes the durable outcome of mutation batches. Notifications happen only
/// after the transaction commit is visible, or after the writer has abandoned a
/// batch permanently.
/// </summary>
public interface IPersistenceMutationObserver
{
    void Committed(IReadOnlyList<PersistenceMutation> mutations);

    void PermanentlyFailed(
        IReadOnlyList<PersistenceMutation> mutations,
        Exception exception);
}
