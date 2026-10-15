-- Milestones 3 and 4: library folders and items, and per-file state.
-- Nothing here refers to media files except by path; files themselves are never changed.

CREATE TABLE library_folders (
    id                      INTEGER PRIMARY KEY,
    path                    TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    kind                    TEXT    NOT NULL,           -- LibraryFolderKind name
    added_utc               INTEGER NOT NULL,           -- Unix ms
    last_scan_started_utc   INTEGER,
    last_scan_completed_utc INTEGER
);

CREATE TABLE library_items (
    id                   INTEGER PRIMARY KEY,
    folder_id            INTEGER NOT NULL REFERENCES library_folders (id) ON DELETE CASCADE,
    path                 TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    size_bytes           INTEGER NOT NULL,
    modified_utc         INTEGER NOT NULL,
    added_utc            INTEGER NOT NULL,
    last_seen_utc        INTEGER NOT NULL,
    missing              INTEGER NOT NULL DEFAULT 0,

    -- What the item is. Source says where these came from: FileName, Online or Manual.
    category             TEXT    NOT NULL,
    title                TEXT    NOT NULL,
    year                 INTEGER,
    season               INTEGER,
    episode              INTEGER,
    episode_title        TEXT,
    air_date             TEXT,                          -- yyyy-MM-dd
    overview             TEXT,
    source               TEXT    NOT NULL,
    confidence           TEXT    NOT NULL,
    artwork_path         TEXT,

    -- What inspecting the file found.
    probed               INTEGER NOT NULL DEFAULT 0,
    duration_ms          INTEGER,
    width                INTEGER,
    height               INTEGER,
    video_codec          TEXT,
    fingerprint          TEXT,

    -- Online lookup: when it ran, and a match kept as a suggestion when it was uncertain.
    looked_up_utc        INTEGER,
    match_service        TEXT,
    match_id             TEXT,
    match_title          TEXT,
    match_year           INTEGER,
    match_episode_title  TEXT,
    match_overview       TEXT,
    match_artwork_url    TEXT,
    match_confidence     TEXT
);

CREATE INDEX ix_library_items_folder ON library_items (folder_id);
CREATE INDEX ix_library_items_added ON library_items (added_utc DESC);

-- Per-file state that is not history: the watched mark and remembered playback choices.
CREATE TABLE item_state (
    path               TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
    watched            INTEGER,          -- NULL: decided by history; 0/1: the user's mark
    audio_track        INTEGER,
    subtitle_track     INTEGER,
    subtitles_off      INTEGER NOT NULL DEFAULT 0,
    subtitle_delay_ms  INTEGER NOT NULL DEFAULT 0,
    audio_delay_ms     INTEGER NOT NULL DEFAULT 0,
    aspect             TEXT,
    speed              REAL
);
