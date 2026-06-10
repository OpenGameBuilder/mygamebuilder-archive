# MyGameBuilder Archive

[![CI](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml)

MyGameBuilder Archive contains console archiver projects and documentation for producing the preserved MyGameBuilder archive artifacts. The archives themselves are treated as large, manually published artifacts; this repository keeps the code skeleton, formats, and development workflow needed to build the archivers.

## What Is Included

- `MyGameBuilder.Archive.S3`, a console app skeleton for the S3 content archiver.
- `MyGameBuilder.Archive.S3.Redactor`, a local web app for manually reviewing photo-like archive PNGs and producing a redacted SQLite archive copy.
- `MyGameBuilder.Archive.Frontend`, a console app for capturing Wayback/CDX frontend snapshots into a server-oriented SQLite archive.
- Visual Studio and VS Code setup for a clean checkout.
- Archive format documentation preserved from the original snapshot notes.

## Getting Started

Install the .NET SDK version from [`global.json`](global.json), then run:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-archive.slnx
dotnet build mygamebuilder-archive.slnx
dotnet run --project src/MyGameBuilder.Archive.S3
dotnet run --project src/MyGameBuilder.Archive.S3.Redactor -- --archive archive-work/JGI_test1.sqlite
dotnet run --project src/MyGameBuilder.Archive.Frontend -- capture --seeds seeds.txt --output archive-work/frontend.sqlite --resume
```

For editor-specific setup, see [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## Archive Documentation

- [`docs/ARCHIVE.md`](docs/ARCHIVE.md) - archive provenance, layout, sidecar files, and client snapshot notes.
- [`docs/FRONTEND.md`](docs/FRONTEND.md) - Wayback frontend archiver seed format, historical serving query, and URL discovery export.
- [`docs/FORMATS.md`](docs/FORMATS.md) - byte-level formats for tiles, actors, maps, screenshots, tutorials, and profiles.
- [`docs/REDACTION.md`](docs/REDACTION.md) - manual PNG review and redacted SQLite archive generation.

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before submitting compatibility or archival work, and report security issues privately as described in [`SECURITY.md`](SECURITY.md).
