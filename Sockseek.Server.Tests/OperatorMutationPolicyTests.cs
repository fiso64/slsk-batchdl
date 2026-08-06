using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class OperatorMutationPolicyTests
{
    [TestMethod]
    public async Task SharingAndTransferMutationsCarryOneNamedOperatorPolicy()
    {
        await using var app = ServerHost.Build(
            [],
            new ServerOptions(),
            "http://127.0.0.1:0");
        var endpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(source => source.Endpoints);

        string[] expected =
        [
            "/api/sharing/scans",
            "/api/sharing/scans/{scanId:guid}/cancel",
            "/api/transfers/{transferId:guid}/cancel",
        ];
        foreach (string pattern in expected)
        {
            RouteEndpoint endpoint = endpoints
                .OfType<RouteEndpoint>()
                .Single(candidate =>
                    candidate.RoutePattern.RawText == pattern
                    && candidate.Metadata
                        .GetMetadata<HttpMethodMetadata>()!
                        .HttpMethods.Contains("POST"));
            OperatorMutationMetadata? metadata =
                endpoint.Metadata.GetMetadata<OperatorMutationMetadata>();

            Assert.IsNotNull(metadata, $"Missing operator policy on {pattern}.");
            Assert.AreEqual(OperatorMutationPolicy.Name, metadata.PolicyName);
        }
    }
}
