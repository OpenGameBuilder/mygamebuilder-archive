# MyGameBuilder Archive

[![CI](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml/badge.svg)](https://github.com/OpenGameBuilder/mygamebuilder-archive/actions/workflows/ci.yml)

MyGameBuilder Archive contains console archiver projects and documentation for producing preserved MyGameBuilder archive artifacts. The archives themselves are large, manually published SQLite databases; this repository keeps the tooling, source-format notes, and development workflow needed to build and validate them.

## What Is Included

- `OpenGameBuilder.Mgb.Archive.S3Archiver`, a console app for capturing the public `JGI_test1` S3 content archive into SQLite.
- `OpenGameBuilder.Mgb.Archive.S3Redactor`, a local web app for manually reviewing photo-like archive PNGs and producing a redacted SQLite archive copy.
- `OpenGameBuilder.Mgb.Archive.ClientArchiver`, a console app for capturing Wayback/CDX frontend snapshots into SQLite.
- Visual Studio and VS Code setup for a clean checkout.
- Archive documentation and source-format notes for the released SQLite artifacts.

## Getting Started

Install the .NET SDK version from [`global.json`](global.json), then run:

```pwsh
dotnet tool restore
dotnet restore mygamebuilder-archive.slnx
dotnet build mygamebuilder-archive.slnx
dotnet run --project src/OpenGameBuilder.Mgb.Archive.S3Archiver -- capture --bucket JGI_test1 --output archive-work/JGI_test1.sqlite --resume
dotnet run --project src/OpenGameBuilder.Mgb.Archive.S3Redactor -- --archive archive-work/JGI_test1.sqlite
dotnet run --project src/OpenGameBuilder.Mgb.Archive.ClientArchiver -- capture --seeds seeds.txt --output archive-work/frontend.sqlite --resume
dotnet run --project src/OpenGameBuilder.Mgb.Archive.S3Archiver -- split-file --input archive-work/archive.sqlite.zst
dotnet run --project src/OpenGameBuilder.Mgb.Archive.S3Archiver -- split-file --input archive-work/archive.sqlite --zstd
```

`split-file` performs byte-only chunking. By default it writes 1,900,000,000-byte parts named like
`archive.sqlite.zst.part-000`, `archive.sqlite.zst.part-001`, and so on. Pass `--zstd` when the
input is an uncompressed SQLite file and the command should compress while splitting. Use
`--output-prefix`, `--part-size-bytes`, or `--replace` when needed.

For editor-specific setup, see [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

## Archive Documentation

- [`docs/S3_ARCHIVE.md`](docs/S3_ARCHIVE.md) - user-facing S3 content archive provenance, SQLite shape, integrity, and redaction notes.
- [`docs/FRONTEND.md`](docs/FRONTEND.md) - user-facing Wayback frontend archive provenance, SQLite shape, capture behavior, and limitations.
- [`docs/FORMATS.md`](docs/FORMATS.md) - byte-level formats for tiles, actors, maps, screenshots, tutorials, and profiles.
- [`docs/REDACTION.md`](docs/REDACTION.md) - manual PNG review and redacted SQLite archive generation.

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before submitting compatibility or archival work, and report security issues privately as described in [`SECURITY.md`](SECURITY.md).
