-- Milestone 9: bookmarks, sound options and choices carried between a show's episodes.

-- Moments the user marked in a video, with an optional note. Keyed by path like history.
CREATE TABLE bookmarks (
    id           INTEGER PRIMARY KEY,
    path         TEXT    NOT NULL COLLATE NOCASE,
    position_ms  INTEGER NOT NULL,
    note         TEXT    NOT NULL DEFAULT '',
    created_utc  INTEGER NOT NULL
);
CREATE INDEX ix_bookmarks_path ON bookmarks (path, position_ms);

-- Sound options the user chose for one file (NULL: as the show or Settings say).
ALTER TABLE item_state ADD COLUMN clear_dialogue INTEGER;
ALTER TABLE item_state ADD COLUMN even_volume INTEGER;

-- What the user last picked while watching a show, carried to its next episodes.
CREATE TABLE show_choices (
    series_key         TEXT    NOT NULL PRIMARY KEY,
    audio_language     TEXT,
    subtitle_language  TEXT,
    subtitles_off      INTEGER NOT NULL DEFAULT 0,
    speed              REAL,
    clear_dialogue     INTEGER,             -- NULL with even_volume NULL: as in Settings
    even_volume        INTEGER
);
