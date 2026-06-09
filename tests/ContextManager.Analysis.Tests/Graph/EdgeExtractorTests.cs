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
    private static readonly string TypeWithReferencesPath = FixturePath("TypeWithReferences.cs");
    private static readonly string OpenGenericImplementationPath = FixturePath("OpenGenericImplementation.cs");

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
            $"Expected INJECTS edge from MyRepositoryConsumer to IRepository<T> (open). Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        // The target must be the OPEN generic, not the closed IRepository<MyEntity> instantiation.
        Assert.IsFalse(injectsEdge!.Target.Id.Contains("MyEntity"),
            $"INJECTS target must be the open generic IRepository<T>, not the closed form. Got: {injectsEdge.Target.Id}");
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

    [TestMethod]
    public void Extract_MethodParameter_EmitsReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdge = edges.FirstOrDefault(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService") &&
            e.Target.Id.Contains("Location"));

        Assert.IsNotNull(referencesEdge,
            $"Expected REFERENCES edge from TourService to Location (method parameter). Edges found: [{string.Join(", ", edges.Where(e => e.Type == "REFERENCES").Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_MethodReturnType_EmitsReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdge = edges.FirstOrDefault(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService") &&
            e.Target.Id.Contains("Attraction") &&
            !e.Target.Id.Contains("IRepository"));

        Assert.IsNotNull(referencesEdge,
            $"Expected REFERENCES edge from TourService to Attraction (method return type). Edges found: [{string.Join(", ", edges.Where(e => e.Type == "REFERENCES").Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_PropertyType_EmitsReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdge = edges.FirstOrDefault(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService") &&
            e.Target.Id.Contains("Tour") &&
            !e.Target.Id.Contains("IRepository") &&
            !e.Target.Id.Contains("TourService"));

        Assert.IsNotNull(referencesEdge,
            $"Expected REFERENCES edge from TourService to Tour (property type). Edges found: [{string.Join(", ", edges.Where(e => e.Type == "REFERENCES").Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_FieldType_EmitsReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdge = edges.FirstOrDefault(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService") &&
            e.Target.Id.Contains("Booking"));

        Assert.IsNotNull(referencesEdge,
            $"Expected REFERENCES edge from TourService to Booking (field type). Edges found: [{string.Join(", ", edges.Where(e => e.Type == "REFERENCES").Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_GenericMember_EmitsReferencesToOpenGenericAndTypeArg()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdges = edges.Where(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService")).ToList();

        var toOpenGeneric = referencesEdges.FirstOrDefault(e =>
            e.Target.Id.Contains("IRepository") && !e.Target.Id.Contains("Attraction"));

        var toTypeArg = referencesEdges.FirstOrDefault(e =>
            e.Target.Id.Contains("Attraction") && !e.Target.Id.Contains("IRepository"));

        Assert.IsNotNull(toOpenGeneric,
            $"Expected REFERENCES edge from TourService to IRepository<T> (open generic). REFERENCES edges: [{string.Join(", ", referencesEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");

        Assert.IsNotNull(toTypeArg,
            $"Expected REFERENCES edge from TourService to Attraction (generic type arg). REFERENCES edges: [{string.Join(", ", referencesEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_LocalVariableInMethodBody_NoReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        // ProcessLocally() uses Tour as a local variable — this must NOT produce a REFERENCES edge
        // that doesn't also exist from a signature. TourService has a Tour property so there IS
        // a REFERENCES to Tour from the property signature; but that edge comes only once (dedup).
        // What we verify here is that the REFERENCES count to Tour equals exactly 1 (from the
        // property), not 2 (property + local variable).
        var tourReferences = edges.Count(e =>
            e.Type == "REFERENCES" &&
            e.Source.Id.Contains("TourService") &&
            e.Target.Id.Contains("Tour") &&
            !e.Target.Id.Contains("IRepository") &&
            !e.Target.Id.Contains("TourService"));

        Assert.AreEqual(1, tourReferences,
            $"Expected exactly 1 REFERENCES edge from TourService to Tour (from property, not duplicated by local var). Got {tourReferences}.");
    }

    [TestMethod]
    public void Extract_BclTypes_NoReferencesEdges()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var bclReferencesEdges = edges.Where(e =>
            e.Type == "REFERENCES" &&
            (e.Target.Id.StartsWith("System.", StringComparison.Ordinal) ||
             e.Target.Id == "string" ||
             e.Target.Id == "int" ||
             e.Target.Id == "bool")).ToList();

        Assert.AreEqual(0, bclReferencesEdges.Count,
            $"Expected no REFERENCES edges to BCL/primitive types. Found: [{string.Join(", ", bclReferencesEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_References_NoDuplicateEdges()
    {
        var (model, root) = ParseWithCompilation([TypeWithReferencesPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var referencesEdges = edges.Where(e => e.Type == "REFERENCES").ToList();
        var distinct = referencesEdges
            .Select(e => (e.Source.Id, e.Target.Id, e.Type))
            .Distinct()
            .ToList();

        Assert.AreEqual(distinct.Count, referencesEdges.Count,
            $"Duplicate REFERENCES edges detected. All REFERENCES edges: [{string.Join(", ", referencesEdges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }

    [TestMethod]
    public void Extract_InjectsClosedGeneric_TargetsOpenGenericNotClosedForm()
    {
        var (model, root) = ParseWithCompilation([OpenGenericImplementationPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var injectsEdge = edges.FirstOrDefault(e =>
            e.Type == "INJECTS" &&
            e.Source.Id.Contains("CatalogConsumer") &&
            e.Target.Id.Contains("IStore"));

        Assert.IsNotNull(injectsEdge,
            $"Expected INJECTS edge from CatalogConsumer to IStore<T> (open). Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        Assert.IsFalse(injectsEdge!.Target.Id.Contains("Catalog"),
            $"INJECTS target must be the open generic IStore<T>, not the closed IStore<Catalog>. Got: {injectsEdge.Target.Id}");

        var closedInjects = edges.Any(e =>
            e.Type == "INJECTS" && e.Target.Id.Contains("Catalog") && e.Target.Id.Contains("IStore"));
        Assert.IsFalse(closedInjects,
            "No INJECTS edge to the closed generic IStore<Catalog> should be created.");
    }

    [TestMethod]
    public void Extract_ReturnsClosedGeneric_TargetsOpenGenericNotClosedForm()
    {
        var (model, root) = ParseWithCompilation([OpenGenericImplementationPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var returnsEdge = edges.FirstOrDefault(e =>
            e.Type == "RETURNS" &&
            e.Source.Id.Contains("GetStore") &&
            e.Target.Id.Contains("IStore"));

        Assert.IsNotNull(returnsEdge,
            $"Expected RETURNS edge from GetStore to IStore<T> (open). Edges found: [{string.Join(", ", edges.Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
        Assert.IsFalse(returnsEdge!.Target.Id.Contains("Catalog"),
            $"RETURNS target must be the open generic IStore<T>, not the closed IStore<Catalog>. Got: {returnsEdge.Target.Id}");
    }

    [TestMethod]
    public void Extract_ClosedGenericTypeArg_SurfacesAsReferencesEdge()
    {
        var (model, root) = ParseWithCompilation([OpenGenericImplementationPath]);
        var extractor = CreateExtractor();

        var edges = extractor.Extract(model, root);

        var typeArgReference = edges.Any(e =>
            e.Type == "REFERENCES" &&
            e.Target.Id.Contains("Catalog") &&
            !e.Target.Id.Contains("IStore"));

        Assert.IsTrue(typeArgReference,
            $"Expected a REFERENCES edge to the type argument Catalog. Edges found: [{string.Join(", ", edges.Where(e => e.Type == "REFERENCES").Select(e => $"{e.Source.Id} --{e.Type}--> {e.Target.Id}"))}]");
    }
}
