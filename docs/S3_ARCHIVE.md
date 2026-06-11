# MyGameBuilder S3 Content Archive

The S3 content archive preserves the public user-generated content that the
original MyGameBuilder Flash client stored in Amazon S3. It is distributed as a
standalone SQLite database rather than as a directory tree of files.

The database is meant to be read directly with SQLite tools. It contains the
raw object bodies, the source HTTP and S3 metadata that was visible to an
anonymous reader, and enough provenance to understand where each object came
from.

## What Was Archived

The source was the public S3 bucket `JGI_test1`, formerly used by the
MyGameBuilder client as its content store. The original Rails backend at
`http://50.18.54.95:3000` is not part of this archive and has been offline for
years.

The archived S3 keys follow the shape used by the client:

```text
{user}/{project}/{piece_type}/{piece_name}
```

`piece_type` is one of:

```text
tile
actor
map
screenshot
profile
tutorial
```

The archive includes user projects, per-user profiles, map screenshots, and the
reserved system content used by the client. Two names have special meaning:

- `!system` is the reserved system user. It owns built-in tutorials and common
  artwork such as badges.
- `-` is the reserved project used for per-user content that is not tied to a
  normal project. User profiles live at `<user>/-/profile/user`.

Tiles and screenshots are PNG images. Actors, maps, tutorials, and profiles use
the original MyGameBuilder binary formats described in
[`FORMATS.md`](./FORMATS.md).

## How It Was Archived

The archiver read `https://s3.amazonaws.com/JGI_test1/` anonymously using S3's
standard REST API.

At a high level, the capture process was:

1. Enumerate the bucket with `ListObjectVersions`, preserving each returned XML
   entry and assigning it a stable listing order.
2. Download every live object with `GetObject`.
3. Record the object body exactly as returned, plus response headers such as
   `Content-Type`, `ETag`, `Last-Modified`, and any visible `x-amz-meta-*`
   headers.
4. Re-list the bucket before finalization and compare a SHA-256 fingerprint of
   the listing to make sure the bucket did not change during capture.
5. Validate the finished SQLite file, checkpoint it into a single-file database,
   and mark the file read-only.

The archive also probes whether anonymous object tags or ACL subresources are
readable. Those probe results are recorded in `archive_info`. If either
subresource were readable, the archiver would refuse to produce a final archive
until that metadata was captured too.

## The SQLite Archive

The canonical archive is a version-aware SQLite database. It preserves the S3
model even if a bucket has old object versions or delete markers:

- `archive_info` records provenance, schema identity, capture counts, listing
  fingerprint, tool names, creation time, and redaction metadata if present.
- `s3_object` records one row per S3 key.
- `s3_entry` records each archived version or delete marker for a key. Live
  entries contain the body bytes and SHA-256 hash; delete markers intentionally
  have no body.
- `mgb_key_part` is a convenience projection of S3 keys into user, project,
  piece type, and piece name.
- `s3_response_header` preserves captured HTTP response headers.
- `s3_user_metadata_extra` keeps custom `x-amz-meta-*` fields that were not one
  of the known MyGameBuilder metadata columns.

Useful views include:

- `v_s3_current_live_entries` for the current non-deleted S3 objects without
  body blobs.
- `v_s3_current_bodies` for the current non-deleted S3 objects with body blobs.
- `v_mgb_current_pieces` for the current MyGameBuilder pieces without body
  blobs.
- `v_mgb_all_piece_versions` for all recorded versions of recognized
  MyGameBuilder pieces.

Some releases may also include a simplified unversioned archive. That database
has `archive_info.schema = 'mgb-jgi-test1-unversioned-archive'` and stores one
live object per row in `s3_object`. It is only produced after validation proves
that the source archive has no versioning artifacts. Its convenience views are
`v_s3_objects`, `v_s3_bodies`, and `v_mgb_pieces`.

Check `archive_info.schema` first if you are writing queries that need to work
against both shapes.

## Opening And Browsing

Any SQLite browser can inspect the archive. Open it read-only when possible.

Common starting points:

```sql
SELECT name, value
FROM archive_info
ORDER BY name;
```

```sql
SELECT piece_type, COUNT(*) AS pieces
FROM v_mgb_current_pieces
GROUP BY piece_type
ORDER BY piece_type;
```

```sql
SELECT user_name, project_name, piece_type, piece_name, key_text
FROM v_mgb_current_pieces
WHERE user_name = 'alice'
ORDER BY project_name, piece_type, piece_name;
```

To extract a body, select it from the body view for the archive shape you are
using. For example, in the canonical archive:

```sql
SELECT body
FROM v_s3_current_bodies
WHERE key_text = 'alice/project1/tile/avatar';
```

SQLite clients differ in how they save BLOB values. A GUI such as DB Browser
for SQLite is often the simplest way to export one body at a time.

## Provenance And Integrity

For each object or version, the archive preserves:

- The original S3 key as UTF-8 text and bytes.
- The original version ID when S3 reported one. The literal S3 version ID
  `null` is represented as SQL `NULL` when it means an unversioned object.
- The `ListObjectVersions` XML fragment that introduced the row.
- The source listing ordinal.
- The source `Last-Modified`, `ETag`, `StorageClass`, and content length.
- The downloaded body, if the row is a live object.
- A SHA-256 hash of the archived body.
- Captured HTTP response headers from `GetObject`.

Validation checks SQLite integrity, foreign keys, body lengths, body hashes,
delete-marker invariants, required metadata counts, and the presence of captured
headers for live entries.

## Modifications And Redaction

The archive is intended to preserve source bytes exactly. When a redacted
release is produced, the changes are deliberately narrow and recorded in
`archive_info`.

The redaction workflow manually reviews photo-like PNG tiles. Candidate tiles
are selected by a visible unique-color threshold, then each candidate is
approved or redacted by a reviewer. Redacted PNGs are replaced with opaque black
PNGs of the same pixel dimensions.

Because screenshots are rendered views of maps, a redacted tile may still be
visible inside screenshots. The redaction submit step propagates blackouts along
this chain:

```text
redacted tile -> actors that reference it -> maps that reference those actors -> screenshots of those maps
```

For redacted rows, the archive updates the body, content length, and body
SHA-256. Source provenance such as S3 key, listing XML, `ETag`,
`Last-Modified`, and response headers is intentionally preserved so users can
still see what the original source metadata said. As a result, a redacted body
is expected not to match the original S3 `ETag`.

## What Is Not Included

The S3 content archive does not include:

- The original Rails backend database, accounts, passwords, server-side state,
  forums, or private data that was not publicly readable from S3.
- The historical website and Flash client files; those are covered separately in
  [`FRONTEND.md`](./FRONTEND.md).
- Decoded sibling files. The SQLite database stores canonical raw object bodies.
  Use [`FORMATS.md`](./FORMATS.md) to decode actors, maps, tutorials, and
  profiles when you need human-readable forms.
- S3 object tags or ACL documents, unless future source access makes them
  anonymously readable and the archiver records them.

## Use Notes

S3 keys are case-sensitive. Treat `key_text` and `key_utf8` as the authoritative
identity of an object; do not normalize usernames, project names, or piece names
unless you are doing a deliberately fuzzy search.

The content is historical user-generated material. Even with redaction, it may
contain names, comments, copyrighted artwork, or other sensitive context. Use
and republish extracts with care.
