using BenchmarkDotNet.Attributes;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Benchmarks;

/// <summary>
/// Measures allocation for the complete immutable plan and materialized child-job
/// graph used to calibrate <see cref="DirectoryTransferMemoryEstimator"/>.
/// </summary>
[Config(typeof(QuickBenchmarkConfig))]
[MemoryDiagnoser]
public class DirectoryTransferMemoryBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int FileCount { get; set; }

    [Benchmark]
    public RemoteDirectoryJob BuildPlanAndChildren()
    {
        var entries = new DirectoryTransferEntry[FileCount];
        for (int index = 0; index < entries.Length; index++)
        {
            string folder = $"Disc {index / 20:D4}";
            string filename = $@"Share\Selection\{folder}\File {index:D6}.bin";
            var target = new PeerFileTarget(
                new PeerFileIdentity("benchmark-peer", filename),
                size: 1_048_576,
                extension: ".bin");
            entries[index] = new DirectoryTransferEntry(target, [folder]);
        }

        var plan = new DirectoryTransferPlan("Selection", entries);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));
        job.MaterializeDirectoryChildren(plan.Entries.Select(entry => new RemoteFileJob(entry.Target)));
        return job;
    }
}
