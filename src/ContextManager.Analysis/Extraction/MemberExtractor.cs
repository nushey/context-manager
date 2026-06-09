using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

public static class MemberExtractor
{
    public static TypeInfo Build(EnumDeclarationSyntax node, bool isTopLevel)
    {
        var name = node.Identifier.ValueText;
        var access = AccessLevel.FromModifiers(node.Modifiers, isTopLevel);
        var attributes = NullIfEmpty(AttributeExtractor.Render(node.AttributeLists));
        var members = node.Members.Select(m => m.Identifier.ValueText).ToList();

        return new TypeInfo(
            Name: name,
            Kind: "enum",
            Access: access,
            Base: null,
            Implements: null,
            Attributes: attributes,
            ConstructorDependencies: null,
            Methods: null,
            Properties: null,
            Members: members);
    }

    public static TypeInfo Build(DelegateDeclarationSyntax node, bool isTopLevel)
    {
        var name = node.Identifier.ValueText;
        var access = AccessLevel.FromModifiers(node.Modifiers, isTopLevel);
        var attributes = NullIfEmpty(AttributeExtractor.Render(node.AttributeLists));
        var parameters = NullIfEmpty(node.ParameterList.Parameters
            .Select(p => new Models.ParameterInfo(
                p.Type?.ToString() ?? string.Empty,
                p.Identifier.ValueText))
            .ToList());

        var delegateSpan = node.GetLocation().GetLineSpan();
        var syntheticMethod = new Models.MethodInfo(
            Name: name,
            Access: access,
            ReturnType: node.ReturnType.ToString(),
            StartLine: delegateSpan.StartLinePosition.Line + 1,
            EndLine: delegateSpan.EndLinePosition.Line + 1,
            Parameters: parameters,
            Attributes: null);

        return new TypeInfo(
            Name: name,
            Kind: "delegate",
            Access: access,
            Base: null,
            Implements: null,
            Attributes: attributes,
            ConstructorDependencies: null,
            Methods: [syntheticMethod],
            Properties: null,
            Members: null);
    }


    // Single rendering of a type's display name (identifier + type parameter list);
    // CrossReferenceResolver must key its lookups with the exact same rendering.
    public static string RenderTypeName(TypeDeclarationSyntax node)
        => node.Identifier.ValueText + node.TypeParameterList?.ToString();

    public static TypeInfo Build(TypeDeclarationSyntax node, bool isTopLevel)
    {
        var bareName = node.Identifier.ValueText;
        var name = RenderTypeName(node);
        var access = AccessLevel.FromModifiers(node.Modifiers, isTopLevel);
        var attributes = AttributeExtractor.Render(node.AttributeLists);

        var kind = node switch
        {
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax r when r.ClassOrStructKeyword.ValueText == "struct" => "record",
            RecordDeclarationSyntax => "record",
            StructDeclarationSyntax => "struct",
            ClassDeclarationSyntax c when c.Modifiers.Any(m => m.ValueText == "static") => "static-class",
            ClassDeclarationSyntax c when c.Modifiers.Any(m => m.ValueText == "abstract") => "abstract-class",
            _ => "class"
        };

        var (baseType, implements) = ExtractBaseList(node);

        var constructorDeps = NullIfEmpty(ExtractConstructorDependencies(node));
        var methods = NullIfEmpty(ExtractMethods(node));
        var properties = NullIfEmpty(ExtractProperties(node));
        var genericConstraints = NullIfEmpty(
            node.ConstraintClauses
                .Select(c => c.ToString())
                .ToList());

        // Only apply the DTO heuristic to plain classes and structs; records, interfaces,
        // static and abstract classes keep their kind. Suffix matching needs the bare
        // identifier — "PagedResponse<T>" must still end with "Response".
        if (kind is "class" or "struct" && DtoDetector.IsDto(node, bareName))
        {
            kind = "dto";
            properties = null;
        }

        bool? isPartial = node.Modifiers.Any(m => m.ValueText == "partial") ? true : null;

        return new TypeInfo(
            Name: name,
            Kind: kind,
            Access: access,
            Base: baseType,
            Implements: NullIfEmpty(implements),
            Attributes: NullIfEmpty(attributes),
            ConstructorDependencies: constructorDeps,
            Methods: methods,
            Properties: properties,
            Members: null,
            IsPartial: isPartial,
            GenericConstraints: genericConstraints);
    }

    public static TypeInfo Build(RecordDeclarationSyntax node, bool isTopLevel)
        => Build((TypeDeclarationSyntax)node, isTopLevel);

    private static (string? baseType, IReadOnlyList<string> implements) ExtractBaseList(TypeDeclarationSyntax node)
    {
        if (node.BaseList is null)
            return (null, []);

        var entries = node.BaseList.Types
            .OfType<SimpleBaseTypeSyntax>()
            .Select(t => t.Type.ToString())
            .ToList();

        if (entries.Count == 0)
            return (null, []);

        // For classes: heuristic — first entry is base class if it doesn't start with 'I'
        if (node is ClassDeclarationSyntax && !entries[0].StartsWith("I", StringComparison.Ordinal))
        {
            var baseClass = entries[0];
            var ifaces = entries.Skip(1).ToList();
            return (baseClass, ifaces);
        }

        // Interfaces, structs, and classes whose first entry starts with 'I' → all are implements
        return (null, entries);
    }

