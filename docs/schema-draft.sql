-- sqlite
-- MyGameBuilder / JGI_test1 canonical S3 archive schema
-- Purpose: a standalone, static SQLite archive of the original S3 object data.
-- Runtime edits/deletes for mygamebuilder-local should live in a separate overlay DB.

PRAGMA encoding = 'UTF-8';
PRAGMA foreign_keys = ON;

-- For a finished standalone archive, do not leave WAL enabled; WAL creates sidecar files.
-- During bulk import you may temporarily use WAL or larger caches, then checkpoint and switch back.
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;

-- Magic marker for file(1)-style identification. "MGBA" as a 32-bit integer.
PRAGMA application_id = 0x4D474241;
PRAGMA user_version = 1;

BEGIN;

-- Minimal provenance required for the file to stand alone.
CREATE TABLE archive_info (
    name  TEXT PRIMARY KEY COLLATE BINARY,
    value TEXT NOT NULL
) STRICT, WITHOUT ROWID;

INSERT INTO archive_info(name, value) VALUES
    ('schema',          'mgb-jgi-test1-canonical-archive'),
    ('schema_version',  '1'),
    ('bucket',          'JGI_test1'),
    ('source_endpoint', 'https://s3.amazonaws.com/JGI_test1/'),
    ('content_scope',   'raw S3 object bodies plus source HTTP/S3 metadata; no decoded derived artifacts'),
    ('versioning',      'supports S3 versions and delete markers; unversioned objects use NULL version_id');

-- One row per S3 key. S3 is flat; key_utf8 is the canonical key identity.
-- key_text is kept because this archive is for humans/tools too, not just bytewise lookup.
CREATE TABLE s3_object (
    object_id INTEGER PRIMARY KEY,

    key_text TEXT NOT NULL COLLATE BINARY,
    key_utf8 BLOB NOT NULL,

    CHECK (length(key_utf8) BETWEEN 1 AND 1024),
    CHECK (key_utf8 = CAST(key_text AS BLOB)),

    UNIQUE (key_utf8)
) STRICT;

CREATE UNIQUE INDEX ux_s3_object_key_text
    ON s3_object(key_text COLLATE BINARY);

-- Optional MGB projection of the S3 key shape:
--   {user}/{project}/{piece_type}/{piece_name}
-- This is deliberately separate from s3_object so the S3 archive table stays clean.
CREATE TABLE mgb_key_part (
    object_id INTEGER PRIMARY KEY
        REFERENCES s3_object(object_id)
        ON DELETE RESTRICT,

    user_name    TEXT NOT NULL COLLATE BINARY,
    project_name TEXT NOT NULL COLLATE BINARY,
    piece_type   TEXT NOT NULL COLLATE BINARY CHECK (piece_type IN (
        'tile', 'actor', 'map', 'screenshot', 'profile', 'tutorial'
    )),
    piece_name   TEXT NOT NULL COLLATE BINARY,

    CHECK (length(user_name) > 0),
    CHECK (length(project_name) > 0),
    CHECK (length(piece_type) > 0),
    CHECK (length(piece_name) > 0)
) STRICT;

CREATE INDEX ix_mgb_key_project_piece
    ON mgb_key_part(user_name, project_name, piece_type, piece_name);

CREATE INDEX ix_mgb_key_project
    ON mgb_key_part(user_name, project_name);

CREATE INDEX ix_mgb_key_piece_type
    ON mgb_key_part(piece_type, user_name, project_name);

-- One row per archived S3 object version, or one row per key if the bucket proves unversioned.
-- If versioning is absent, insert exactly one row per object with version_id NULL and is_latest = 1.
-- If ListObjectVersions finds versions/delete markers, insert every version/marker here.
CREATE TABLE s3_entry (
    entry_id INTEGER PRIMARY KEY,

    object_id INTEGER NOT NULL
        REFERENCES s3_object(object_id)
        ON DELETE RESTRICT,

    -- S3 VersionId exactly as observed. NULL means no version id was present/known.
    version_id TEXT NULL COLLATE BINARY,

    -- Importer-assigned ordering per key. For unversioned data this is 0.
    -- If versions exist, use 0 = oldest and increasing toward latest.
    version_order INTEGER NOT NULL DEFAULT 0 CHECK (version_order >= 0),

    is_latest INTEGER NOT NULL DEFAULT 1 CHECK (is_latest IN (0, 1)),
    is_delete_marker INTEGER NOT NULL DEFAULT 0 CHECK (is_delete_marker IN (0, 1)),
    source_list_ordinal INTEGER NOT NULL CHECK (source_list_ordinal >= 0),
    source_list_xml TEXT NOT NULL,

    -- Source headers / S3 response metadata.
    -- last_modified_utc should be normalized to ISO-8601 UTC, e.g. 2009-10-12T17:50:00Z.
    last_modified_utc TEXT NOT NULL COLLATE BINARY,
    content_type TEXT NULL COLLATE BINARY,
    etag TEXT NULL COLLATE BINARY,
    storage_class TEXT NULL COLLATE BINARY,
    content_length_bytes INTEGER NULL CHECK (
        content_length_bytes IS NULL OR content_length_bytes >= 0
    ),
    body_sha256 TEXT NULL COLLATE BINARY CHECK (
        body_sha256 IS NULL OR (
            length(body_sha256) = 64
            AND body_sha256 NOT GLOB '*[^0-9a-f]*'
        )
    ),

    -- Raw object body. NULL only for delete markers.
    body BLOB NULL,

    -- Known x-amz-meta-* headers, stored as header values, not decoded body-derived facts.
    meta_width        TEXT NULL,
    meta_height       TEXT NULL,
    meta_tilename     TEXT NULL,
    meta_blobencoding TEXT NULL,
    meta_comment      TEXT NULL,
    meta_acl          TEXT NULL,

    CHECK (
        (
            is_delete_marker = 1
            AND body IS NULL
            AND content_length_bytes IS NULL
            AND content_type IS NULL
            AND etag IS NULL
            AND body_sha256 IS NULL
        )
        OR
        (
            is_delete_marker = 0
            AND body IS NOT NULL
            AND content_length_bytes IS NOT NULL
            AND content_length_bytes = length(body)
            AND content_type IS NOT NULL
            AND etag IS NOT NULL
            AND body_sha256 IS NOT NULL
        )
    )
) STRICT;

