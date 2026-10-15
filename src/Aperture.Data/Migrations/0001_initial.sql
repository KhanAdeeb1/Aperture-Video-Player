-- Milestone 1: settings and playback history.
CREATE TABLE settings (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE playback_history (
    path             TEXT    NOT NULL PRIMARY KEY COLLATE NOCASE,
    title            TEXT    NOT NULL,
    position_ms      INTEGER NOT NULL,
    duration_ms      INTEGER NOT NULL,
    last_played_utc  INTEGER NOT NULL  -- Unix time, milliseconds
);

CREATE INDEX ix_playback_history_last_played ON playback_history (last_played_utc DESC);
