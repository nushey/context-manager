using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class EdgeExtractorTests
{
    private static string FixturePath(string name)
        => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Fixtures",
            "GraphFixtures",
            name);

    private static readonly string SimpleServicePath = FixturePath("SimpleService.cs");
    private static readonly string ISimpleServicePath = FixturePath("ISimpleService.cs");
    private static readonly string PrimaryConstructorClassPath = FixturePath("PrimaryConstructorClass.cs");
    private static readonly string GenericConstructorClassPath = FixturePath("GenericConstructorClass.cs");
    private static readonly string TypeWithMembersPath = FixturePath("TypeWithMembers.cs");

    private static (SemanticModel Model, CompilationUnitSyntax Root) ParseWithCompilation(
        string[] sourcePaths)
    {
        var trees = sourcePaths
            .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "EdgeExtractorTest",
            syntaxTrees: trees);

        // Use the first file's tree as the primary analysis target.
        var primaryTree = trees[0];
        var model = compilation.GetSemanticModel(primaryTree);
        var root = (CompilationUnitSyntax)primaryTree.GetRoot();

        return (model, root);
    }

    private static EdgeExtractor CreateExtractor() => new();

    [TestMethod]
    public void Extract_SimpleServiceImplementsISimpleService_EmitsImplementsEdge()
    {
        var (model, root) = ParseWithCompilation([SimpleServicePath, ISimpleServicePath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var implementsEdge = edges.FirstOrDefault(e =>
            e.Type == "IMPLEMENTS" &&
            e.Source.Id.Contains("SimpleService") &&
            e.Target.Id.Contains("ISimpleService"));

        Assert.IsNotNull(implementsEdge,
            $"Expected IMPLEMENTS edge from SimpleService to ISimpleService. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        Assert.AreEqual("Class", implementsEdge!.Source.Kind, "Source node kind should be Class");
        Assert.AreEqual("Interface", implementsEdge.Target.Kind, "Target node kind should be Interface");
    }

    [TestMethod]
    public void Extract_SimpleServiceInjectsISimpleService_EmitsInjectsEdge()
    {
        var (model, root) = ParseWithCompilation([SimpleServicePath, ISimpleServicePath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var injectsEdge = edges.FirstOrDefault(e =>
            e.Type == "INJECTS" &&
            e.Source.Id.Contains("SimpleService") &&
            e.Target.Id.Contains("ISimpleService"));

        Assert.IsNotNull(injectsEdge,
            $"Expected INJECTS edge from SimpleService to ISimpleService. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_BclTypes_NoEdgesEmitted()
    {
        var (model, root) = ParseWithCompilation([SimpleServicePath, ISimpleServicePath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var bclEdges = edges.Where(e =>
            e.Target.Id.StartsWith("System.", StringComparison.Ordinal) ||
            e.Source.Id.StartsWith("System.", StringComparison.Ordinal)).ToList();

        Assert.AreEqual(0, bclEdges.Count,
            $"No edges to BCL types expected. Found: [{string.Join(", ", bclEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_NoInheritsEdge_WhenNoBaseClass()
    {
        var (model, root) = ParseWithCompilation([SimpleServicePath, ISimpleServicePath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var inheritsEdges = edges.Where(e => e.Type == "INHERITS").ToList();

        Assert.AreEqual(0, inheritsEdges.Count,
            $"Expected no INHERITS edges (SimpleService only implements an interface). Found: [{string.Join(", ", inheritsEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_PrimaryConstructor_GeneratesInjectsEdge()
    {
        var (model, root) = ParseWithCompilation([PrimaryConstructorClassPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var injectsEdge = edges.FirstOrDefault(e =>
            e.Type == "INJECTS" &&
            e.Source.Id.Contains("MyServiceConsumer") &&
            e.Target.Id.Contains("IMyService"));

        Assert.IsNotNull(injectsEdge,
            $"Expected INJECTS edge from MyServiceConsumer to IMyService. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_GenericPrimaryConstructor_GeneratesInjectsEdge()
    {
        var (model, root) = ParseWithCompilation([GenericConstructorClassPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var injectsEdge = edges.FirstOrDefault(e =>
            e.Type == "INJECTS" &&
            e.Source.Id.Contains("MyRepositoryConsumer") &&
            e.Target.Id.Contains("IRepository"));

        Assert.IsNotNull(injectsEdge,
            $"Expected INJECTS edge from MyRepositoryConsumer to IRepository<MyEntity>. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_TypeWithMethod_EmitsContainsEdgeToMethod()
    {
        var (model, root) = ParseWithCompilation([TypeWithMembersPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var containsEdge = edges.FirstOrDefault(e =>
            e.Type == "CONTAINS" &&
            e.Source.Id.Contains("TypeWithMembers") &&
            e.Target.Kind == "Method");

        Assert.IsNotNull(containsEdge,
            $"Expected CONTAINS edge from TypeWithMembers to a Method node. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        Assert.AreEqual("Class", containsEdge!.Source.Kind, "CONTAINS source kind should be Class");
        Assert.AreEqual("Method", containsEdge.Target.Kind, "CONTAINS target kind should be Method");
    }

    [TestMethod]
    public void Extract_TypeWithProperty_EmitsContainsEdgeToProperty()
    {
        var (model, root) = ParseWithCompilation([TypeWithMembersPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var containsEdge = edges.FirstOrDefault(e =>
            e.Type == "CONTAINS" &&
            e.Source.Id.Contains("TypeWithMembers") &&
            e.Target.Kind == "Property");

        Assert.IsNotNull(containsEdge,
            $"Expected CONTAINS edge from TypeWithMembers to a Property node. Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        Assert.AreEqual("Class", containsEdge!.Source.Kind, "CONTAINS source kind should be Class");
        Assert.AreEqual("Property", containsEdge.Target.Kind, "CONTAINS target kind should be Property");
    }

    [TestMethod]
    public void Extract_TypeWithMembers_ContainsTargetIdMatchesGraphBuilderDisplayString()
    {
        var (model, root) = ParseWithCompilation([TypeWithMembersPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var methodContainsEdge = edges.FirstOrDefault(e =>
            e.Type == "CONTAINS" && e.Target.Kind == "Method");

        Assert.IsNotNull(methodContainsEdge, "Expected at least one CONTAINS edge to a Method node");
        // The ID must match IMethodSymbol.ToDisplayString() — same form GraphBuilder uses for method nodes.
        Assert.IsTrue(
            methodContainsEdge!.Target.Id.Contains("GetGreeting"),
            $"Expected method node ID to contain 'GetGreeting' (ToDisplayString form). Got: {methodContainsEdge.Target.Id}");
    }

    [TestMethod]
    public void Extract_TypeWithMembers_NoDuplicateContainsEdges()
    {
        var (model, root) = ParseWithCompilation([TypeWithMembersPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var containsEdges = edges.Where(e => e.Type == "CONTAINS").ToList();
        var distinct = containsEdges
            .Select(e => (e.Source.Id, e.Target.Id, e.Type))
            .Distinct()
            .ToList();

        Assert.AreEqual(distinct.Count, containsEdges.Count,
            $"Duplicate CONTAINS edges detected. All CONTAINS edges: [{string.Join(", ", containsEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_EdgeSourceKinds_AreCorrect()
    {
        var (model, root) = ParseWithCompilation([SimpleServicePath, ISimpleServicePath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        Assert.IsTrue(edges.Count > 0, "Expected at least one edge from the fixture");

        foreach (var edge in edges)
        {
            Assert.IsFalse(string.IsNullOrEmpty(edge.Source.Id), "Edge source Id must not be empty");
            Assert.IsFalse(string.IsNullOrEmpty(edge.Target.Id), "Edge target Id must not be empty");
            Assert.IsFalse(string.IsNullOrEmpty(edge.Type), "Edge type must not be empty");
        }
    }
}
