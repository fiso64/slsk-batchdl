using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SubmissionOptionsStoreTests
{
    [TestMethod]
    public void RetirementClearsExactGenerationStateWithoutDeletingLaterSameIdOptions()
    {
        var store = new SubmissionOptionsStore();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        var firstOptions = new SubmissionOptionsDto(ProfileNames: ["first"]);
        store.SetWorkflowOptions(workflowId, firstOptions);
        store.SetJobOptions(firstJobId, firstOptions);
        store.SetJobOutputParentDir(firstJobId, "first-output");
        long firstVersion = store.CaptureWorkflowVersion(workflowId);

        var laterOptions = new SubmissionOptionsDto(ProfileNames: ["later"]);
        store.SetWorkflowOptions(workflowId, laterOptions);
        store.RetireWorkflow(workflowId, [firstJobId], firstVersion);

        var laterJob = new SearchJob("query") { WorkflowId = workflowId };
        Assert.AreSame(laterOptions, store.GetOptions(laterJob));
        Assert.IsNull(store.GetJobOutputParentDir(firstJobId));
        Assert.AreEqual((1, 0, 0), store.RetainedStateCounts);

        store.RetireWorkflow(
            workflowId,
            [laterJob.Id],
            store.CaptureWorkflowVersion(workflowId));
        Assert.AreEqual((0, 0, 0), store.RetainedStateCounts);
    }
}
