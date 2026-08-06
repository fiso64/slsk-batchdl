using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class OpenApiContractTests
{
    [TestMethod]
    public async Task OpenApiDocument_ContainsCoreServerContractSchemas()
    {
        string musicRoot = Path.Combine(Path.GetTempPath(), "Sockseek-openapi-test-" + Guid.NewGuid());
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-openapi-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(outputDir);

        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                },
            },
            Profiles = ProfileCatalog.Empty,
        }, url);

        try
        {
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            using var response = await http.GetAsync("/api/openapi.json");

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var json = document.RootElement.GetRawText();

            var version = document.RootElement
                .GetProperty("info")
                .GetProperty("version")
                .GetString();

            Assert.AreEqual(ExpectedOpenApiVersion(), version);

            StringAssert.Contains(json, nameof(JobSummaryDto));
            StringAssert.Contains(json, nameof(SubmitAlbumJobRequestDto));
            StringAssert.Contains(json, nameof(AlbumJobPayloadDto));
            StringAssert.Contains(json, nameof(FileCandidateDto));
            StringAssert.Contains(json, nameof(WorkflowTreeDto));
            StringAssert.Contains(json, nameof(StateSnapshotDto));
            StringAssert.Contains(json, nameof(ApiErrorDto));
            StringAssert.Contains(json, nameof(SharingStateDto));
            StringAssert.Contains(json, nameof(UploadRuntimeStateDto));
            StringAssert.Contains(json, nameof(LiveTransferPageDto));
            StringAssert.Contains(json, nameof(TransferDetailDto));
            StringAssert.Contains(json, "lifecycleState");
            StringAssert.Contains(json, "activityPhase");
            StringAssert.Contains(json, "terminalOutcome");
            StringAssert.Contains(json, "discriminator");
            StringAssert.Contains(json, "kind");
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/daemon/snapshot", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/workflows/{workflowId}/snapshot", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/jobs/cancel-all", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/sharing", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/sharing/scans", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/transfers/live", out _));
            Assert.IsTrue(document.RootElement
                .GetProperty("paths")
                .TryGetProperty("/api/transfers/{transferId}/cancel", out _));
            Assert.IsFalse(json.Contains("ServerEventEnvelopeDto", StringComparison.Ordinal));

            var jobListParameterNames = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/jobs")
                .GetProperty("get")
                .GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString())
                .ToList();
            CollectionAssert.Contains(jobListParameterNames, "lifecycleState");
            CollectionAssert.Contains(jobListParameterNames, "terminalOutcome");
            Assert.IsFalse(jobListParameterNames.Contains("state"), "/api/jobs should not expose the old flattened state filter.");
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(musicRoot))
                Directory.Delete(musicRoot, recursive: true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    private static string ExpectedOpenApiVersion()
    {
        var assemblyVersion = typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ServerHost).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var metadataIndex = assemblyVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? assemblyVersion[..metadataIndex] : assemblyVersion;
    }

    [TestMethod]
    public void SockseekApiJsonContext_CoversApiDtoContracts()
    {
        var dtoTypes = typeof(SockseekApiJsonContext).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Namespace == typeof(SockseekApiJsonContext).Namespace
                && type.Name.EndsWith("Dto", StringComparison.Ordinal)
                && !type.ContainsGenericParameters)
            .OrderBy(type => type.FullName)
            .ToList();

        var closedGenericDtoTypes = new[]
        {
            typeof(SearchResultSnapshotDto<FileCandidateDto>),
            typeof(SearchResultSnapshotDto<AlbumFolderDto>),
            typeof(SearchResultSnapshotDto<AggregateTrackCandidateDto>),
            typeof(SearchResultSnapshotDto<AggregateAlbumCandidateDto>),
            typeof(CollectionPatchDto<string>),
            typeof(CollectionPatchDto<RegexRuleDto>),
        };

        var missing = dtoTypes
            .Concat(closedGenericDtoTypes)
            .Distinct()
            .Where(type => !HasApiJsonTypeInfo(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.AreEqual(
            0,
            missing.Count,
            "Missing SockseekApiJsonContext metadata for:" + "\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void LiveBatch_JsonRoundTrip_PreservesTypedDeltaAndPolymorphicActivity()
    {
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var summary = new JobSummaryDto(
            jobId,
            4,
            workflowId,
            ServerJobKind.Search,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Searching,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            "item",
            "query",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
        var batch = new StateUpdateBatchDto(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            7,
            8,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                Jobs =
                [
                    new JobDeltaDto(
                        jobId,
                        1,
                        Added: JobStateDto.FromSummary(summary, 1)),
                ],
            },
            [
                new ActivityEventDto(
                    8,
                    DateTimeOffset.UtcNow,
                    "job.message",
                    workflowId,
                    jobId,
                    null,
                    new JobMessageActivityDto(4, "Information", "test", "hello")),
                new ActivityEventDto(
                    9,
                    DateTimeOffset.UtcNow,
                    "workflow.message",
                    workflowId,
                    null,
                    null,
                    new WorkflowMessageActivityDto("Information", "test", "monitor attached")),
            ]);
        var options = SockseekApiJson.CreateSerializerOptions();

        string json = JsonSerializer.Serialize(batch, options);
        var roundTripped = JsonSerializer.Deserialize<StateUpdateBatchDto>(json, options);

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(batch.Scope, roundTripped.Scope);
        Assert.AreEqual(epoch, roundTripped.Epoch);
        Assert.AreEqual(jobId, roundTripped.State.Jobs.Single().Added?.JobId);
        Assert.IsInstanceOfType<JobMessageActivityDto>(roundTripped.Activity[0].Payload, out var jobMessage);
        Assert.AreEqual("hello", jobMessage.Message);
        Assert.IsInstanceOfType<WorkflowMessageActivityDto>(
            roundTripped.Activity[1].Payload,
            out var workflowMessage);
        Assert.AreEqual("monitor attached", workflowMessage.Message);
        StringAssert.Contains(json, "\"kind\":\"jobMessage\"");
        StringAssert.Contains(json, "\"kind\":\"workflowMessage\"");
    }

    [TestMethod]
    public async Task ServerInfo_AdvertisesEnforcedLiveProtocolVersion()
    {
        int port = GetFreeTcpPort();
        string url = $"http://127.0.0.1:{port}";
        await using var app = ServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url) };
            var info = await new SockseekApiClient(http).GetServerInfoAsync();

            Assert.AreEqual(LiveProtocol.Version, info.LiveProtocolVersion);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static bool HasApiJsonTypeInfo(Type type)
    {
        try
        {
            return SockseekApiJsonContext.Default.GetTypeInfo(type) != null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
