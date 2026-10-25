Run the production `PidFileManager` against in-memory Unity persistence. This verifies
ownership decisions without launching Unity or reading/writing real EditorPrefs.
It does not establish live Editor or OS process cleanup behavior.

From the repository root, build and execute separately (Windows PowerShell):

```powershell
$ownershipRoot = (Get-Location).Path.Replace('\', '/')
dotnet build tools/ownership-contracts/OwnershipContracts.csproj --configuration Release "-p:BaseIntermediateOutputPath=$ownershipRoot/.worktrees/ownership-obj/" "-p:OutputPath=$ownershipRoot/.worktrees/ownership-bin/"
dotnet .worktrees/ownership-bin/OwnershipContracts.dll
```

Requires .NET SDK 8. No NuGet test framework or Unity installation is needed.
