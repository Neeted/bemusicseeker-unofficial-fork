-- Independent LR2/BeMusicSeeker existing-data fixture.
-- This is deliberately authored without loading BeMusicSeeker assemblies.  The
-- acceptance runner copies the compiled SQLite database generated from this
-- schema and only relocates path values into its temporary sandbox.
PRAGMA user_version = 1;

CREATE TABLE song (
    hash varchar,
    title varchar,
    subtitle varchar,
    artist varchar,
    subartist varchar,
    genre varchar,
    tag varchar,
    path varchar primary key not null,
    type integer,
    folder varchar,
    stagefile varchar,
    banner varchar,
    backbmp varchar,
    parent varchar,
    level integer,
    difficulty integer,
    maxbpm integer,
    minbpm integer,
    mode integer,
    judge integer,
    longnote integer,
    bga integer,
    random integer,
    date integer,
    favorite integer,
    txt integer,
    karinotes integer,
    adddate integer,
    exlevel integer
);
CREATE TABLE folder (
    title varchar,
    subtitle varchar,
    category varchar,
    info_a varchar,
    info_b varchar,
    command varchar,
    path varchar primary key not null,
    type integer,
    banner varchar,
    parent varchar,
    date integer,
    max integer,
    adddate integer
);
CREATE TABLE install (
    path varchar primary key not null,
    delete_parent integer
);
CREATE TABLE maintenance (
    hash varchar,
    path varchar primary key not null,
    encoding varchar,
    is_encoding_fixed integer,
    wav_files_existing integer,
    wav_files_defined integer,
    bga_files_existing integer,
    bga_files_defined integer,
    movie_files_existing integer,
    movie_files_defined integer,
    is_stagefile_existing integer,
    is_stagefile_defined integer,
    is_banner_existing integer,
    is_banner_defined integer,
    is_backbmp_existing integer,
    is_backbmp_defined integer,
    is_files_warning_ignored integer,
    lr2_warning_flags integer,
    lr2_resource_max_relative_cp932_bytes integer,
    lr2_resource_has_parent_traversal integer
);
CREATE TABLE playlist (
    playlist_id integer primary key autoincrement not null,
    name varchar,
    symbol varchar,
    folder_order varchar,
    folder_sort_key integer,
    folder_sort_ascending integer,
    entry_type integer,
    page_url varchar,
    header_url varchar,
    data_url varchar,
    tag varchar,
    header_sha256 varchar,
    data_sha256 varchar,
    compat_prefix varchar,
    last_update bigint,
    org_name varchar,
    org_symbol varchar,
    ignore_folder_output integer,
    is_external_sync integer,
    output_dir varchar,
    custom_folder_output_base_name varchar,
    is_root_folder integer,
    bmt_sort integer,
    is_bmt_output integer
);
CREATE TABLE playlist_course (
    course_id integer primary key autoincrement not null,
    playlist_id integer not null,
    course_order integer,
    course_json varchar
);
CREATE TABLE playlist_entry (
    playlist_id integer not null,
    md5 varchar,
    sha256 varchar,
    level float,
    title varchar,
    artist varchar,
    folder varchar,
    lr2_bmsid varchar,
    url varchar,
    url_diff varchar,
    name_diff varchar,
    org_md5 varchar,
    adddate bigint,
    comment varchar,
    memo varchar,
    is_removed integer
);
CREATE TABLE app_schema_version (
    name varchar primary key not null,
    version integer
);

CREATE INDEX hashidx ON song (hash);
CREATE INDEX parentidx ON song (parent);
CREATE INDEX song_idx_folder ON song (folder);
CREATE INDEX song_idx_path_nocase ON song (path COLLATE NOCASE);
CREATE UNIQUE INDEX folder_path ON folder (path);
CREATE INDEX maintenance_idx_path_nocase ON maintenance (path COLLATE NOCASE);
CREATE INDEX hashidx_mtn ON maintenance (hash);
CREATE INDEX playlist_course_idx_id ON playlist_course (playlist_id);
CREATE UNIQUE INDEX playlist_course_idx_uniq ON playlist_course (playlist_id, course_order);
CREATE INDEX playlist_entry_idx_adddate ON playlist_entry (adddate, playlist_id, is_removed);
CREATE INDEX playlist_entry_idx_folder ON playlist_entry (playlist_id, folder, is_removed);
CREATE INDEX playlist_entry_idx_id ON playlist_entry (playlist_id, is_removed);
CREATE INDEX playlist_entry_idx_level ON playlist_entry (playlist_id, level, is_removed);
CREATE INDEX playlist_entry_idx_md5 ON playlist_entry (md5, playlist_id, is_removed);
CREATE INDEX playlist_entry_idx_sha256 ON playlist_entry (sha256, playlist_id, is_removed);
CREATE INDEX playlist_entry_idx_title ON playlist_entry (playlist_id, title, is_removed);
CREATE UNIQUE INDEX playlist_entry_idx_uniq ON playlist_entry (md5, sha256, playlist_id, folder, lr2_bmsid, title, is_removed);

INSERT INTO app_schema_version (name, version) VALUES ('app_schema', 1);
INSERT INTO song (hash, title, artist, path, folder, level, difficulty, maxbpm, minbpm, mode, judge, adddate)
VALUES ('839194ef691f63a91a97b2a7c481bb1d', 'E1 Fixture Song', 'BeMusicSeeker', 'Fixture\\e1-fixture.bms', 'E1 Fixture Folder', 1, 1, 120, 120, 5, 2, 1);
INSERT INTO folder (title, path) VALUES ('E1 Fixture Folder', 'Fixture');
INSERT INTO install (path, delete_parent) VALUES ('__E1_INSTALL_PATH__', 0);
INSERT INTO playlist (playlist_id, name, symbol, last_update, bmt_sort, is_bmt_output)
VALUES (7001, 'E1 Fixture Playlist', 'E1', 638396964000000000, 1, 1);
INSERT INTO playlist_course (course_id, playlist_id, course_order, course_json)
VALUES (7002, 7001, 0, '{"name":"E1 Course","items":["839194ef691f63a91a97b2a7c481bb1d"]}');
INSERT INTO playlist_entry (playlist_id, md5, title, folder, adddate, is_removed)
VALUES (7001, '839194ef691f63a91a97b2a7c481bb1d', 'E1 Fixture Song', 'E1 Fixture Folder', 638396964000000000, 0);
INSERT INTO maintenance (hash, path, encoding, is_encoding_fixed, wav_files_existing, wav_files_defined, lr2_warning_flags)
VALUES ('839194ef691f63a91a97b2a7c481bb1d', 'Fixture\\e1-fixture.bms', 'shift_jis', 0, 0, 1, 14);
