# Finding: `project_scan` fails on .NET Framework solutions with Visual Studio 2026 18.10

Date: 2026-10-08
Branch: `fix/roslyn-msbuild-18-buildhost` (commit `e37c1d5`, not pushed, not released)

## Error

`project_scan` on `Zureo.Online.sln` (55 projects, .NET Framework 4.8) returned:

```
{"code":"scan_failed","message":"phase=solution_evaluation_and_extraction;
Microsoft.CodeAnalysis.MSBuild.RemoteInvocationException: An exception of type
System.TypeInitializationException was thrown: Se produjo una excepción en el
inicializador de tipo de 'Microsoft.Build.Shared.XMakeElements'."}
```

- Reproducible in this repo: `ProjectScanToolTests.ProjectScanAsync_MixedLegacyAndMissingTargets_ReportsPartialCoverage` failed with the same exception (legacy fixture `LegacyProject.csproj`, `v4.8`).
- Same symptom recorded on 2026-09-08 in `.spec/graph-correctness-compatibility/plan.md`, where the root cause was left unestablished.
- Last successful scan of Zureo: 2026-08-13. Visual Studio 2026 Community was updated to 18.10 on 2026-09-23.

## Root cause

Roslyn's `MSBuildWorkspace` loads .NET Framework projects out of process in `BuildHost-net472`, which locates and loads the **newest Visual Studio MSBuild** (here VS 2026, MSBuild `18.10.1`). That host ships its own copies of some dependencies:

| Assembly | BuildHost-net472 (Roslyn 5.3.0) | VS 2026 18.10 MSBuild | VS 2022 17.14 MSBuild | BuildHost-net472 (Roslyn 5.9.0) |
|---|---|---|---|---|
| `System.Collections.Immutable` | 9.0.0.0 | 10.0.0.10 | 9.0.0.0 | not shipped |
| `System.Memory` | 4.0.2.0 | 4.0.5.0 | 4.0.2.0 | 4.0.5.0 (redirect `0.0.0.0-4.0.5.0`) |

MSBuild 18.10 needs `System.Collections.Immutable` 10.0. The BuildHost resolves its bundled 9.0 copy, so the static initializer of `Microsoft.Build.Shared.XMakeElements` fails. The exception crosses the RPC boundary as `RemoteInvocationException` with only type and message, so the remote stack is lost.

Ruled out (verified, no effect):

- Pinning the .NET SDK with `global.json` (10.0.303 instead of 10.0.401): the in-process locator is not involved in loading legacy projects.
- `MSBUILD_EXE_PATH` in the MCP client config (pointing to VS 2022 Build Tools): removing it does not change the result.

## Fix done

- `src/ContextManager.Analysis/ContextManager.Analysis.csproj`: `Microsoft.CodeAnalysis.CSharp`, `.CSharp.Workspaces`, `.Workspaces.MSBuild` 5.3.0 → 5.9.0.
- `tests/ContextManager.Analysis.Tests/ContextManager.Analysis.Tests.csproj`: `Microsoft.CodeAnalysis.CSharp` 5.3.0 → 5.9.0.

Verification on this machine (VS 2022 17.14 + VS 2026 18.10, SDKs 8.0.425 / 9.0.315 / 10.0.303 / 10.0.401):

- The previously failing legacy test passes.
- Full suite: 301/301 passed.
- Local Debug build used as MCP server: `project_scan` on `Zureo.Online.sln` returned `Scan partial. 12366 nodes, 29297 edges. Coverage: 54/55 projects, 987/1041 documents, 1 unsupported projects` (the unsupported one is a VB.NET project; 27 warnings "project reference without matching metadata reference").

Local environment changes outside this repo (to undo after releasing):

- `~/.claude.json` → `context-manager.command` points to `src\ContextManager.Mcp\bin\Debug\net10.0\ContextManager.Mcp.exe`. Restore `"command": "context-manager"` after `dotnet tool update -g ContextManager`.
- `MSBUILD_EXE_PATH` was removed from that entry's `env`.

Pending: release via `scripts/release.sh patch` (needs `NUGET_API_KEY` and push access; not run on this device).

## Review plan: remaining blind spots

Ordered by risk. Each item states what to check, why, and how to verify.

1. **Roslyn 5.9.0 on machines with only VS 2022 17.x.**
   - Why: the 5.9.0 BuildHost no longer ships `System.Collections.Immutable` and relies on the one next to MSBuild (9.0 in VS 17.14). If the BuildHost itself was compiled against 10.0, legacy scans break on VS 2022-only machines, the mirror image of this bug. Not verified: this machine always selects VS 2026.
   - Verify: run `ProjectScanAsync_MixedLegacyAndMissingTargets_ReportsPartialCoverage` on a machine (or VM / CI runner) with only VS 2022 17.14 / Build Tools 2022 installed. Also inspect the BuildHost's referenced assembly versions (`Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe` metadata) for `System.Collections.Immutable`.

2. **Diagnostics describe the wrong MSBuild for legacy projects.**
   - Why: stderr logs `selected=.NET Core SDK 10.0.401` (the in-process locator), but legacy projects are evaluated by the newest Visual Studio MSBuild inside `BuildHost-net472`, which is never reported. This misled both the 2026-09-08 diagnosis and this one (SDK pin attempt). `ProjectScanTool` walks `InnerException`, but `RemoteInvocationException` carries no remote inner exception or stack.
   - Verify: check whether Roslyn 5.9.0 exposes the BuildHost's chosen MSBuild path or remote stack (e.g. via the `msbuildLogger` parameter of `OpenSolutionAsync` or a binlog). If so, include it in the `scan_failed` diagnostics.

3. **Documentation overstates compatibility.**
   - Why: `INSTALL.md` / `README.md` state "MSBuild 17.x and 18.x are both supported". Compatibility depends on the Roslyn BuildHost version matching the newest installed VS minor; 18.10 broke with Roslyn 5.3.0. `CONTEXT_MANAGER_MSBUILD_PATH` and `MSBUILD_EXE_PATH` do not steer the BuildHost, so users have no documented override.
   - Verify: confirm whether any env var or `MSBuildWorkspace` property steers the BuildHost MSBuild selection; document the real behavior and the minimum Roslyn version per VS version.

4. **Silent breakage after VS updates.**
   - Why: the installed tool can break any time Visual Studio auto-updates, with no change to the tool. `release.sh` only tests against the publisher's VS.
   - Verify: decide whether a scheduled CI job against the latest VS image (running the legacy fixture test) is worth it, or whether a documented troubleshooting entry is enough.

5. **Package alignment after the bump.**
   - Why: `Microsoft.Build.Framework` / `Microsoft.NET.StringTools` stay at 18.6.3 (compile-time only, `ExcludeAssets="runtime"`) while Roslyn moved to 5.9.0. Restore produced no NU1608/NU1605 warnings here, but it was not checked explicitly.
   - Verify: `dotnet restore` + `dotnet list package --include-transitive` and look for version conflicts or downgrade warnings.
