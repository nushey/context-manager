using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeInfo = ContextManager.Analysis.Models.TypeInfo;

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

    public static string RenderTypeName(TypeDeclarationSyntax node)
        => node.Identifier.ValueText + node.TypeParameterList?.ToString();

    public static TypeInfo Build(TypeDeclarationSyntax node, bool isTopLevel, SemanticModel? model = null)
    {
        var name = RenderTypeName(node);
        var access = AccessLevel.FromModifiers(node.Modifiers, isTopLevel);
        var attributes = AttributeExtractor.Render(node.AttributeLists);

        var kind = node switch
        {
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            StructDeclarationSyntax => "struct",
            ClassDeclarationSyntax c when c.Modifiers.Any(m => m.ValueText == "static") => "static-class",
            ClassDeclarationSyntax c when c.Modifiers.Any(m => m.ValueText == "abstract") => "abstract-class",
            _ => "class"
        };

        var (baseType, implements) = ExtractBaseList(node, model);

        var constructorDeps = NullIfEmpty(ExtractConstructorDependencies(node));
        var methods = NullIfEmpty(ExtractMethods(node));
        var properties = NullIfEmpty(ExtractProperties(node));
        var events = NullIfEmpty(ExtractEvents(node));
        var genericConstraints = NullIfEmpty(
            node.ConstraintClauses
                .Select(c => c.ToString())
                .ToList());

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
            GenericConstraints: genericConstraints,
            Events: events);
    }

    private static (string? baseType, IReadOnlyList<string> implements) ExtractBaseList(TypeDeclarationSyntax node, SemanticModel? model)
    {
        var entries = node.BaseList?.Types.OfType<SimpleBaseTypeSyntax>().Select(t => t.Type).ToList() ?? [];
        if (entries.Count == 0)
            return (null, []);

        return IsBaseClass(node, entries[0], model)
            ? (entries[0].ToString(), entries.Skip(1).Select(t => t.ToString()).ToList())
            : (null, entries.Select(t => t.ToString()).ToList());
    }

    public static bool IsBaseClass(TypeDeclarationSyntax node, TypeSyntax firstBaseType, SemanticModel? model)
    {
        if (node is not (ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: not "struct" }))
            return false;

        if (model?.GetTypeInfo(firstBaseType).Type is { TypeKind: not TypeKind.Error } resolved)
            return resolved.TypeKind != TypeKind.Interface;

        var name = firstBaseType.ToString();
        return node is ClassDeclarationSyntax && !(name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]));
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

        foreach (var method in node.Members.OfType<MethodDeclarationSyntax>())
        {
            var explicitSpecifier = method.ExplicitInterfaceSpecifier;
            var methodAccess = AccessLevel.OfMember(method, explicitSpecifier);
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
                Name: explicitSpecifier is not null
                    ? $"{explicitSpecifier.Name}.{method.Identifier.ValueText}"
                    : method.Identifier.ValueText,
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

    private static IReadOnlyList<Models.PropertyInfo> ExtractEvents(TypeDeclarationSyntax node)
    {
        var result = new List<Models.PropertyInfo>();

        foreach (var member in node.Members)
        {
            if (member is EventFieldDeclarationSyntax eventField)
            {
                var access = AccessLevel.OfMember(eventField);
                if (access == "private")
                    continue;

                var typeName = eventField.Declaration.Type.ToString();
                foreach (var variable in eventField.Declaration.Variables)
                {
                    result.Add(new Models.PropertyInfo(
                        Name: variable.Identifier.ValueText,
                        Type: typeName,
                        Access: access));
                }
            }
            else if (member is EventDeclarationSyntax eventDecl)
            {
                var explicitSpecifier = eventDecl.ExplicitInterfaceSpecifier;
                var access = AccessLevel.OfMember(eventDecl, explicitSpecifier);
                if (access == "private")
                    continue;

                result.Add(new Models.PropertyInfo(
                    Name: explicitSpecifier is not null
                        ? $"{explicitSpecifier.Name}.{eventDecl.Identifier.ValueText}"
                        : eventDecl.Identifier.ValueText,
                    Type: eventDecl.Type.ToString(),
                    Access: access));
            }
        }

        return result;
    }

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

        if (node is RecordDeclarationSyntax { ParameterList: { } positionalParameters } record)
        {
            var declaredNames = node.Members.OfType<PropertyDeclarationSyntax>()
                .Select(p => p.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);
            var isMutableStruct = record.ClassOrStructKeyword.ValueText == "struct"
                && !record.Modifiers.Any(m => m.ValueText == "readonly");

            foreach (var parameter in positionalParameters.Parameters)
            {
                if (parameter.Type is null || declaredNames.Contains(parameter.Identifier.ValueText))
                    continue;

                result.Add(new Models.PropertyInfo(
                    Name: parameter.Identifier.ValueText,
                    Type: parameter.Type.ToString(),
                    Access: "public",
                    Accessors: isMutableStruct ? "get; set;" : "get; init;"));
            }
        }

        foreach (var prop in node.Members.OfType<PropertyDeclarationSyntax>())
        {
            var explicitSpecifier = prop.ExplicitInterfaceSpecifier;
            var propAccess = AccessLevel.OfMember(prop, explicitSpecifier);
            if (propAccess == "private")
                continue;

            bool? isRequired = prop.Modifiers.Any(m => m.ValueText == "required") ? true : null;

            result.Add(new Models.PropertyInfo(
                Name: explicitSpecifier is not null
                    ? $"{explicitSpecifier.Name}.{prop.Identifier.ValueText}"
                    : prop.Identifier.ValueText,
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

            var typeName = field.Declaration.Type.ToString();

            foreach (var variable in field.Declaration.Variables)
            {
                result.Add(new Models.PropertyInfo(
                    Name: variable.Identifier.ValueText,
                    Type: typeName,
                    Access: "public"));
            }
        }

        return result;
    }
}
