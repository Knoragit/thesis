//! The song library: a `SQLite` table of known songs plus the folder of `.mid` files.
//!
//! The table layout is shared with the original bridge, so an existing database keeps working.

use std::path::{Path, PathBuf};

use rusqlite::{Connection, OptionalExtension, params};
use thiserror::Error;

use crate::midi::analysis::{self, ReferenceNote};
use crate::midi::{Timeline, TimelineError};
use crate::protocol::SongEntry;

const UNKNOWN_ARTIST: &str = "Artista Non Specificato";
const UNKNOWN_TITLE: &str = "Titolo Sconosciuto";
/// How many suggestions Unity gets while the user is typing.
const SUGGESTION_LIMIT: i64 = 20;

#[derive(Debug, Error)]
pub enum LibraryError {
    #[error("database error: {0}")]
    Database(#[from] rusqlite::Error),
    #[error("cannot read songs folder: {0}")]
    Io(#[from] std::io::Error),
    #[error("cannot read song: {0}")]
    Song(#[from] TimelineError),
}

/// A song as stored in the database.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SongRecord {
    pub filename: String,
    pub title: Option<String>,
    pub artist: Option<String>,
}

/// What the core needs from the library.
pub trait SongLibrary {
    /// Every song, with placeholders for missing title or artist.
    fn list(&self) -> Result<Vec<SongEntry>, LibraryError>;
    /// Songs matching what the user has typed so far (stored values, no placeholders).
    fn suggest(&self, text: &str) -> Result<Vec<SongEntry>, LibraryError>;
    fn filename_by_id(&self, id: i64) -> Result<Option<String>, LibraryError>;
    /// The song earlier downloaded for exactly this search, if its file is still there.
    fn cached_search(&self, query: &str) -> Result<Option<SongRecord>, LibraryError>;
    /// Adds a song unless its file is already known.
    fn register(&self, query: &str, song: &SongRecord) -> Result<(), LibraryError>;
    /// The path of a song's file, if the file exists.
    fn song_file(&self, filename: &str) -> Option<PathBuf>;
    /// The notes of a song, as played by the original performer.
    fn reference_notes(&self, filename: &str) -> Result<Vec<ReferenceNote>, LibraryError>;
}

#[derive(Debug)]
pub struct Library {
    connection: Connection,
    songs_dir: PathBuf,
}

const CREATE_TABLE: &str = "CREATE TABLE IF NOT EXISTS midi_files (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    query TEXT,
    filename TEXT UNIQUE,
    title TEXT,
    artist TEXT)";

impl Library {
    fn song_path(&self, filename: &str) -> PathBuf {
        self.songs_dir.join(filename)
    }

    pub fn open(db_path: &Path, songs_dir: &Path) -> Result<Self, LibraryError> {
        let connection = Connection::open(db_path)?;
        let library = Self {
            connection,
            songs_dir: songs_dir.to_owned(),
        };
        library.create_or_migrate()?;
        Ok(library)
    }

    /// An older version declared `query` UNIQUE, which allowed only one song per search.
    fn create_or_migrate(&self) -> Result<(), LibraryError> {
        self.connection.execute(CREATE_TABLE, [])?;
        let schema: Option<String> = self
            .connection
            .query_row(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='midi_files'",
                [],
                |row| row.get(0),
            )
            .optional()?;
        if schema.is_some_and(|sql| query_column_is_unique(&sql)) {
            tracing::info!("migrating midi_files: UNIQUE(query) -> UNIQUE(filename)");
            self.connection.execute_batch(&format!(
                "BEGIN;
                 {};
                 INSERT OR IGNORE INTO midi_files_new (query, filename, title, artist)
                     SELECT query, filename, title, artist FROM midi_files;
                 DROP TABLE midi_files;
                 ALTER TABLE midi_files_new RENAME TO midi_files;
                 COMMIT;",
                CREATE_TABLE
                    .replace("midi_files", "midi_files_new")
                    .replace("IF NOT EXISTS ", ""),
            ))?;
        }
        Ok(())
    }

    /// Registers every `.mid` file in the songs folder that is safe to visualize.
    /// Returns how many songs were added.
    pub fn scan_folder(&self) -> Result<usize, LibraryError> {
        let mut names: Vec<String> = std::fs::read_dir(&self.songs_dir)?
            .filter_map(Result::ok)
            .filter_map(|entry| entry.file_name().into_string().ok())
            .filter(|name| name.to_lowercase().ends_with(".mid"))
            .collect();
        names.sort();

        let mut added = 0;
        for name in names {
            // An unreadable file is registered anyway; it fails when someone tries to play it.
            if let Ok(timeline) = Timeline::load(&self.songs_dir.join(&name))
                && let Err(reason) = analysis::check_visualizer_safety(&timeline)
            {
                tracing::warn!(file = %name, %reason, "skipping song unsafe for the visualizer");
                continue;
            }
            let (title, artist) = parse_song_info(&name);
            added += self.insert(&name.to_lowercase(), &name, Some(&title), Some(&artist))?;
        }
        tracing::info!(added, "scanned songs folder");
        Ok(added)
    }

