//! The UDP wire protocol shared with the Unity app.
//!
//! Inbound datagrams that are not well-formed commands are ignored, as in the original bridge.
//! Outbound events are encoded byte-for-byte like Python's `json.dumps`.

use std::io;

use serde::Serialize;
use serde_json::{Map, Value, ser::Formatter};

/// How a play command identifies the song.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SongRef {
    Id(i64),
    Filename(String),
    /// Neither a usable id nor a filename was given.
    Missing,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Hand {
    Left,
    Right,
}

/// The tutorial demonstrations, numbered as Unity sends them (`sfida`).
///
/// 1-4 are the easy level, 5-7 the medium one and 8-10 the hard one.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Challenge {
    SoftTouch = 1,
    Crescendo = 2,
    Staccato = 3,
    Legato = 4,
    /// Four notes loud, then the same four soft.
    DynamicEcho = 5,
    /// Two notes legato, then two staccato.
    LegatoThenStaccato = 6,
    /// Groups of four with the first note accented.
    FirstBeatAccent = 7,
    /// A short melody that slows down at the end.
    Ritardando = 8,
    /// Two-note chords with the top note louder.
    TopVoice = 9,
    /// A long, smooth legato line.
    Cantabile = 10,
}

impl Challenge {
    pub const ALL: [Self; 10] = [
        Self::SoftTouch,
        Self::Crescendo,
        Self::Staccato,
        Self::Legato,
        Self::DynamicEcho,
        Self::LegatoThenStaccato,
        Self::FirstBeatAccent,
        Self::Ritardando,
        Self::TopVoice,
        Self::Cantabile,
    ];
}

/// A command sent by Unity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Command {
    Ping,
    ListSongs,
    SearchSong {
        query: String,
    },
    SearchSuggest {
        query: String,
    },
    /// Plays a song from `start_ms` milliseconds in (0 for the beginning).
    PlaySong {
        song: SongRef,
        start_ms: u64,
    },
    PlaySongFollow(SongRef),
    /// Follow-me in turns: the bridge plays `bars` bars, then the user plays them, and so on.
    PlaySongFollowInTurns {
        song: SongRef,
        bars: u32,
    },
    NextStep,
    StopSong,
    PlayExample(Challenge),
    StopExample,
    StartFaiTu,
    StopFaiTu,
    NoteHand {
        note: u8,
        hand: Hand,
    },
    GenerateReport,
    GetReportStatus,
    /// Engraves the song's score (cached), answered by `score_ready` with the same `request`.
    GetScore {
        song: SongRef,
        request: i64,
    },
}

/// Parses a datagram. Returns `None` for anything that is not a recognised, usable command.
#[must_use]
pub fn parse_command(datagram: &[u8]) -> Option<Command> {
    let Value::Object(object) = serde_json::from_slice(datagram).ok()? else {
        return None;
    };
    let action = object.get("action")?.as_str()?;
    Some(match action {
        "ping" => Command::Ping,
        "list_songs" => Command::ListSongs,
        "search_song" => Command::SearchSong {
            query: query(&object)?,
        },
        "search_suggest" => Command::SearchSuggest {
            query: query(&object)?,
        },
        "play_song" => Command::PlaySong {
            song: song_ref(&object),
            start_ms: start_ms(&object),
        },
        "play_song_follow" => match object.get("demo_bars").and_then(integer) {
            Some(bars @ 1..=64) => Command::PlaySongFollowInTurns {
                song: song_ref(&object),
                bars: bars as u32,
            },
            _ => Command::PlaySongFollow(song_ref(&object)),
        },
        "next_step" => Command::NextStep,
        "stop_song" => Command::StopSong,
        "play_example" => Command::PlayExample(challenge(&object)?),
        "stop_example" => Command::StopExample,
        "start_fai_tu" => Command::StartFaiTu,
        "stop_fai_tu" => Command::StopFaiTu,
        "note_hand" => note_hand(&object)?,
        "generate_report" => Command::GenerateReport,
        "get_report_status" => Command::GetReportStatus,
        "get_score" => Command::GetScore {
            song: song_ref(&object),
            request: object.get("request").and_then(integer).unwrap_or(0),
        },
        _ => return None,
    })
}

/// A missing query counts as empty; a query that is not a string is unusable.
fn query(object: &Map<String, Value>) -> Option<String> {
    match object.get("query") {
        None => Some(String::new()),
        Some(Value::String(text)) => Some(text.clone()),
        Some(_) => None,
    }
}

