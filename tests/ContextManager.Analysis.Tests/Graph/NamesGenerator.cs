using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Tests.Graph;

[Generator]
public sealed class NamesGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = context.AdditionalTextsProvider
            .Where(text => Path.GetFileName(text.Path) == "names.txt")
            .Select((text, ct) => text.GetText(ct)?.ToString() ?? string.Empty);

        context.RegisterSourceOutput(names, (output, text) =>
        {
            var source = new StringBuilder("namespace IncrementalFixtures.Core;\n\npublic static class Names\n{\n");
            foreach (var name in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                source.Append($"    public static void {name}() {{ }}\n");

            output.AddSource("Names.g.cs", source.Append("}\n").ToString());
        });

        var callers = context.SyntaxProvider.CreateSyntaxProvider(
            (node, _) => node is ClassDeclarationSyntax { Identifier.ValueText: "ConfigCaller" },
            (syntax, _) => syntax.Node.SyntaxTree);
        var targets = callers.Combine(context.AnalyzerConfigOptionsProvider)
            .Select((input, _) => input.Right.GetOptions(input.Left).TryGetValue("cm_target_base", out var target) ? target : null)
            .Where(target => target is not null);

        context.RegisterSourceOutput(targets, (output, target) =>
            output.AddSource("ConfigTarget.g.cs", $"namespace IncrementalFixtures.Core; public class ConfigTarget : {target} {{ }}"));
    }
}
