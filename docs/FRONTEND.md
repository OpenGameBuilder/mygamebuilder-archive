# Frontend Wayback Archive

`MyGameBuilder.Archive.Frontend` captures Wayback Machine CDX records and raw
replay responses into a SQLite database designed for historical serving.

## Capture

Create a seed file:

```text
domain mygamebuilder.com
prefix https://s3.amazonaws.com/apphost/
exclude https://mygamebuilder.com/forum/
```

Run the archiver:

```pwsh
dotnet run --project src/MyGameBuilder.Archive.Frontend -- capture --seeds seeds.txt --output archive-work/frontend.sqlite --resume
```

Seed lines are:

- `domain <host>` - CDX domain search, e.g. `domain mygamebuilder.com`.
- `prefix <absolute-url>` - CDX prefix search, e.g. `prefix https://s3.amazonaws.com/apphost/`.
- `url <absolute-url>` - exact URL search.
- `exclude <absolute-url-prefix>` or `exclude-prefix <absolute-url-prefix>` - skip matching CDX captures after enumeration. Excludes compare by canonical URL and by host/path, so `https://mygamebuilder.com/forum/` also excludes historical `http://mygamebuilder.com/forum/...` captures.

The archive stores every accepted CDX capture row. Replay downloads use raw
Wayback URLs in the form:

```text
https://web.archive.org/web/{timestamp}id_/{original-url}
```

Replay status, headers, bodies, and replay errors are stored. Bodies are deduped
in `frontend_content` by SHA-256, while every timestamped capture remains in
`frontend_capture`.

## Historical Serving Lookup

For a request URL, canonicalize it the same way the archiver does: lower-case
scheme/host and remove default ports. Then select the newest capture at or
before the desired Wayback timestamp:

```sql
SELECT
    c.capture_id,
    c.capture_timestamp,
    c.replay_status_code,
    c.replay_reason_phrase,
    c.replay_error,
    b.body
FROM v_frontend_capture_lookup c
LEFT JOIN frontend_content b ON b.content_id = c.content_id
WHERE c.canonical_url = $canonical_url
  AND c.capture_timestamp <= $timestamp
ORDER BY c.capture_timestamp DESC, c.capture_id DESC
LIMIT 1;
```

Then fetch headers for the selected capture:

```sql
SELECT name, value
FROM frontend_response_header
WHERE capture_id = $capture_id
ORDER BY header_order;
```

If no row is returned, the archive has no known capture at or before that time.
If `replay_error` is non-null, CDX metadata exists but the raw replay body was
not available to the archiver.

## URL Discovery

Every downloaded body is scanned for URL-like strings, including comments,
escaped JavaScript strings, CSS references, relative paths, and printable runs
inside binary files. Review unique discovered URLs with:

```pwsh
dotnet run --project src/MyGameBuilder.Archive.Frontend -- export-urls --database archive-work/frontend.sqlite --output archive-work/discovered-urls.json
```

Use the exported list to create a follow-up seed file for the next manual pass.
