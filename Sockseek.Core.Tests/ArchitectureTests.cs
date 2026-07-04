using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests;

[TestClass]
public class ArchitectureTests
{
    // Temporary source-level tripwires for the JobOutcome refactor. Once job snapshots
    // are immutable and lifecycle changes go through a reducer/state-store boundary,
    // the compiler should enforce these invariants and these tests can go away.
    private static readonly string[] OutcomeProcessors =
    [
        "ProcessExtractJob",
        "ProcessSearchJob",
        "ProcessRetrieveFolderJob",
        "ProcessSongDiscovery",
        "ProcessAlbumDiscovery",
        "ProcessAggregateDiscovery",
        "ProcessAlbumAggregateDiscovery",
        "ProcessLeafDownload",
        "ProcessSongDownload",
        "ProcessAggregateDownload",
        "ProcessAlbumDownload",
        "DownloadSong",
        "DownloadEmbeddedSong",
        "SearchAndDownloadSong",
    ];

    private static readonly HashSet<string> LegacyTerminalMutators =
    [
        "SetDone",
        "SetAlreadyExists",
        "SetSkipped",
        "Fail",
    ];

    [TestMethod]
    public void OutcomeProcessorTripwires_DoNotDirectlyCallLegacyTerminalMutators()
    {
        var sources = LoadCoreSources();

        foreach (var processor in OutcomeProcessors)
        {
            var methods = sources
                .SelectMany(source => source.Methods.Where(method => method.Identifier.ValueText == processor)
                    .Select(method => (source, method)))
                .ToList();

            Assert.IsTrue(methods.Count > 0, $"Method {processor} was not found in Sockseek.Core source files.");

            foreach (var (source, method) in methods)
                AssertNoLegacyTerminalMutators(source, method, processor);
        }
    }

    [TestMethod]
    public void SearcherTripwire_DoesNotDirectlyCallLegacyTerminalMutators()
    {
        var methods = LoadCoreSources()
            .SelectMany(source => source.Methods
                .Where(method => method.Identifier.ValueText == "Search"
                    && method.Ancestors().OfType<ClassDeclarationSyntax>().Any(type => type.Identifier.ValueText == "Searcher"))
                .Select(method => (source, method)))
            .ToList();

        Assert.IsTrue(methods.Count > 0, "Searcher.Search was not found in Sockseek.Core source files.");

        foreach (var (source, method) in methods)
            AssertNoLegacyTerminalMutators(source, method, "Searcher.Search");
    }

    [TestMethod]
    public void CancellationOutcomeTripwire_DoesNotCommitSourceLessCancellations()
    {
        foreach (var source in LoadCoreSources())
        {
            foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsJobOutcomeFailedCancelled(invocation) || IsFailCancelled(invocation))
                {
                    Assert.Fail($"{FormatLocation(source, invocation)} should use JobOutcome.Cancelled(source) / SetCancelled(source) instead of source-less cancelled failure.");
                }
            }
        }
    }

    [TestMethod]
    public void DownloadOrchestration_LivesUnderTransfersDownloads()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(coreRoot, "Engine")),
            "Download orchestration should live under Sockseek.Core/Transfers/Downloads, not a top-level Engine folder.");

        Assert.IsTrue(
            File.Exists(Path.Combine(coreRoot, "Transfers", "Downloads", "DownloadEngine.cs")),
            "DownloadEngine.cs should live under Sockseek.Core/Transfers/Downloads.");

        Assert.IsTrue(
            File.Exists(Path.Combine(coreRoot, "Transfers", "Downloads", "JobOrchestration", "JobOrchestrator.cs")),
            "Download job orchestration should live under Sockseek.Core/Transfers/Downloads/JobOrchestration.");
    }

    [TestMethod]
    public void CoreStructure_DoesNotUseCatchAllModelsFolder()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(coreRoot, "Models")),
            "Do not add a top-level Models folder; place data shapes beside their owning domain.");
    }

    [TestMethod]
    public void DownloadSessionState_UsesPurposeBuiltCollaborators()
    {
        var coreRoot = Path.Combine(FindRepositoryRoot(), "Sockseek.Core");

        Assert.IsFalse(
            Directory.EnumerateFiles(coreRoot, "Registries.cs", SearchOption.AllDirectories).Any(),
            "Mixed session-state registries obscure ownership; use purpose-built collaborators instead.");

        var retiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SessionRegistry",
            "IDownloadRegistry",
            "IUserStats",
        };
        var declarations = LoadCoreSources()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(type => (source, Name: type.Identifier.ValueText)))
            .Where(type => retiredTypes.Contains(type.Name))
            .Select(type => $"{type.Name} at {Path.GetRelativePath(FindRepositoryRoot(), type.source.Path)}")
            .ToList();

        Assert.IsFalse(
            declarations.Count > 0,
            "Retired mixed registry types should not be reintroduced:\n" + string.Join("\n", declarations));
    }

    private static void AssertNoLegacyTerminalMutators(CoreSource source, MethodDeclarationSyntax method, string methodName)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var invocationName = memberAccess.Name.Identifier.ValueText;
            Assert.IsFalse(
                LegacyTerminalMutators.Contains(invocationName),
                $"{methodName} should return/commit JobOutcome instead of directly calling {invocationName} at {FormatLocation(source, invocation)}");
        }
    }

    private static bool IsJobOutcomeFailedCancelled(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.ValueText == "Failed"
            && memberAccess.Expression.ToString() == "JobOutcome"
            && FirstArgumentContainsCancelled(invocation);
    }

    private static bool IsFailCancelled(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;

        return memberAccess.Name.Identifier.ValueText == "Fail"
            && FirstArgumentContainsCancelled(invocation);
    }

    private static bool FirstArgumentContainsCancelled(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault()?.ToString()
            .Contains("JobFailureReason.Cancelled", StringComparison.Ordinal) == true;

    private static string FormatLocation(CoreSource source, SyntaxNode node)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        return $"{Path.GetRelativePath(FindRepositoryRoot(), source.Path)}:{lineSpan.StartLinePosition.Line + 1}";
    }

    private static List<CoreSource> LoadCoreSources()
    {
        var root = FindRepositoryRoot();
        var coreRoot = Path.Combine(root, "Sockseek.Core");

        return Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var text = File.ReadAllText(path);
                var tree = CSharpSyntaxTree.ParseText(text, path: path);
                var rootNode = tree.GetRoot();
                return new CoreSource(path, rootNode, rootNode.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList());
            })
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.EnumerateFiles(dir.FullName, "*.sln")
            .Any(path => Path.GetFileName(path).Equals("Sockseek.sln", StringComparison.OrdinalIgnoreCase)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed record CoreSource(string Path, SyntaxNode Root, IReadOnlyList<MethodDeclarationSyntax> Methods);
}
