using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class MethodSignatureFormatterTests
{
    [TestMethod]
    public void Format_MultipleParameters_ReturnsNameTypePairsCommaSeparated()
    {
        var method = new MethodInfo(
            Name: "DoThing",
            Access: "public",
            ReturnType: "bool",
            StartLine: 1,
            EndLine: 1,
            Parameters: [new ParameterInfo("string", "name"), new ParameterInfo("int", "count")],
            Attributes: null);

        var result = MethodSignatureFormatter.Format(method);

        Assert.AreEqual("DoThing(name:string, count:int): bool", result);
    }

    [TestMethod]
    [DataRow("protected")]
    [DataRow("internal")]
    [DataRow("protected internal")]
    public void Format_NonPublicMethod_PrefixesAccessLevel(string access)
    {
        var method = new MethodInfo(
            Name: "GetDimensionsAsync",
            Access: access,
            ReturnType: "Task<decimal>",
            StartLine: 1,
            EndLine: 1,
            Parameters: [new ParameterInfo("ShoppingCartItem", "item")],
            Attributes: null);

        var result = MethodSignatureFormatter.Format(method);

        Assert.AreEqual($"{access} GetDimensionsAsync(item:ShoppingCartItem): Task<decimal>", result);
    }

    [TestMethod]
    public void Format_NoParameters_ReturnsEmptyParens()
    {
        var method = new MethodInfo(
            Name: "GetAll",
            Access: "public",
            ReturnType: "IReadOnlyList<Order>",
            StartLine: 1,
            EndLine: 1,
            Parameters: null,
            Attributes: null);

        var result = MethodSignatureFormatter.Format(method);

        Assert.AreEqual("GetAll(): IReadOnlyList<Order>", result);
    }

    [TestMethod]
    public void Format_EmptyParameterList_ReturnsEmptyParens()
    {
        var method = new MethodInfo(
            Name: "Reset",
            Access: "public",
            ReturnType: "void",
            StartLine: 1,
            EndLine: 1,
            Parameters: [],
            Attributes: null);

        var result = MethodSignatureFormatter.Format(method);

        Assert.AreEqual("Reset(): void", result);
    }

    [TestMethod]
    public void Format_GenericReturnType_IsPreservedVerbatim()
    {
        var method = new MethodInfo(
            Name: "FindAll",
            Access: "public",
            ReturnType: "Task<IReadOnlyList<Order>>",
            StartLine: 1,
            EndLine: 1,
            Parameters: [new ParameterInfo("CancellationToken", "ct")],
            Attributes: null);

        var result = MethodSignatureFormatter.Format(method);

        Assert.AreEqual("FindAll(ct:CancellationToken): Task<IReadOnlyList<Order>>", result);
    }
}
