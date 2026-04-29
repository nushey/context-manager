using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class MemberExtractorModernCSharpTests
{
    private static readonly string FixturePath = Path.Combine(
        Path.GetDirectoryName(typeof(MemberExtractorModernCSharpTests).Assembly.Location)!,
        @"Fixtures/ModernCSharpFeatures.cs");

    private static IReadOnlyList<MemberDeclarationSyntax> LoadFixtureMembers()
    {
        var text = File.ReadAllText(FixturePath);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        if (root.Members[0] is FileScopedNamespaceDeclarationSyntax fileNs)
            return fileNs.Members;

        if (root.Members[0] is NamespaceDeclarationSyntax blockNs)
            return blockNs.Members;

        return root.Members;
    }

    private static TypeDeclarationSyntax GetTypeByName(string name)
    {
        var members = LoadFixtureMembers();
        return members
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.ValueText == name);
    }

    private static RecordDeclarationSyntax GetRecordByName(string name)
    {
        var members = LoadFixtureMembers();
        return members
            .OfType<RecordDeclarationSyntax>()
            .First(r => r.Identifier.ValueText == name);
    }

    // ── partial class ─────────────────────────────────────────────────────────

    [TestMethod]
    public void PartialClass_IsPartialIsTrue()
    {
        var node = GetTypeByName("PartialOrderService");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(true, result.IsPartial);
    }

    [TestMethod]
    public void NonPartialClass_IsPartialIsNull()
    {
        var node = GetTypeByName("CustomerProfile");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.IsPartial);
    }

    // ── nullable properties and parameters ───────────────────────────────────

    [TestMethod]
    public void NullableProperty_TypePreservesQuestionMark()
    {
        var node = GetTypeByName("PartialOrderService");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var prop = result.Properties!.First(p => p.Name == "CustomerName");
        Assert.AreEqual("string?", prop.Type);
    }

    [TestMethod]
    public void NullableParameter_TypePreservesQuestionMark()
    {
        var node = GetTypeByName("PartialOrderService");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var method = result.Methods!.First(m => m.Name == "Process");
        var param = method.Parameters!.First();
        Assert.AreEqual("string?", param.Type);
    }

    // ── required properties ───────────────────────────────────────────────────

    [TestMethod]
    public void RequiredProperty_IsRequiredIsTrue()
    {
        var node = GetTypeByName("CustomerProfile");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var emailProp = result.Properties!.First(p => p.Name == "Email");
        Assert.AreEqual(true, emailProp.IsRequired);

        var nameProp = result.Properties!.First(p => p.Name == "FullName");
        Assert.AreEqual(true, nameProp.IsRequired);
    }

    [TestMethod]
    public void NonRequiredProperty_IsRequiredIsNull()
    {
        var node = GetTypeByName("CustomerProfile");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var phoneProp = result.Properties!.First(p => p.Name == "PhoneNumber");
        Assert.IsNull(phoneProp.IsRequired);
    }

    // ── generic constraints ───────────────────────────────────────────────────

    [TestMethod]
    public void MethodWithSingleConstraint_GenericConstraintsPopulated()
    {
        var node = GetTypeByName("GenericProcessor");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var convertMethod = result.Methods!.First(m => m.Name == "Convert");
        Assert.IsNotNull(convertMethod.GenericConstraints);
        Assert.AreEqual(1, convertMethod.GenericConstraints.Count);
        StringAssert.Contains(convertMethod.GenericConstraints[0], "where T");
    }

    [TestMethod]
    public void MethodWithMultipleConstraints_AllCaptured()
    {
        var node = GetTypeByName("GenericProcessor");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var mapMethod = result.Methods!.First(m => m.Name == "Map");
        Assert.IsNotNull(mapMethod.GenericConstraints);
        Assert.AreEqual(2, mapMethod.GenericConstraints.Count);
        Assert.IsTrue(mapMethod.GenericConstraints.Any(c => c.Contains("TSource")));
        Assert.IsTrue(mapMethod.GenericConstraints.Any(c => c.Contains("TResult")));
    }

    [TestMethod]
    public void MethodWithNoConstraints_GenericConstraintsIsNull()
    {
        var node = GetTypeByName("PartialOrderService");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var processMethod = result.Methods!.First(m => m.Name == "Process");
        Assert.IsNull(processMethod.GenericConstraints);
    }

    // ── record with primary constructor ───────────────────────────────────────

    [TestMethod]
    public void Record_KindIsRecord()
    {
        var node = GetRecordByName("OrderSummary");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("record", result.Kind);
    }

    [TestMethod]
    public void Record_PrimaryConstructorParametersInConstructorDependencies()
    {
        var node = GetRecordByName("OrderSummary");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNotNull(result.ConstructorDependencies);
        Assert.AreEqual(3, result.ConstructorDependencies.Count);
        Assert.AreEqual("string", result.ConstructorDependencies[0].Type);
        Assert.AreEqual("OrderId", result.ConstructorDependencies[0].Name);
        Assert.AreEqual("decimal", result.ConstructorDependencies[1].Type);
        Assert.AreEqual("Total", result.ConstructorDependencies[1].Name);
        Assert.AreEqual("string?", result.ConstructorDependencies[2].Type);
        Assert.AreEqual("Notes", result.ConstructorDependencies[2].Name);
    }
}
