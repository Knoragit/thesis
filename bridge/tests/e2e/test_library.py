"""Song library: folder scan, list_songs, search_suggest, persistence."""

import sqlite3

from harness import midi_fixtures as fx

SONGS = {
    "Moonlight Sonata - Beethoven.mid": fx.scale(),
    "fur_elise-beethoven.mid": fx.piano(64),
    "plain_name.mid": fx.piano(65),
    "UPPER.MID": fx.piano(66),
    "hammer.mid": fx.hammer(),
    "range.mid": fx.out_of_range(),
    "notes.txt": b"not a song",
    "extension.midi": fx.piano(67),
}
SAFE = {
    "Moonlight Sonata - Beethoven.mid": ("Moonlight Sonata", "Beethoven"),
    "fur_elise-beethoven.mid": ("fur elise", "beethoven"),
    "plain_name.mid": ("plain name", "Artista Non Specificato"),
    "UPPER.MID": ("UPPER", "Artista Non Specificato"),
}
SONG_KEYS = ["id", "query", "filename", "title", "artist"]


def list_songs(bridge):
    return bridge.unity.request({"action": "list_songs"}, "song_list")


def test_scan_registers_safe_midi_files_only(make_bridge):
    bridge = make_bridge(SONGS)
    reply = list_songs(bridge)
    assert list(reply) == ["action", "count", "songs"]
    assert reply["count"] == len(reply["songs"]) == len(SAFE)
    by_name = {s["filename"]: s for s in reply["songs"]}
    assert set(by_name) == set(SAFE)
    for name, (title, artist) in SAFE.items():
        song = by_name[name]
        assert list(song) == SONG_KEYS
        assert (song["title"], song["artist"]) == (title, artist)
        assert song["query"] == name.lower()
    ids = [s["id"] for s in reply["songs"]]
    assert len(set(ids)) == len(ids)
    assert ids == sorted(ids)  # listed in id order


def test_ids_survive_a_restart_without_duplicates(make_bridge):
    bridge = make_bridge(SONGS)
    before = list_songs(bridge)
    bridge.restart()
    assert list_songs(bridge) == before


def test_suggest_matches_title_artist_filename_and_query_ignoring_case(make_bridge):
    bridge = make_bridge(SONGS)

    def suggest(text):
        return bridge.unity.request({"action": "search_suggest", "query": text}, "song_list")

    reply = suggest("BEETHOVEN")
    assert list(reply) == ["action", "count", "songs", "suggest"]
    assert reply["suggest"] is True
    assert {s["filename"] for s in reply["songs"]} == {
        "Moonlight Sonata - Beethoven.mid",
        "fur_elise-beethoven.mid",
    }
    assert reply["count"] == 2
    assert [s["filename"] for s in suggest("  moon ")["songs"]] == [
        "Moonlight Sonata - Beethoven.mid"
    ]
    assert [s["filename"] for s in suggest("plain_name.mid")["songs"]] == ["plain_name.mid"]
    assert suggest("zzz")["songs"] == []


def test_suggest_with_a_blank_query_returns_nothing(make_bridge):
    bridge = make_bridge(SONGS)
    for text in ("", "   "):
        reply = bridge.unity.request({"action": "search_suggest", "query": text}, "song_list")
        assert reply == {"action": "song_list", "count": 0, "songs": [], "suggest": True}


def test_suggest_returns_at_most_twenty_songs(make_bridge):
    songs = {f"tune {i:02d}.mid": fx.piano(40 + i % 40) for i in range(25)}
    bridge = make_bridge(songs)
    reply = bridge.unity.request({"action": "search_suggest", "query": "tune"}, "song_list")
    assert reply["count"] == len(reply["songs"]) == 20
    assert len(list_songs(bridge)["songs"]) == 25


def test_missing_query_behaves_like_a_blank_one(make_bridge):
    bridge = make_bridge(SONGS)
    reply = bridge.unity.request({"action": "search_suggest"}, "song_list")
    assert reply == {"action": "song_list", "count": 0, "songs": [], "suggest": True}


def _create_database(path, schema, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.unlink(missing_ok=True)
    con = sqlite3.connect(path)
    con.execute(schema)
    con.executemany(
        "INSERT INTO midi_files (query, filename, title, artist) VALUES (?,?,?,?)", rows
    )
    con.commit()
    con.close()


def test_existing_database_rows_are_kept_and_win_over_the_folder_scan(make_bridge, tmp_path):
    # The bridge creates its data folder itself; prepare the database before the first start.
    bridge = make_bridge({"cached.mid": fx.piano()}, wait_ready=False)
    bridge.stop()
    _create_database(
        bridge.layout.db_path,
        "CREATE TABLE midi_files (id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT, "
        "filename TEXT UNIQUE, title TEXT, artist TEXT)",
        [
            ("my search", "cached.mid", "Cached Title", "Cached Artist"),
            ("gone", "deleted.mid", "Gone", None),
        ],
    )
    bridge.start()
    songs = {s["filename"]: s for s in list_songs(bridge)["songs"]}
    assert songs["cached.mid"]["title"] == "Cached Title"
    assert songs["cached.mid"]["query"] == "my search"
    # Rows are listed even if their file disappeared; missing metadata gets a placeholder.
    assert songs["deleted.mid"]["title"] == "Gone"
    assert songs["deleted.mid"]["artist"] == "Artista Non Specificato"
    assert songs["cached.mid"]["id"] == 1
    assert songs["deleted.mid"]["id"] == 2


def test_old_database_schema_is_migrated_keeping_rows(make_bridge):
    bridge = make_bridge({}, wait_ready=False)
    bridge.stop()
    _create_database(
        bridge.layout.db_path,
        "CREATE TABLE midi_files (id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT UNIQUE, "
        "filename TEXT, title TEXT, artist TEXT)",
        [("q1", "a.mid", "A", "X"), ("q2", "b.mid", "B", "Y")],
    )
    bridge.start()
    songs = list_songs(bridge)["songs"]
    assert [(s["id"], s["filename"], s["query"]) for s in songs] == [
        (1, "a.mid", "q1"),
        (2, "b.mid", "q2"),
    ]
    bridge.stop()
    con = sqlite3.connect(bridge.layout.db_path)
    (schema,) = con.execute("SELECT sql FROM sqlite_master WHERE name='midi_files'").fetchone()
    con.close()
    assert "filename TEXT UNIQUE" in schema
    assert "query TEXT UNIQUE" not in schema
