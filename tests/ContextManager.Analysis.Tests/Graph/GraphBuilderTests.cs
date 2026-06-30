using ContextManager.Analysis.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class GraphBuilderTests
{
    // IsSourceDocument normalizes separators before the obj/bin check, so both `/` and `\`
    // forms are rejected. `.g.cs` filtering is separator-independent.

    [TestMethod]
    public void IsSourceDocument_ObjWithAltSeparator_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"/obj/foo.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_BinWithPlatformSeparator_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"\Bin\x.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_GeneratedCs_Rejected()
    {
        Assert.IsFalse(GraphBuilder.IsSourceDocument(@"Foo.g.cs"));
    }

    [TestMethod]
    public void IsSourceDocument_CleanSourcePath_Accepted()
    {
        Assert.IsTrue(GraphBuilder.IsSourceDocument(@"Src/Foo.cs"));
    }
}