-- Version identity. SQLite allows multiple NULLs in UNIQUE indexes, so use two partial indexes.
CREATE UNIQUE INDEX ux_s3_entry_version_nonnull
    ON s3_entry(object_id, version_id)
    WHERE version_id IS NOT NULL;

CREATE UNIQUE INDEX ux_s3_entry_version_null
    ON s3_entry(object_id)
    WHERE version_id IS NULL;

-- At most one current/latest entry per key. Post-import validation should also assert at least one.
CREATE UNIQUE INDEX ux_s3_entry_latest
    ON s3_entry(object_id)
    WHERE is_latest = 1;

CREATE UNIQUE INDEX ux_s3_entry_object_order
    ON s3_entry(object_id, version_order);

CREATE UNIQUE INDEX ux_s3_entry_source_list_ordinal
    ON s3_entry(source_list_ordinal);

CREATE INDEX ix_s3_entry_object_latest
    ON s3_entry(object_id, is_latest, is_delete_marker);

CREATE INDEX ix_s3_entry_last_modified
    ON s3_entry(last_modified_utc);

CREATE INDEX ix_s3_entry_etag
    ON s3_entry(etag)
    WHERE etag IS NOT NULL;

CREATE INDEX ix_s3_entry_body_sha256
    ON s3_entry(body_sha256)
    WHERE body_sha256 IS NOT NULL;

