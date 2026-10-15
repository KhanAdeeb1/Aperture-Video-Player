-- Milestone 5: episodes that hold several episodes, intro/recap/credits segments with the
-- user's corrections, analysis records, and per-show preferences.
-- Segments are keyed by a file's content fingerprint, so they follow a renamed or moved file,
-- are shared by identical copies, and never leak to a different release of the same episode.

ALTER TABLE library_items ADD COLUMN episode_end INTEGER;

-- Files whose names may hold several episodes ("S01E01E02", "S01E01-02") are read again by
-- the next scan, which fills in episode_end. Other rows are untouched.
UPDATE library_items SET modified_utc = -1
WHERE category = 'Episode' AND path GLOB '*[Ss][0-9]*[Ee][0-9]*[-Ee][0-9]*';

CREATE TABLE media_segments (
    fingerprint      TEXT    NOT NULL,
    kind             TEXT    NOT NULL,           -- SegmentKind name
    source           TEXT    NOT NULL,           -- Detected or Chapter (corrections live in segment_corrections)
    start_ms         INTEGER NOT NULL,
    end_ms           INTEGER NOT NULL,
    confidence       REAL    NOT NULL,
    evidence         TEXT,
    PRIMARY KEY (fingerprint, kind)
);

-- The user's own times for a segment; both NULL means "this file has none".
CREATE TABLE segment_corrections (
    fingerprint      TEXT    NOT NULL,
    kind             TEXT    NOT NULL,
    start_ms         INTEGER,
    end_ms           INTEGER,
    updated_utc      INTEGER NOT NULL,
    PRIMARY KEY (fingerprint, kind)
);

CREATE TABLE episode_analysis (
    fingerprint      TEXT    NOT NULL PRIMARY KEY,
    detector_version INTEGER NOT NULL,
    signature        TEXT    NOT NULL,
    analyzed_utc     INTEGER NOT NULL,
    outcome          TEXT    NOT NULL
);

CREATE TABLE series_preferences (
    series_key       TEXT    NOT NULL PRIMARY KEY,
    autoplay         INTEGER,                    -- NULL: as in Settings
    intro_mode       TEXT,
    recap_mode       TEXT
);
