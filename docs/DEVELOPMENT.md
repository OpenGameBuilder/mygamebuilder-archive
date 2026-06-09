# Developer Setup

This repo builds archive tooling for the preserved MyGameBuilder snapshot. It currently includes two console app skeletons: one for S3 content archiving and one for frontend/client snapshot archiving.

## Requirements

- [.NET SDK 10.0.300+](https://dotnet.microsoft.com/download), matching [`../global.json`](../global.json).
- [Visual Studio 2026+](https://visualstudio.microsoft.com/vs/) with .NET desktop development tools, or [Visual Studio Code](https://code.visualstudio.com/) with the recommended extensions.
- Git for Windows if you want the Husky.NET pre-commit hook to run locally.

Visual Studio can install the required workload from [`../.vsconfig`](../.vsconfig) when you open the solution.

## Command Line

From the repository root:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-archive.slnx
dotnet build mygamebuilder-archive.slnx
dotnet run --project src/MyGameBuilder.Archive.S3
dotnet run --project src/MyGameBuilder.Archive.Frontend
```

## Visual Studio

1. Open [`../mygamebuilder-archive.slnx`](../mygamebuilder-archive.slnx).
2. Let Visual Studio install missing components from [`../.vsconfig`](../.vsconfig) if prompted.
3. Set `MyGameBuilder.Archive.S3` or `MyGameBuilder.Archive.Frontend` as the startup project.
4. Press F5.

## VS Code

1. Open the repository folder in VS Code.
2. Install the recommended extensions from [`../.vscode/extensions.json`](../.vscode/extensions.json).
3. Press F5 and choose either `Launch S3 Archiver` or `Launch Frontend Archiver`.

Useful tasks are available from **Terminal: Run Task**:

- `build-s3`
- `build-frontend`
- `build-all`
- `clean`

## Local Archive Data

The full archive is intentionally not stored in git. Place local working archive inputs in `archive/` at the repository root when future tooling needs real data. Build outputs and manually prepared archive packages belong in `artifacts/`.