/// An integer, an integral float or a numeric string, as Python's `int()` would accept.
fn integer(value: &Value) -> Option<i64> {
    match value {
        Value::Number(number) => number.as_i64().or_else(|| {
            let float = number.as_f64()?;
            (float.fract() == 0.0 && float.abs() < 9e15).then_some(float as i64)
        }),
        Value::String(text) => text.trim().parse().ok(),
        _ => None,
    }
}

fn song_ref(object: &Map<String, Value>) -> SongRef {
    match object.get("id") {
        // An id wins over a filename, even one that matches nothing.
        Some(id) if !id.is_null() => integer(id).map_or(SongRef::Missing, SongRef::Id),
        _ => match object.get("filename") {
            Some(Value::String(name)) if !name.is_empty() => SongRef::Filename(name.clone()),
            _ => SongRef::Missing,
        },
    }
}

/// Where to start a song: `start` in seconds. Missing, negative or unusable means the beginning.
fn start_ms(object: &Map<String, Value>) -> u64 {
    object
        .get("start")
        .and_then(Value::as_f64)
        .filter(|seconds| seconds.is_finite() && *seconds > 0.0)
        .map_or(0, |seconds| (seconds * 1000.0).min(u64::MAX as f64) as u64)
}

fn challenge(object: &Map<String, Value>) -> Option<Challenge> {
    let number = object.get("sfida").map_or(Some(1), integer)?;
    match number {
        1 => Some(Challenge::SoftTouch),
        2 => Some(Challenge::Crescendo),
        3 => Some(Challenge::Staccato),
        4 => Some(Challenge::Legato),
        5 => Some(Challenge::DynamicEcho),
        6 => Some(Challenge::LegatoThenStaccato),
        7 => Some(Challenge::FirstBeatAccent),
        8 => Some(Challenge::Ritardando),
        9 => Some(Challenge::TopVoice),
        10 => Some(Challenge::Cantabile),
        _ => None,
    }
}

fn note_hand(object: &Map<String, Value>) -> Option<Command> {
    let note = u8::try_from(integer(object.get("note")?)?)
        .ok()
        .filter(|n| *n < 128)?;
    let hand = match object.get("hand")?.as_str()? {
        "left" => Hand::Left,
        "right" => Hand::Right,
        _ => return None,
    };
    Some(Command::NoteHand { note, hand })
}

/// A song as listed to Unity.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SongEntry {
    pub id: i64,
    pub query: Option<String>,
    pub filename: String,
    pub title: Option<String>,
    pub artist: Option<String>,
}

/// The outcome of an online search.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SearchOutcome {
    Found {
        filename: String,
        title: Option<String>,
        artist: Option<String>,
    },
    NotFound,
}

/// An event sent to Unity.
#[derive(Debug, Clone, PartialEq)]
pub enum Event {
    BridgeReady,
    ClearScene,
    Press {
        note: u8,
        velocity: f64,
    },
    Release {
        note: u8,
        velocity: f64,
    },
    ExpectNotes(Vec<u8>),
    /// Follow-me in turns: the chord to play and when it starts in the song, in seconds.
    ExpectNotesAt {
        notes: Vec<u8>,
        time: f64,
    },
    SongList {
        songs: Vec<SongEntry>,
        suggest: bool,
    },
    SearchResult(SearchOutcome),
    PlayError(String),
    ExampleDone,
    ReportReady {
        file: String,
        folder: String,
        pages: u32,
    },
    ReportStatus {
        has_material: bool,
    },
    /// The engraved score of `filename`: `pagina_1.png` … in `folder`, plus `score.json`.
    /// No pages means it could not be engraved.
    ScoreReady {
        request: i64,
        filename: String,
        folder: String,
        pages: u32,
    },
    /// How far the observer song has got, both in seconds.
    SongProgress {
        position: f64,
        duration: f64,
    },
}

