# Dispatcher contracts without starting Unity

These tests compile the production dispatcher with simulated Editor update/thread boundaries.
They check cancellation before/after dispatch, background API access, and async cleanup. They do
not establish Unity focus, domain reload, or native test-job runtime behavior.

Use an installed Newtonsoft.Json assembly (the Unity compile-check workflow extracts one into
`.compile-refs/`); no new package dependency is installed:

```powershell
dotnet build tools/dispatch-contracts/DispatchContracts.csproj --configuration Release `
  -p:NewtonsoftJsonPath="$PWD/.compile-refs/Newtonsoft.Json.dll" `
  -p:BaseIntermediateOutputPath="$PWD/.worktrees/dispatch-obj/" `
  -p:OutputPath="$PWD/.worktrees/dispatch-bin/"
dotnet .worktrees/dispatch-bin/DispatchContracts.dll
```

Run build and execution separately; a failed build must not execute an older output assembly.
