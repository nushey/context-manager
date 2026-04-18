using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelTypeInfo = ContextManager.Analysis.Models.TypeInfo;

namespace ContextManager.Analysis.Extraction;

public sealed class TypeExtractor : CSharpSyntaxWalker
{
    private readonly List<ModelTypeInfo> _types = [];

    public IReadOnlyList<ModelTypeInfo> Types => _types;

    private static bool IsTopLevel(SyntaxNode node)
        => node.Parent is CompilationUnitSyntax
            or NamespaceDeclarationSyntax
            or FileScopedNamespaceDeclarationSyntax;

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ModelTypeInfo typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitClassDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitRecordDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitStructDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitEnumDeclaration(node);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var typeInfo = MemberExtractor.Build(node, IsTopLevel(node));
        if (typeInfo.Access != "private")
            _types.Add(typeInfo);
        base.VisitDelegateDeclaration(node);
    }
}
