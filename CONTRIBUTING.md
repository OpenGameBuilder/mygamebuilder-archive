# Contributing to MyGameBuilder Archive

Thank you for helping with MyGameBuilder Archive. This project supports preservation tooling and documentation for the legacy MyGameBuilder S3 archive.

Useful contributions include code, tests, documentation, setup fixes, etc.

## Development

Development setup instructions are in [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md).

Before opening a pull request, run the relevant checks when practical:

```pwsh
dotnet build mygamebuilder-archive.slnx
```

The repo includes `.editorconfig` and a Husky.NET pre-commit hook for formatting staged C# files.

## Archive Work

Archive contributions should describe observed data, metadata, request/response shapes, file formats, or reproducible behavior. Do not copy, translate, port, adapt, or mechanically rewrite decompiled source code from the original MyGameBuilder Flash client or any other proprietary source.

Public issues and pull requests should not include passwords, private information, sensitive archival material, security vulnerability details, archived private game dumps, original proprietary assets, or decompiled source code.

## Pull Requests

Good pull requests are focused and easy to review. Include a summary, why the change is needed, how it was tested, and any archive-format or compatibility tradeoffs.

Documentation updates are especially helpful when behavior changes or a new setup step is introduced.
