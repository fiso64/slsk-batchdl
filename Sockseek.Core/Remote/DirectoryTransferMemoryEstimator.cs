namespace Sockseek.Core.Models;

/// <summary>
/// Conservative retained-memory estimate for an admitted plan plus the file-job
/// graph materialized from it. Constants are rounded upward from the allocation
/// benchmark in <c>DirectoryTransferMemoryBenchmarks</c> on 64-bit .NET.
/// </summary>
public static class DirectoryTransferMemoryEstimator
{
    // The measured graph is roughly 1.21 KiB per ordinary entry before text and
    // attributes. Padding to 1.5 KiB covers runtime/layout variation and observer
    // registrations without pretending the estimate is object-layout exact.
    private const long FixedPlanBytes = 1_024;
    private const long FixedEntryAndChildBytes = 1_536;
    private const long FixedAttributeBytes = 64;
    private const long StringObjectBytes = 32;

    public static long EstimatePlanAndChildren(DirectoryTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        long bytes = checked(FixedPlanBytes + StringBytes(plan.DisplayRoot));
        foreach (var entry in plan.Entries)
        {
            bytes = checked(bytes
                + FixedEntryAndChildBytes
                + StringBytes(entry.Target.Username)
                + StringBytes(entry.Target.Filename)
                + StringBytes(entry.Target.Extension));

            foreach (string component in entry.RelativeDirectoryComponents)
                bytes = checked(bytes + StringBytes(component));

            if (entry.Target.Attributes != null)
            {
                foreach (var attribute in entry.Target.Attributes)
                    bytes = checked(bytes + FixedAttributeBytes + StringBytes(attribute.Type));
            }
        }

        return bytes;
    }

    private static long StringBytes(string? value)
        => value == null ? 0 : checked(StringObjectBytes + (long)value.Length * sizeof(char));
}
