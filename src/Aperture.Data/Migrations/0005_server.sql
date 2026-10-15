-- Milestone 7: the media server. Its settings and passphrase hash live in `settings`
-- (keys starting "server."). Nothing here refers to media except library folder ids.

-- Library folders the user shared with the server. Removing a folder from the library
-- stops sharing it.
CREATE TABLE server_shared_folders (
    folder_id  INTEGER NOT NULL PRIMARY KEY REFERENCES library_folders (id) ON DELETE CASCADE
);

-- Devices signed in to the server. Only a SHA-256 of each device's token is kept.
CREATE TABLE server_devices (
    id             INTEGER PRIMARY KEY,
    token_hash     TEXT    NOT NULL UNIQUE,
    name           TEXT    NOT NULL,
    created_utc    INTEGER NOT NULL,
    last_seen_utc  INTEGER NOT NULL,
    expires_utc    INTEGER NOT NULL,
    last_address   TEXT
);
