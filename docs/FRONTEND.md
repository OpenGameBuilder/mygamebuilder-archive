# MyGameBuilder Frontend Archive

The frontend archive preserves historical public web files for MyGameBuilder as
captured by the Internet Archive Wayback Machine, plus current live captures for
explicit URL seeds and URLs discovered in downloaded bodies. It is distributed
as a standalone SQLite database of timestamped captures, not as a cleaned static
website folder.

This archive is separate from the S3 content archive. The S3 archive preserves
user-generated game pieces from `JGI_test1`; the frontend archive preserves the
website and client-facing files that could be recovered through Wayback.

## What Was Archived

The archive is seeded from public URLs and URL scopes related to the original
MyGameBuilder web frontend, including the public website and the Flash client
hosted under `https://s3.amazonaws.com/apphost/`.

Depending on the seed file used for a release, captured resources may include:

- Marketing or landing pages from `mygamebuilder.com`.
- The Flash embedding page.
- The original `MGB.swf` client file.
- JavaScript, CSS, images, music, and other assets referenced by captured
  pages or binaries.
- Historical redirects and non-200 responses when Wayback reported them for a
  resource that also had at least one non-error CDX capture.
- Current live responses for non-excluded raw `url` seeds and non-excluded URLs
  discovered while downloading archived bodies.

The archive stores timestamped captures. A single URL can have many captured
versions, and several captures can point at the same deduplicated body.

## Provenance

The primary source is the Internet Archive Wayback Machine:

- CDX metadata is enumerated through the Wayback CDX API.
- Replay bodies are downloaded through raw `id_` replay URLs in this form:

```text
https://web.archive.org/web/{timestamp}id_/{original-url}
```

The database records the CDX endpoint, Wayback replay endpoint, seed file path,
seed file SHA-256, seed rows, excluded URL rules, capture counts, replay error
counts, and creation time in `archive_info`.

Each CDX row is stored with its original URL, timestamp, MIME type, status code,
digest, length, redirect target, and raw JSON row. Each replay attempt records
the replay URL, replay status, headers, body hash, and body bytes when the body
was available.

After Wayback replay downloads complete, the archiver performs a direct live
capture pass for every raw `url` seed and every non-excluded URL discovered in
successfully downloaded replay bodies. These live captures use the current UTC
timestamp at archive creation time, store the final downloaded body, and preserve
the live response URL, status, headers, hash, and bytes in the same replay
fields used for Wayback captures.

## How It Was Archived

The capture process is intentionally conservative:

1. Read a seed file containing domains, URL prefixes, exact URLs, and optional
   exclude rules.
2. Query CDX for every seed, paging with Wayback resume keys until each seed is
   complete.
3. Store every accepted CDX row. Excluded rows are skipped, and the exclude
   rules are kept in the database for transparency.
4. Drop resources whose CDX captures all have HTTP error status codes, such as
   404 or 500. Resources with at least one non-error CDX capture are kept.
5. Download each accepted capture through raw Wayback replay.
6. Store replay headers and bodies. Bodies are deduplicated by SHA-256, but
   every timestamped capture remains present.
7. Scan successful replay bodies for linked URLs and apphost asset references.
8. Directly capture every non-excluded discovered URL, plus every non-excluded
   raw `url` seed, from the live web as it exists during the run.
9. Validate the database and finalize it as a single read-only SQLite file.

Excludes are prefix-based and compare both canonical URL form and host/path
form, so an exclude such as `https://mygamebuilder.com/forum/` also excludes
matching historical `http://mygamebuilder.com/forum/...` captures.
Excludes are applied to both the captured original URL and any CDX redirect
target, so a capture for an allowed URL is skipped if Wayback reports that it
redirected into an excluded scope.
Replay downloads do not follow HTTP redirects; if the replay response itself
redirects into an excluded scope, that capture is skipped too. Direct live
captures follow allowed redirects to the final live response, but stop and skip
the capture if any redirect target enters an excluded scope.
Use `exclude https://v2.mygamebuilder.com/` to skip a whole subdomain. Use
`exclude-contains forum` to skip any CDX URL containing `forum`, regardless of
where that text appears in the URL.

Canonical resource identity treats `http` and `https` as equivalent and also
treats a leading `www.` host label as equivalent to the bare host. For example,
`https://www.mygamebuilder.com/play/` and `http://mygamebuilder.com/play/`
share one canonical resource key.

## The SQLite Archive

Important tables and views:

- `archive_info` records provenance, counts, endpoints, seed identity, and
  schema identity.
- `frontend_seed` records every seed line and its generated CDX query.
- `frontend_exclude` records excluded URL rules.
- `frontend_resource` records canonical URLs. Canonicalization lowercases
  hosts, normalizes `http` and `https` to one identity, strips a leading `www.`,
  removes default ports, and drops fragments.
- `frontend_capture` records each timestamped CDX capture and replay result.
- `frontend_content` stores deduplicated replay body bytes by SHA-256.
- `frontend_response_header` preserves replay response headers in order.
- `v_frontend_capture_lookup` joins resource identity with capture metadata for
  historical lookup.

The archive schema is identified by:

```text
archive_info.schema = mgb-frontend-wayback-archive
archive_info.schema_version = 2
```

## Opening And Browsing

Any SQLite browser can inspect the archive. Open it read-only when possible.

Start with archive metadata:

```sql
SELECT name, value
FROM archive_info
ORDER BY name;
```

List resources with capture counts:

```sql
SELECT r.canonical_url, COUNT(*) AS captures
FROM frontend_resource r
JOIN frontend_capture c ON c.resource_id = r.resource_id
GROUP BY r.resource_id
ORDER BY r.canonical_url;
```

Find the best capture for a URL at or before a historical timestamp:

```sql
SELECT
    c.capture_id,
    c.capture_timestamp,
    c.original_url,
    c.replay_status_code,
    c.replay_reason_phrase,
    c.replay_error,
    c.content_id
FROM v_frontend_capture_lookup c
WHERE c.canonical_url = 'http://mygamebuilder.com/'
  AND c.capture_timestamp <= '20120101000000'
ORDER BY c.capture_timestamp DESC, c.capture_id DESC
LIMIT 1;
```

If `content_id` is present, the body is in `frontend_content`. If
`replay_error` is present, Wayback had CDX metadata for the capture but the
archiver could not retrieve a body for that replay.

## Modifications

The frontend archive does not rewrite downloaded bodies. It does not strip
Wayback markers, fix links, rename files, or convert the capture into a static
site. The body stored in `frontend_content` is the byte sequence returned by the
Wayback raw replay request at capture time.

The only derived data added by the archiver is metadata around the capture:
canonical URLs, replay hashes, replay headers, and deduplicated content
references.

## Limitations

Wayback is an archival source, not the original production server. A missing
capture, failed replay, or replay response that differs from the historical live
site can only be recorded as-is. CDX metadata and replay status can also differ;
both are preserved so users can inspect the difference.

The archive may contain multiple historical versions of a resource. Consumers
should choose the timestamp they want and select the newest capture at or before
that timestamp.

Running a recovered Flash client from the archive is not expected to recreate
the original service by itself. The client may still attempt to contact offline
production endpoints or the public S3 bucket.

## Use Notes

The frontend archive contains public historical web material, including files
that may be copyrighted or obsolete. Use extracts responsibly and preserve
provenance when republishing.