    fn insert(
        &self,
        query: &str,
        filename: &str,
        title: Option<&str>,
        artist: Option<&str>,
    ) -> Result<usize, LibraryError> {
        Ok(self.connection.execute(
            "INSERT OR IGNORE INTO midi_files (query, filename, title, artist) VALUES (?1, ?2, ?3, ?4)",
            params![query, filename, title, artist],
        )?)
    }

    fn entries(
        &self,
        sql: &str,
        parameters: impl rusqlite::Params,
    ) -> Result<Vec<SongEntry>, LibraryError> {
        let mut statement = self.connection.prepare(sql)?;
        let rows = statement.query_map(parameters, |row| {
            Ok(SongEntry {
                id: row.get(0)?,
                query: row.get(1)?,
                filename: row.get(2)?,
                title: row.get(3)?,
                artist: row.get(4)?,
            })
        })?;
        Ok(rows.collect::<Result<_, _>>()?)
    }
}

impl SongLibrary for Library {
    fn list(&self) -> Result<Vec<SongEntry>, LibraryError> {
        let mut songs = self.entries(
            "SELECT id, query, filename, title, artist FROM midi_files ORDER BY id",
            [],
        )?;
        for song in &mut songs {
            let missing = |text: &Option<String>| text.as_deref().is_none_or(str::is_empty);
            match (missing(&song.title), missing(&song.artist)) {
                (true, true) => {
                    let (title, artist) = parse_song_info(&song.filename);
                    song.title = Some(title);
                    song.artist = Some(artist);
                }
                (true, false) => song.title = Some(UNKNOWN_TITLE.to_owned()),
                (false, true) => song.artist = Some(UNKNOWN_ARTIST.to_owned()),
                (false, false) => {}
            }
        }
        Ok(songs)
    }

    fn suggest(&self, text: &str) -> Result<Vec<SongEntry>, LibraryError> {
        let text = text.trim().to_lowercase();
        if text.is_empty() {
            return Ok(Vec::new());
        }
        let pattern = format!("%{text}%");
        self.entries(
            "SELECT id, query, filename, title, artist FROM midi_files
             WHERE title LIKE ?1 OR artist LIKE ?1 OR filename LIKE ?1 OR query LIKE ?1
             ORDER BY id LIMIT ?2",
            params![pattern, SUGGESTION_LIMIT],
        )
    }

    fn filename_by_id(&self, id: i64) -> Result<Option<String>, LibraryError> {
        Ok(self
            .connection
            .query_row(
                "SELECT filename FROM midi_files WHERE id = ?1",
                [id],
                |row| row.get(0),
            )
            .optional()?)
    }

    fn cached_search(&self, query: &str) -> Result<Option<SongRecord>, LibraryError> {
        let record = self
            .connection
            .query_row(
                "SELECT filename, title, artist FROM midi_files WHERE query = ?1 ORDER BY id LIMIT 1",
                [query],
                |row| {
                    Ok(SongRecord { filename: row.get(0)?, title: row.get(1)?, artist: row.get(2)? })
                },
            )
            .optional()?;
        Ok(record.filter(|song| self.song_file(&song.filename).is_some()))
    }

    fn register(&self, query: &str, song: &SongRecord) -> Result<(), LibraryError> {
        self.insert(
            query,
            &song.filename,
            song.title.as_deref(),
            song.artist.as_deref(),
        )?;
        Ok(())
    }

    fn song_file(&self, filename: &str) -> Option<PathBuf> {
        Some(self.song_path(filename)).filter(|path| path.is_file())
    }

