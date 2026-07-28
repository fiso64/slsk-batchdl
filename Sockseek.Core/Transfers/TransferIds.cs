namespace Sockseek.Core.Transfers;

public static class TransferIds
{
    public static Guid New()
        => Guid.NewGuid();
}

public static class TransferAttemptIds
{
    public static Guid New()
        => Guid.NewGuid();
}