impl Event {
    /// The datagram payload, formatted like Python's `json.dumps`.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        match self {
            Self::BridgeReady => to_wire(&Tagged {
                action: "bridge_ready",
            }),
            Self::ClearScene => to_wire(&Tagged {
                action: "clear_scene",
            }),
            Self::ExampleDone => to_wire(&Tagged {
                action: "example_done",
            }),
            // Note events put `action` last, unlike every other message.
            Self::Press { note, velocity } => to_wire(&NoteWire {
                note: *note,
                velocity: *velocity,
                action: "press",
            }),
            Self::Release { note, velocity } => to_wire(&NoteWire {
                note: *note,
                velocity: *velocity,
                action: "release",
            }),
            Self::ExpectNotes(notes) => to_wire(&ExpectNotesWire {
                action: "expect_notes",
                notes,
            }),
            Self::ExpectNotesAt { notes, time } => to_wire(&ExpectNotesAtWire {
                action: "expect_notes",
                notes,
                time: *time,
            }),
            Self::ScoreReady {
                request,
                filename,
                folder,
                pages,
            } => to_wire(&ScoreReadyWire {
                action: "score_ready",
                request: *request,
                filename,
                folder,
                pages: *pages,
            }),
            Self::SongList {
                songs,
                suggest: false,
            } => to_wire(&SongListWire {
                action: "song_list",
                count: songs.len(),
                songs,
            }),
            Self::SongList {
                songs,
                suggest: true,
            } => to_wire(&SuggestionsWire {
                action: "song_list",
                count: songs.len(),
                songs,
                suggest: true,
            }),
            Self::SearchResult(SearchOutcome::Found {
                filename,
                title,
                artist,
            }) => to_wire(&SearchFoundWire {
                action: "search_result",
                status: "success",
                filename,
                title,
                artist,
            }),
            Self::SearchResult(SearchOutcome::NotFound) => to_wire(&MessageWire {
                action: "search_result",
                status: "error",
                message: "Canzone non trovata nel database online.",
            }),
            Self::PlayError(message) => to_wire(&MessageWire {
                action: "play_result",
                status: "error",
                message,
            }),
            Self::ReportReady {
                file,
                folder,
                pages,
            } => to_wire(&ReportReadyWire {
                action: "report_ready",
                file,
                folder,
                pages: *pages,
            }),
            Self::ReportStatus { has_material } => to_wire(&ReportStatusWire {
                action: "report_status",
                has_material: *has_material,
            }),
            Self::SongProgress { position, duration } => to_wire(&SongProgressWire {
                action: "song_progress",
                position: *position,
                duration: *duration,
            }),
        }
    }
}

// Field order below is the key order on the wire; do not rearrange.

#[derive(Serialize)]
struct Tagged {
    action: &'static str,
}

#[derive(Serialize)]
struct NoteWire {
    note: u8,
    velocity: f64,
    action: &'static str,
}

#[derive(Serialize)]
struct ExpectNotesWire<'a> {
    action: &'static str,
    notes: &'a [u8],
}

#[derive(Serialize)]
struct ExpectNotesAtWire<'a> {
    action: &'static str,
    notes: &'a [u8],
    time: f64,
}

#[derive(Serialize)]
struct ScoreReadyWire<'a> {
    action: &'static str,
    request: i64,
    filename: &'a str,
    folder: &'a str,
    pages: u32,
}

#[derive(Serialize)]
struct SongListWire<'a> {
    action: &'static str,
    count: usize,
    songs: &'a [SongEntry],
}

#[derive(Serialize)]
struct SuggestionsWire<'a> {
    action: &'static str,
    count: usize,
    songs: &'a [SongEntry],
    suggest: bool,
}

#[derive(Serialize)]
struct SearchFoundWire<'a> {
    action: &'static str,
    status: &'static str,
    filename: &'a str,
    title: &'a Option<String>,
    artist: &'a Option<String>,
}

#[derive(Serialize)]
struct MessageWire<'a> {
    action: &'static str,
    status: &'static str,
    message: &'a str,
}

#[derive(Serialize)]
struct ReportReadyWire<'a> {
    action: &'static str,
    file: &'a str,
    folder: &'a str,
    pages: u32,
}

#[derive(Serialize)]
struct ReportStatusWire {
    action: &'static str,
    has_material: bool,
}

#[derive(Serialize)]
struct SongProgressWire {
    action: &'static str,
    position: f64,
    duration: f64,
}

fn to_wire<T: Serialize>(value: &T) -> Vec<u8> {
    let mut out = Vec::new();
    let mut serializer = serde_json::Serializer::with_formatter(&mut out, PythonFormatter);
    value
        .serialize(&mut serializer)
        .expect("serializing into a Vec cannot fail");
    out
}

/// `json.dumps` defaults: `", "` and `": "` separators, and non-ASCII escaped as `\uXXXX`.
struct PythonFormatter;

impl Formatter for PythonFormatter {
    fn begin_array_value<W: ?Sized + io::Write>(
        &mut self,
        w: &mut W,
        first: bool,
    ) -> io::Result<()> {
        if first { Ok(()) } else { w.write_all(b", ") }
    }