    private static IReadOnlyList<Models.ParameterInfo> ExtractConstructorDependencies(TypeDeclarationSyntax node)
    {
        var parameters = ConstructorParameterLocator.Locate(node);
        return parameters is null ? [] : MapParameters(parameters.Value);
    }

    private static IReadOnlyList<Models.ParameterInfo> MapParameters(
        Microsoft.CodeAnalysis.SeparatedSyntaxList<ParameterSyntax> parameters)
        => parameters
            .Select(p => new Models.ParameterInfo(
                p.Type?.ToString() ?? string.Empty,
                p.Identifier.ValueText))
            .ToList();

    private static IReadOnlyList<Models.MethodInfo> ExtractMethods(TypeDeclarationSyntax node)
    {
        var result = new List<Models.MethodInfo>();
        bool isInterface = node is InterfaceDeclarationSyntax;

        foreach (var method in node.Members.OfType<MethodDeclarationSyntax>())
        {
            // Interface methods with no explicit modifier are implicitly public
            string methodAccess;
            if (isInterface && !method.Modifiers.Any())
                methodAccess = "public";
            else
                methodAccess = AccessLevel.FromModifiers(method.Modifiers, isTopLevelType: false);

            if (methodAccess == "private")
                continue;

            var parameters = NullIfEmpty(MapParameters(method.ParameterList.Parameters));
            var methodAttrs = NullIfEmpty(AttributeExtractor.Render(method.AttributeLists));
            var lineSpan = method.GetLocation().GetLineSpan();
            var genericConstraints = NullIfEmpty(
                method.ConstraintClauses
                    .Select(c => c.ToString())
                    .ToList());
            var modifiers = NullIfEmpty(ExtractNonAccessModifiers(method.Modifiers));

            result.Add(new Models.MethodInfo(
                Name: method.Identifier.ValueText,
                Access: methodAccess,
                ReturnType: method.ReturnType.ToString(),
                StartLine: lineSpan.StartLinePosition.Line + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                Parameters: parameters,
                Attributes: methodAttrs,
                GenericConstraints: genericConstraints,
                Modifiers: modifiers));
        }

        return result;
    }

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> list) => list.Count == 0 ? null : list;

    private static IReadOnlyList<string> ExtractNonAccessModifiers(Microsoft.CodeAnalysis.SyntaxTokenList modifiers)
        => modifiers
            .Where(m => m.ValueText is not ("public" or "protected" or "internal" or "private"))
            .Select(m => m.ValueText)
            .ToList();

    private static string? RenderAccessors(PropertyDeclarationSyntax prop)
    {
        if (prop.ExpressionBody is not null)
            return "get;";

        if (prop.AccessorList is null)
            return null;

        var rendered = prop.AccessorList.Accessors.Select(a =>
        {
            var mods = string.Join(" ", a.Modifiers.Select(m => m.ValueText));
            return mods.Length > 0 ? $"{mods} {a.Keyword.ValueText};" : $"{a.Keyword.ValueText};";
        });

        return string.Join(" ", rendered);
    }

    private static IReadOnlyList<Models.PropertyInfo> ExtractProperties(TypeDeclarationSyntax node)
    {
        var result = new List<Models.PropertyInfo>();

        foreach (var prop in node.Members.OfType<PropertyDeclarationSyntax>())
        {
            var propAccess = AccessLevel.FromModifiers(prop.Modifiers, isTopLevelType: false);
            if (propAccess == "private")
                continue;

            bool? isRequired = prop.Modifiers.Any(m => m.ValueText == "required") ? true : null;

            result.Add(new Models.PropertyInfo(
                Name: prop.Identifier.ValueText,
                Type: prop.Type.ToString(),
                Access: propAccess,
                IsRequired: isRequired,
                Accessors: RenderAccessors(prop)));
        }

        foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
        {
            bool isPublic = field.Modifiers.Any(m => m.ValueText == "public");
            bool isConst = field.Modifiers.Any(m => m.ValueText == "const");
            bool isStatic = field.Modifiers.Any(m => m.ValueText == "static");
            bool isReadonly = field.Modifiers.Any(m => m.ValueText == "readonly");

            if (!isPublic)
                continue;

            if (!isConst && !(isStatic && isReadonly))
                continue;

            var fieldAccess = AccessLevel.FromModifiers(field.Modifiers, isTopLevelType: false);
            var typeName = field.Declaration.Type.ToString();

            foreach (var variable in field.Declaration.Variables)
            {
                result.Add(new Models.PropertyInfo(
                    Name: variable.Identifier.ValueText,
                    Type: typeName,
                    Access: fieldAccess));
            }
        }

        return result;
    }
}
