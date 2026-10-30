using System.Text;
using Microsoft.CodeAnalysis;

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
    }
}
