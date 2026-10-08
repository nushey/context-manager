using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class MsBuildBootstrapProcessTests
{
    [TestMethod]
    public async Task DiagnoseMsBuild_IsolatedProcess_ReportsSelectedAndLoadedIdentityOnStderr()
    {
        var result = await RunDiagnosticAsync();

        Assert.AreEqual(0, result.ExitCode, result.Stderr);
        Assert.AreEqual(string.Empty, result.Stdout);
        StringAssert.Contains(result.Stderr, "selected=");
        StringAssert.Contains(result.Stderr, "loaded=[");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDiagnosticAsync()
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
        startInfo.ArgumentList.Add("--diagnose-msbuild");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start MCP diagnostic process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