    fn reference_notes(&self, filename: &str) -> Result<Vec<ReferenceNote>, LibraryError> {
        Ok(analysis::reference_notes(&Timeline::load(
            &self.song_path(filename),
        )?))
    }
}

/// True if the `query` column of a `CREATE TABLE` statement is declared UNIQUE.
fn query_column_is_unique(create_sql: &str) -> bool {
    let lower = create_sql.to_lowercase();
    lower.match_indices("query").any(|(start, _)| {
        let rest = &lower[start..];
        let end = rest.find([',', '\n']).unwrap_or(rest.len());
        rest[..end].contains("unique")
    })
}

/// Guesses `(title, artist)` from a file name such as `Moonlight Sonata - Beethoven.mid`.
#[must_use]
pub fn parse_song_info(filename: &str) -> (String, String) {
    let name = file_stem(filename).replace('_', " ");
    let split = name.split_once(" - ").or_else(|| name.split_once('-'));
    match split {
        Some((title, artist)) => (title.trim().to_owned(), artist.trim().to_owned()),
        None => (name.trim().to_owned(), UNKNOWN_ARTIST.to_owned()),
    }
}

/// Like Python's `os.path.splitext(name)[0]`: drops the last extension, ignoring a leading dot.
fn file_stem(filename: &str) -> &str {
    match filename.rfind('.') {
        Some(dot) if filename[..dot].chars().any(|c| c != '.') => &filename[..dot],
        _ => filename,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::midi::testing;

    struct Fixture {
        _dir: tempfile::TempDir,
        songs: PathBuf,
        db: PathBuf,
    }

    fn fixture() -> Fixture {
        let dir = tempfile::tempdir().unwrap();
        let songs = dir.path().join("songs");
        std::fs::create_dir(&songs).unwrap();
        let db = dir.path().join("library.db");
        Fixture {
            _dir: dir,
            songs,
            db,
        }
    }

    impl Fixture {
        fn put(&self, name: &str, bytes: &[u8]) {
            std::fs::write(self.songs.join(name), bytes).unwrap();
        }

        fn open(&self) -> Library {
            Library::open(&self.db, &self.songs).unwrap()
        }
    }

    #[test]
    fn song_names_are_parsed_into_title_and_artist() {
        let cases = [
            (
                "Moonlight Sonata - Beethoven.mid",
                "Moonlight Sonata",
                "Beethoven",
            ),
            ("fur_elise-beethoven.mid", "fur elise", "beethoven"),
            ("a - b - c.mid", "a", "b - c"),
            ("plain_name.mid", "plain name", UNKNOWN_ARTIST),
            ("UPPER.MID", "UPPER", UNKNOWN_ARTIST),
            ("dotted.name.mid", "dotted.name", UNKNOWN_ARTIST),
            (".mid", ".mid", UNKNOWN_ARTIST),
            ("noext", "noext", UNKNOWN_ARTIST),
        ];
        for (file, title, artist) in cases {
            assert_eq!(
                parse_song_info(file),
                (title.to_owned(), artist.to_owned()),
                "{file}"
            );
        }
    }

    #[test]
    fn scan_registers_safe_midi_files_only() {
        let f = fixture();
        f.put("good - artist.mid", &testing::melody(&[60]));
        f.put("SHOUT.MID", &testing::melody(&[62]));
        f.put("hammer.mid", &testing::hammer());
        f.put("notes.txt", b"hello");
        f.put("other.midi", &testing::melody(&[64]));
        f.put("broken.mid", b"not midi"); // unreadable files are kept, as the original does

        let library = f.open();
        assert_eq!(library.scan_folder().unwrap(), 3);
        let names: Vec<String> = library
            .list()
            .unwrap()
            .into_iter()
            .map(|s| s.filename)
            .collect();
        assert_eq!(names, ["SHOUT.MID", "broken.mid", "good - artist.mid"]);

        // Scanning again adds nothing, and ids stay put.
        let before = library.list().unwrap();
        assert_eq!(library.scan_folder().unwrap(), 0);
        assert_eq!(library.list().unwrap(), before);
    }

    #[test]
    fn listing_fills_in_missing_metadata() {
        let f = fixture();
        let library = f.open();
        library.insert("q1", "no_meta - x.mid", None, None).unwrap();
        library
            .insert("q2", "b.mid", None, Some("Someone"))
            .unwrap();
        library.insert("q3", "c.mid", Some("Tune"), None).unwrap();
        library.insert("q4", "d.mid", Some(""), Some("")).unwrap();
        let songs = library.list().unwrap();
        let shown: Vec<_> = songs
            .iter()
            .map(|s| (s.title.as_deref().unwrap(), s.artist.as_deref().unwrap()))
            .collect();
        assert_eq!(
            shown,
            [
                ("no meta", "x"),
                (UNKNOWN_TITLE, "Someone"),
                ("Tune", UNKNOWN_ARTIST),
                ("d", UNKNOWN_ARTIST),
            ]
        );
    }

    #[test]
    fn suggestions_search_every_text_column_ignoring_case() {
        let f = fixture();
        let library = f.open();
        library
            .insert(
                "moon search",
                "a.mid",
                Some("Clair de Lune"),
                Some("Debussy"),
            )
            .unwrap();
        library
            .insert("other", "Moonlight.mid", None, None)
            .unwrap();
        library
            .insert("zzz", "z.mid", Some("Z"), Some("Z"))
            .unwrap();

        let files = |text: &str| -> Vec<String> {
            library
                .suggest(text)
                .unwrap()
                .into_iter()
                .map(|s| s.filename)
                .collect()
        };
        assert_eq!(files("MOON"), ["a.mid", "Moonlight.mid"]); // query column, then filename
        assert_eq!(files(" debussy "), ["a.mid"]);
        assert_eq!(files("lune"), ["a.mid"]);
        assert!(files("").is_empty());
        assert!(files("   ").is_empty());
        assert!(files("nothing").is_empty());
        // Suggestions show what is stored: no placeholders.
        assert_eq!(library.suggest("Moonlight").unwrap()[0].title, None);
    }

    #[test]
    fn suggestions_are_capped() {
        let f = fixture();
        let library = f.open();
        for i in 0..30 {
            library
                .insert("tune", &format!("tune{i}.mid"), Some("Tune"), None)
                .unwrap();
        }
        assert_eq!(library.suggest("tune").unwrap().len(), 20);
        assert_eq!(library.list().unwrap().len(), 30);
    }

    #[test]
    fn lookup_by_id_and_cached_search() {
        let f = fixture();
        f.put("a.mid", &testing::melody(&[60]));
        let library = f.open();
        library
            .insert("my query", "a.mid", Some("A"), None)
            .unwrap();
        library
            .insert("my query", "gone.mid", Some("G"), None)
            .unwrap();
        library.insert("lost", "lost.mid", None, None).unwrap();

        assert_eq!(library.filename_by_id(1).unwrap().as_deref(), Some("a.mid"));
        assert_eq!(library.filename_by_id(99).unwrap(), None);
        let hit = library.cached_search("my query").unwrap().unwrap();
        assert_eq!(
            (hit.filename.as_str(), hit.title.as_deref()),
            ("a.mid", Some("A"))
        );
        // Only the first row for a query counts, and only while its file exists.
        assert_eq!(library.cached_search("lost").unwrap(), None);
        assert_eq!(library.cached_search("unknown").unwrap(), None);
    }

    #[test]
    fn register_ignores_a_known_file() {
        let f = fixture();
        let library = f.open();
        let song = SongRecord {
            filename: "a.mid".into(),
            title: Some("A".into()),
            artist: None,
        };
        library.register("first", &song).unwrap();
        library
            .register(
                "second",
                &SongRecord {
                    title: Some("Changed".into()),
                    ..song
                },
            )
            .unwrap();
        let songs = library.list().unwrap();
        assert_eq!(songs.len(), 1);
        assert_eq!(songs[0].query.as_deref(), Some("first"));
        assert_eq!(songs[0].title.as_deref(), Some("A"));
    }

    #[test]
    fn data_survives_reopening_the_database() {
        let f = fixture();
        f.put("a.mid", &testing::melody(&[60]));
        {
            let library = f.open();
            library.scan_folder().unwrap();
        }
        let again = f.open();
        assert_eq!(again.list().unwrap().len(), 1);
        assert_eq!(again.scan_folder().unwrap(), 0);
    }

    #[test]
    fn the_old_schema_is_migrated_keeping_rows_and_ids() {
        let f = fixture();
        {
            let old = Connection::open(&f.db).unwrap();
            old.execute_batch(
                "CREATE TABLE midi_files (id INTEGER PRIMARY KEY AUTOINCREMENT, query TEXT UNIQUE,
                    filename TEXT, title TEXT, artist TEXT);
                 INSERT INTO midi_files (query, filename, title, artist) VALUES
                    ('q1', 'a.mid', 'A', 'X'), ('q2', 'b.mid', 'B', 'Y');",
            )
            .unwrap();
        }
        let library = f.open();
        let songs = library.list().unwrap();
        assert_eq!(
            songs
                .iter()
                .map(|s| (s.id, s.filename.as_str(), s.query.as_deref()))
                .collect::<Vec<_>>(),
            [(1, "a.mid", Some("q1")), (2, "b.mid", Some("q2"))]
        );
        // The new schema allows several songs per search and rejects the same file twice.
        library.insert("q1", "c.mid", None, None).unwrap();
        assert_eq!(library.insert("q9", "c.mid", None, None).unwrap(), 0);
        drop(library);
        let sql: String = Connection::open(&f.db)
            .unwrap()
            .query_row(
                "SELECT sql FROM sqlite_master WHERE name = 'midi_files'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert!(!query_column_is_unique(&sql), "{sql}");
    }

    #[test]
    fn unique_detection_looks_at_the_query_column_only() {
        assert!(query_column_is_unique(
            "CREATE TABLE t (id INTEGER, query TEXT UNIQUE, f TEXT)"
        ));
        assert!(!query_column_is_unique(
            "CREATE TABLE t (query TEXT, filename TEXT UNIQUE)"
        ));
        assert!(!query_column_is_unique(
            "CREATE TABLE t (query TEXT,\n filename TEXT UNIQUE)"
        ));
    }
}