CREATE TABLE s3_response_header (
    entry_id INTEGER NOT NULL
        REFERENCES s3_entry(entry_id)
        ON DELETE RESTRICT,

    name TEXT NOT NULL COLLATE BINARY,
    value TEXT NOT NULL,

    PRIMARY KEY(entry_id, name, value),
    CHECK (length(name) > 0)
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_s3_response_header_name
    ON s3_response_header(name, value);

-- For non-MGB/surprise x-amz-meta-* headers. Keep this empty if the known columns cover everything.
-- Names should be stored without the x-amz-meta- prefix, exactly as normalized by your importer.
CREATE TABLE s3_user_metadata_extra (
    entry_id INTEGER NOT NULL
        REFERENCES s3_entry(entry_id)
        ON DELETE RESTRICT,

    name TEXT NOT NULL COLLATE BINARY,
    value TEXT NOT NULL,

    PRIMARY KEY(entry_id, name),

    CHECK (length(name) > 0),
    CHECK (name NOT IN ('width', 'height', 'tilename', 'blobencoding', 'comment', 'acl'))
) STRICT, WITHOUT ROWID;

CREATE INDEX ix_s3_user_metadata_extra_name
    ON s3_user_metadata_extra(name, value);

-- Read views. Metadata views intentionally omit body so normal listing queries do not sling blobs around.
CREATE VIEW v_s3_all_entries AS
SELECT
    o.object_id,
    o.key_text,
    o.key_utf8,
    e.entry_id,
    e.version_id,
    e.version_order,
    e.is_latest,
    e.is_delete_marker,
    e.source_list_ordinal,
    e.source_list_xml,
    e.last_modified_utc,
    e.content_type,
    e.etag,
    e.storage_class,
    e.content_length_bytes,
    e.body_sha256,
    e.meta_width,
    e.meta_height,
    e.meta_tilename,
    e.meta_blobencoding,
    e.meta_comment,
    e.meta_acl
FROM s3_entry e
JOIN s3_object o ON o.object_id = e.object_id;

CREATE VIEW v_s3_current_entries AS
SELECT *
FROM v_s3_all_entries
WHERE is_latest = 1;

CREATE VIEW v_s3_current_live_entries AS
SELECT *
FROM v_s3_current_entries
WHERE is_delete_marker = 0;

CREATE VIEW v_s3_current_bodies AS
SELECT
    c.*,
    e.body
FROM v_s3_current_live_entries c
JOIN s3_entry e ON e.entry_id = c.entry_id;

CREATE VIEW v_s3_all_bodies AS
SELECT
    a.*,
    e.body
FROM v_s3_all_entries a
JOIN s3_entry e ON e.entry_id = a.entry_id
WHERE a.is_delete_marker = 0;

CREATE VIEW v_mgb_current_pieces AS
SELECT
    m.user_name,
    m.project_name,
    m.piece_type,
    m.piece_name,
    c.key_text,
    c.key_utf8,
    c.object_id,
    c.entry_id,
    c.version_id,
    c.source_list_ordinal,
    c.source_list_xml,
    c.last_modified_utc,
    c.content_type,
    c.etag,
    c.storage_class,
    c.content_length_bytes,
    c.body_sha256,
    c.meta_width,
    c.meta_height,
    c.meta_tilename,
    c.meta_blobencoding,
    c.meta_comment,
    c.meta_acl
FROM v_s3_current_live_entries c
JOIN mgb_key_part m ON m.object_id = c.object_id;

CREATE VIEW v_mgb_all_piece_versions AS
SELECT
    m.user_name,
    m.project_name,
    m.piece_type,
    m.piece_name,
    a.key_text,
    a.key_utf8,
    a.object_id,
    a.entry_id,
    a.version_id,
    a.version_order,
    a.is_latest,
    a.is_delete_marker,
    a.source_list_ordinal,
    a.source_list_xml,
    a.last_modified_utc,
    a.content_type,
    a.etag,
    a.storage_class,
    a.content_length_bytes,
    a.body_sha256,
    a.meta_width,
    a.meta_height,
    a.meta_tilename,
    a.meta_blobencoding,
    a.meta_comment,
    a.meta_acl
FROM v_s3_all_entries a
JOIN mgb_key_part m ON m.object_id = a.object_id;

-- Integrity helper views to run after import. They should return zero rows.
CREATE VIEW v_integrity_object_without_current AS
SELECT o.object_id, o.key_text, o.key_utf8
FROM s3_object o
WHERE NOT EXISTS (
    SELECT 1 FROM s3_entry e
    WHERE e.object_id = o.object_id
      AND e.is_latest = 1
);

CREATE VIEW v_integrity_live_entry_without_mgb_key AS
SELECT c.object_id, c.key_text, c.key_utf8, c.entry_id
FROM v_s3_current_live_entries c
WHERE NOT EXISTS (
    SELECT 1 FROM mgb_key_part m
    WHERE m.object_id = c.object_id
);

COMMIT;

-- Recommended post-import checks:
--   PRAGMA foreign_key_check;
--   PRAGMA integrity_check;
--   SELECT COUNT(*) FROM v_integrity_object_without_current;
--   SELECT COUNT(*) FROM v_integrity_live_entry_without_mgb_key;
--   ANALYZE;
--   PRAGMA optimize;
--   PRAGMA wal_checkpoint(TRUNCATE); -- only if WAL was used during import
--   PRAGMA journal_mode = DELETE;    -- return to one-file standalone mode
--
-- For runtime use, open this DB read-only/immutable from the application.


-- -----------------------------------------------------------------------------
-- Optional mygamebuilder-local overlay sketch
-- -----------------------------------------------------------------------------
-- Put this in a separate writable SQLite file, not in the archive file.
-- Current-only overlay semantics:
--   - a local live row shadows the archive object with the same key_utf8
--   - a local delete marker hides the archive object with the same key_utf8
--   - absence of a local row falls through to the archive
-- Add local version tables later only if the local app truly needs history.

/*
CREATE TABLE local_object_overlay (
    key_utf8 BLOB PRIMARY KEY,
    key_text TEXT NOT NULL COLLATE BINARY,

    is_delete_marker INTEGER NOT NULL DEFAULT 0 CHECK (is_delete_marker IN (0, 1)),
    updated_utc TEXT NOT NULL COLLATE BINARY,

    content_type TEXT NULL COLLATE BINARY,
    etag TEXT NULL COLLATE BINARY,
    content_length_bytes INTEGER NULL CHECK (
        content_length_bytes IS NULL OR content_length_bytes >= 0
    ),
    body BLOB NULL,

    meta_width        TEXT NULL,
    meta_height       TEXT NULL,
    meta_tilename     TEXT NULL,
    meta_blobencoding TEXT NULL,
    meta_comment      TEXT NULL,
    meta_acl          TEXT NULL,

    CHECK (key_utf8 = CAST(key_text AS BLOB)),
    CHECK (
        (
            is_delete_marker = 1
            AND body IS NULL
            AND content_length_bytes IS NULL
            AND content_type IS NULL
            AND etag IS NULL
        )
        OR
        (
            is_delete_marker = 0
            AND body IS NOT NULL
            AND content_length_bytes IS NOT NULL
            AND content_length_bytes = length(body)
            AND content_type IS NOT NULL
            AND etag IS NOT NULL
        )
    )
) STRICT;

CREATE INDEX ix_local_object_overlay_key_text
    ON local_object_overlay(key_text COLLATE BINARY);
*/