    fn begin_object_key<W: ?Sized + io::Write>(
        &mut self,
        w: &mut W,
        first: bool,
    ) -> io::Result<()> {
        if first { Ok(()) } else { w.write_all(b", ") }
    }

    fn begin_object_value<W: ?Sized + io::Write>(&mut self, w: &mut W) -> io::Result<()> {
        w.write_all(b": ")
    }

    fn write_string_fragment<W: ?Sized + io::Write>(
        &mut self,
        w: &mut W,
        fragment: &str,
    ) -> io::Result<()> {
        let mut units = [0u16; 2];
        for ch in fragment.chars() {
            if ch.is_ascii() && ch != '\u{7f}' {
                w.write_all(&[ch as u8])?;
            } else {
                for unit in ch.encode_utf16(&mut units) {
                    write!(w, "\\u{unit:04x}")?;
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn text(event: &Event) -> String {
        String::from_utf8(event.encode()).unwrap()
    }

    fn song(id: i64, title: Option<&str>) -> SongEntry {
        SongEntry {
            id,
            query: Some("q".into()),
            filename: "a.mid".into(),
            title: title.map(Into::into),
            artist: None,
        }
    }

    #[test]
    fn simple_events_match_python_json_dumps() {
        assert_eq!(text(&Event::BridgeReady), r#"{"action": "bridge_ready"}"#);
        assert_eq!(text(&Event::ClearScene), r#"{"action": "clear_scene"}"#);
        assert_eq!(text(&Event::ExampleDone), r#"{"action": "example_done"}"#);
        assert_eq!(
            text(&Event::SongProgress {
                position: 12.5,
                duration: 180.0
            }),
            r#"{"action": "song_progress", "position": 12.5, "duration": 180.0}"#
        );
        assert_eq!(
            text(&Event::ReportStatus {
                has_material: false
            }),
            r#"{"action": "report_status", "has_material": false}"#
        );
    }

    #[test]
    fn note_events_put_action_last() {
        assert_eq!(
            text(&Event::Press {
                note: 60,
                velocity: 100.0 / 127.0
            }),
            r#"{"note": 60, "velocity": 0.7874015748031497, "action": "press"}"#
        );
        assert_eq!(
            text(&Event::Release {
                note: 61,
                velocity: 0.0
            }),
            r#"{"note": 61, "velocity": 0.0, "action": "release"}"#
        );
        assert_eq!(
            text(&Event::Press {
                note: 60,
                velocity: 1.0
            }),
            r#"{"note": 60, "velocity": 1.0, "action": "press"}"#
        );
    }

    #[test]
    fn every_midi_velocity_formats_like_python() {
        // Generated with: [json.dumps(v / 127.0) for v in range(128)]
        let expected = include_str!("../tests/data/velocities.txt");
        for (value, want) in (0..128).zip(expected.lines()) {
            let got = text(&Event::Press {
                note: 0,
                velocity: f64::from(value) / 127.0,
            });
            assert_eq!(
                got,
                format!(r#"{{"note": 0, "velocity": {want}, "action": "press"}}"#)
            );
        }
    }

    #[test]
    fn lists_and_nested_objects() {
        assert_eq!(
            text(&Event::ExpectNotes(vec![60, 64, 67])),
            r#"{"action": "expect_notes", "notes": [60, 64, 67]}"#
        );
        assert_eq!(
            text(&Event::ExpectNotes(vec![])),
            r#"{"action": "expect_notes", "notes": []}"#
        );
        assert_eq!(
            text(&Event::SongList {
                songs: vec![song(1, Some("T"))],
                suggest: false
            }),
            r#"{"action": "song_list", "count": 1, "songs": [{"id": 1, "query": "q", "filename": "a.mid", "title": "T", "artist": null}]}"#
        );
        assert_eq!(
            text(&Event::SongList {
                songs: vec![],
                suggest: true
            }),
            r#"{"action": "song_list", "count": 0, "songs": [], "suggest": true}"#
        );
    }

    #[test]
    fn search_and_play_results() {
        assert_eq!(
            text(&Event::SearchResult(SearchOutcome::Found {
                filename: "a.mid".into(),
                title: Some("A".into()),
                artist: None,
            })),
            r#"{"action": "search_result", "status": "success", "filename": "a.mid", "title": "A", "artist": null}"#
        );
        assert_eq!(
            text(&Event::SearchResult(SearchOutcome::NotFound)),
            r#"{"action": "search_result", "status": "error", "message": "Canzone non trovata nel database online."}"#
        );
        assert_eq!(
            text(&Event::PlayError("Brano non trovato nel database.".into())),
            r#"{"action": "play_result", "status": "error", "message": "Brano non trovato nel database."}"#
        );
    }

    #[test]
    fn strings_are_escaped_like_python() {
        let tricky = Some("caf\u{e9} \"q\" \\ \n \u{1F600} \u{7f}");
        assert_eq!(
            text(&Event::SongList {
                songs: vec![song(1, tricky)],
                suggest: false
            }),
            "{\"action\": \"song_list\", \"count\": 1, \"songs\": [{\"id\": 1, \"query\": \"q\", \
             \"filename\": \"a.mid\", \"title\": \"caf\\u00e9 \\\"q\\\" \\\\ \\n \\ud83d\\ude00 \\u007f\", \
             \"artist\": null}]}"
        );
    }

    #[test]
    fn report_ready_escapes_windows_paths() {
        assert_eq!(
            text(&Event::ReportReady {
                file: r"C:\Users\x\r.pdf".into(),
                folder: r"C:\Users\x".into(),
                pages: 3,
            }),
            r#"{"action": "report_ready", "file": "C:\\Users\\x\\r.pdf", "folder": "C:\\Users\\x", "pages": 3}"#
        );
    }

    fn parse(json: &str) -> Option<Command> {
        parse_command(json.as_bytes())
    }

    #[test]
    fn parses_every_command() {
        use Command::*;
        assert_eq!(parse(r#"{"action":"ping"}"#), Some(Ping));
        assert_eq!(parse(r#"{"action": "list_songs"}"#), Some(ListSongs));
        assert_eq!(
            parse(r#"{"action":"search_song", "query":"a b"}"#),
            Some(SearchSong {
                query: "a b".into()
            })
        );
        assert_eq!(
            parse(r#"{"action":"search_suggest"}"#),
            Some(SearchSuggest {
                query: String::new()
            })
        );
        assert_eq!(parse(r#"{"action":"next_step"}"#), Some(NextStep));
        assert_eq!(parse(r#"{"action":"stop_song"}"#), Some(StopSong));
        assert_eq!(parse(r#"{"action":"stop_example"}"#), Some(StopExample));
        assert_eq!(
            parse(r#"{"action":"start_fai_tu","filename":"Improvvisazione_Libera"}"#),
            Some(StartFaiTu)
        );
        assert_eq!(parse(r#"{"action":"stop_fai_tu"}"#), Some(StopFaiTu));
        assert_eq!(
            parse(r#"{"action":"generate_report"}"#),
            Some(GenerateReport)
        );
        assert_eq!(
            parse(r#"{"action":"get_report_status"}"#),
            Some(GetReportStatus)
        );
    }

    #[test]
    fn follow_in_turns_and_scores() {
        assert_eq!(
            parse(r#"{"action":"play_song_follow","id":2,"demo_bars":2}"#),
            Some(Command::PlaySongFollowInTurns {
                song: SongRef::Id(2),
                bars: 2
            })
        );
        // Without a usable bar count it is the plain follow-me.
        for bad in [
            r#""demo_bars":0"#,
            r#""demo_bars":"x""#,
            r#""demo_bars":null"#,
        ] {
            assert_eq!(
                parse(&format!(r#"{{"action":"play_song_follow","id":2,{bad}}}"#)),
                Some(Command::PlaySongFollow(SongRef::Id(2)))
            );
        }
        assert_eq!(
            parse(r#"{"action":"get_score","filename":"a.mid","request":4}"#),
            Some(Command::GetScore {
                song: SongRef::Filename("a.mid".into()),
                request: 4
            })
        );
        assert_eq!(
            text(&Event::ExpectNotesAt {
                notes: vec![60, 64],
                time: 1.5
            }),
            r#"{"action": "expect_notes", "notes": [60, 64], "time": 1.5}"#
        );
        assert_eq!(
            text(&Event::ScoreReady {
                request: 4,
                filename: "a.mid".into(),
                folder: "C:/x".into(),
                pages: 2
            }),
            r#"{"action": "score_ready", "request": 4, "filename": "a.mid", "folder": "C:/x", "pages": 2}"#
        );
    }

    #[test]
    fn song_references() {
        let play = |song| Some(Command::PlaySong { song, start_ms: 0 });
        assert_eq!(
            parse(r#"{"action":"play_song","id":7}"#),
            play(SongRef::Id(7))
        );
        assert_eq!(
            parse(r#"{"action":"play_song","filename":"a.mid"}"#),
            play(SongRef::Filename("a.mid".into()))
        );
        // The id wins, even next to a filename or when it is not a number at all.
        assert_eq!(
            parse(r#"{"action":"play_song","id":7,"filename":"a.mid"}"#),
            play(SongRef::Id(7))
        );
        assert_eq!(
            parse(r#"{"action":"play_song","id":"x","filename":"a.mid"}"#),
            play(SongRef::Missing)
        );
        assert_eq!(
            parse(r#"{"action":"play_song","id":"7"}"#),
            play(SongRef::Id(7))
        );
        assert_eq!(
            parse(r#"{"action":"play_song","id":7.0}"#),
            play(SongRef::Id(7))
        );
        for json in [
            r#"{"action":"play_song"}"#,
            r#"{"action":"play_song","filename":""}"#,
            r#"{"action":"play_song","filename":5}"#,
            r#"{"action":"play_song","id":null}"#,
        ] {
            assert_eq!(parse(json), play(SongRef::Missing), "{json}");
        }
        assert_eq!(
            parse(r#"{"action":"play_song_follow","id":2}"#),
            Some(Command::PlaySongFollow(SongRef::Id(2)))
        );
    }

    #[test]
    fn a_song_can_start_part_way_through() {
        let start = |json: &str| match parse(json) {
            Some(Command::PlaySong { start_ms, .. }) => start_ms,
            other => panic!("expected play_song, got {other:?}"),
        };
        assert_eq!(
            start(r#"{"action":"play_song","id":7,"start":42.5}"#),
            42_500
        );
        assert_eq!(start(r#"{"action":"play_song","id":7,"start":3}"#), 3_000);
        for unusable in ["-1", r#""10""#, "null", "0"] {
            let json = format!(r#"{{"action":"play_song","id":7,"start":{unusable}}}"#);
            assert_eq!(start(&json), 0, "{json}");
        }
    }

    #[test]
    fn challenges() {
        let ex = |json: &str| parse(json);
        assert_eq!(
            ex(r#"{"action":"play_example"}"#),
            Some(Command::PlayExample(Challenge::SoftTouch))
        );
        assert_eq!(
            ex(r#"{"action":"play_example","sfida":4}"#),
            Some(Command::PlayExample(Challenge::Legato))
        );
        assert_eq!(
            ex(r#"{"action":"play_example","sfida":2.0}"#),
            Some(Command::PlayExample(Challenge::Crescendo))
        );
        for challenge in Challenge::ALL {
            let json = format!(
                r#"{{"action":"play_example","sfida":{}}}"#,
                challenge as i64
            );
            assert_eq!(ex(&json), Some(Command::PlayExample(challenge)), "{json}");
        }
        for bad in ["0", "11", "-1", "\"x\"", "null", "2.5"] {
            assert_eq!(
                ex(&format!(r#"{{"action":"play_example","sfida":{bad}}}"#)),
                None,
                "{bad}"
            );
        }
    }

    #[test]
    fn note_hand_commands() {
        assert_eq!(
            parse(r#"{"action":"note_hand","note":60,"hand":"left"}"#),
            Some(Command::NoteHand {
                note: 60,
                hand: Hand::Left
            })
        );
        assert_eq!(
            parse(r#"{"action":"note_hand","note":"61","hand":"right"}"#),
            Some(Command::NoteHand {
                note: 61,
                hand: Hand::Right
            })
        );
        for bad in [
            r#"{"action":"note_hand","note":60,"hand":"both"}"#,
            r#"{"action":"note_hand","note":"x","hand":"left"}"#,
            r#"{"action":"note_hand","hand":"left"}"#,
            r#"{"action":"note_hand","note":60}"#,
            r#"{"action":"note_hand","note":300,"hand":"left"}"#,
            r#"{"action":"note_hand","note":-1,"hand":"left"}"#,
        ] {
            assert_eq!(parse(bad), None, "{bad}");
        }
    }

    #[test]
    fn junk_is_ignored() {
        for junk in [
            "",
            "not json",
            "[1,2]",
            "\"str\"",
            "{}",
            r#"{"action":null}"#,
            r#"{"action":5}"#,
            r#"{"action":"nope"}"#,
            r#"{"action":"search_song","query":5}"#,
            r#"{"action":"search_suggest","query":null}"#,
        ] {
            assert_eq!(parse(junk), None, "{junk}");
        }
        assert_eq!(parse_command(&[0xff, 0xfe, 0x00]), None);
    }
}
