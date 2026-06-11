# S3 Archive Redaction Review

`MyGameBuilder.Archive.S3.Redactor` is a local web app for manually reviewing PNG objects that may contain identifiable photographs of real people. It produces a new SQLite archive database, leaving the source database unchanged.

## Run

From the repository root:

```pwsh
dotnet run --project src/MyGameBuilder.Archive.S3.Redactor -- --archive archive-work/JGI_test1.sqlite
```

Optional arguments:

```pwsh
--review <path>                  # sidecar review DB; defaults beside the archive
--output <path>                  # redacted output DB; defaults beside the archive
--unique-color-threshold <count> # defaults to 100
```

## Review Workflow

The app scans live PNG entries in the archive in the background and excludes objects whose MGB piece type is `screenshot`. Candidates must have at least the configured number of visible unique colors. Fully transparent pixels are counted as a single transparent color, regardless of their hidden RGB values.

You can start reviewing as soon as the first matching image appears. Newly discovered candidates are appended to the end of the review list in archive entry order while the scanner continues. The UI shows both review counts and source PNG scan progress.

Each candidate is shown at original size, 2x size, and 512 x 512 with nearest-neighbor scaling. Use the on-screen buttons or keyboard:

- `Enter`: approve the image for inclusion.
- `Backspace`: redact the image.
- `Left Arrow`: move to the previous image without changing its status.
- `Right Arrow`: move to the next image without changing its status.

The review is resumable. Source scan progress, candidate metadata, decisions, and current position are committed to the sidecar SQLite database as you work. Restart the app with the same `--archive` and `--review` paths to continue scanning and reviewing.

## Submit

After the background scan is complete and every candidate has been approved or redacted, submit the review. The app creates a SQLite backup of the source archive, writes changes to a temporary output database, and moves it to the requested output path only after the redaction pass succeeds.

Manually redacted PNG bodies are replaced with opaque black PNGs of the same pixel dimensions. The output DB updates `s3_entry.body`, `content_length_bytes`, and `body_sha256`; source provenance such as `etag`, `last_modified_utc`, and captured response headers is intentionally preserved.

## Screenshot Propagation

Screenshots are not reviewed manually, but the submit step applies the archive policy from [`S3_ARCHIVE.md`](./S3_ARCHIVE.md):

```text
redacted tile -> actors whose animationTable references it -> maps whose layers reference those actors -> screenshots of those maps
```

Affected screenshots are also replaced with same-size black PNGs in the output database.

## Notes

The unique-color heuristic is intentionally simple. It matched the original manual review process well because tiles were small and photo-like uploads tended to exceed 100 visible colors, while heavily edited, low-detail, or transparent artwork generally did not.
