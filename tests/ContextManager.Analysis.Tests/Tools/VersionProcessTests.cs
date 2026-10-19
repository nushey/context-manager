using System.Diagnostics;
using System.Reflection;
using ContextManager.Mcp.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class VersionProcessTests
{
    [TestMethod]
    [DataRow("--version")]
    [DataRow("-v")]
    public async Task VersionFlag_IsolatedProcess_OutputsCleanVersionToStdout(string flag)
    {
        var expected = typeof(SetupCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion
            .Split('+')[0];

        var result = await RunVersionAsync(flag);

        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual(string.Empty, result.Stderr);
        Assert.AreEqual(expected, result.Stdout.Trim());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunVersionAsync(string flag)
    {
        var serverAssembly = Path.Combine(AppContext.BaseDirectory, "ContextManager.Mcp.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssembly);
        startInfo.ArgumentList.Add(flag);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start MCP version process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
