using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelTypeInfo = ContextManager.Analysis.Models.TypeInfo;

namespace ContextManager.Analysis.Extraction;

public sealed class TypeExtractor(SemanticModel? model = null) : CSharpSyntaxWalker
{
    private readonly List<ModelTypeInfo> _types = [];

    public IReadOnlyList<ModelTypeInfo> Types => _types;

    public static string? NamespaceOf(CompilationUnitSyntax root) =>
        root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private static bool IsTopLevel(SyntaxNode node)
        => node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax;

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node), model));
        base.VisitClassDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node), model));
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node), model));
        base.VisitRecordDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node), model));
        base.VisitStructDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node)));
        base.VisitEnumDeclaration(node);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        Collect(MemberExtractor.Build(node, IsTopLevel(node)));
        base.VisitDelegateDeclaration(node);
    }

    private void Collect(ModelTypeInfo typeInfo)
    {
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
    }
}
