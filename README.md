# MyGameBuilder Archive

[![CI](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml)

MyGameBuilder Archive contains console archiver projects and documentation for producing the preserved MyGameBuilder archive artifacts. The archives themselves are treated as large, manually published artifacts; this repository keeps the code skeleton, formats, and development workflow needed to build the archivers.

## What Is Included

- `MyGameBuilder.Archive.S3`, a console app skeleton for the S3 content archiver.
- `MyGameBuilder.Archive.Frontend`, a console app skeleton for the frontend/client snapshot archiver.
- Visual Studio and VS Code setup for a clean checkout.
- Archive format documentation preserved from the original snapshot notes.

## Getting Started

Install the .NET SDK version from [`global.json`](global.json), then run:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-archive.slnx
dotnet build mygamebuilder-archive.slnx
dotnet run --project src/MyGameBuilder.Archive.S3
dotnet run --project src/MyGameBuilder.Archive.Frontend
```

For editor-specific setup, see [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## Archive Documentation

- [`docs/ARCHIVE.md`](docs/ARCHIVE.md) - archive provenance, layout, sidecar files, and client snapshot notes.
- [`docs/FORMATS.md`](docs/FORMATS.md) - byte-level formats for tiles, actors, maps, screenshots, tutorials, and profiles.

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before submitting compatibility or archival work, and report security issues privately as described in [`SECURITY.md`](SECURITY.md).
