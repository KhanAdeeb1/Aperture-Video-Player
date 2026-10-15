-- Milestone 6: YouTube videos watched in Aperture or saved for later.
-- position_ms, watched_utc and saved_utc are Aperture's own records. title, channel,
-- thumbnail_url and length_ms are YouTube's data: details_utc says when they were fetched,
-- and they are refreshed or set to NULL within 30 days (YouTube API Services Developer
-- Policies III.E.4). A row exists only while the video is in progress, finished or saved.

CREATE TABLE youtube_videos (
    video_id       TEXT    NOT NULL PRIMARY KEY,
    position_ms    INTEGER NOT NULL DEFAULT 0,
    duration_ms    INTEGER NOT NULL DEFAULT 0,
    watched_utc    INTEGER,
    saved_utc      INTEGER,
    title          TEXT,
    channel        TEXT,
    thumbnail_url  TEXT,
    length_ms      INTEGER,
    details_utc    INTEGER
);
