use std::cell::RefCell;
use std::collections::HashMap;

use super::*;
use crate::library::LibraryError;
use crate::midi::analysis::ReferenceNote;
use crate::protocol::{Hand, SongEntry};

/// An in-memory library: songs by id, with the files that "exist" and their notes.
#[derive(Default)]
struct FakeLibrary {
    ids: HashMap<i64, String>,
    files: HashMap<String, Vec<ReferenceNote>>,
    cached: HashMap<String, SongRecord>,
    registered: RefCell<Vec<(String, SongRecord)>>,
}

impl FakeLibrary {
    fn with_song(mut self, id: i64, filename: &str, notes: &[(u8, f64)]) -> Self {
        self.ids.insert(id, filename.to_owned());
        let notes = notes.iter().map(|&(note, start)| ReferenceNote {
            note,
            vel: 0.5,
            start,
            dur: 0.1,
        });
        self.files.insert(filename.to_owned(), notes.collect());
        self
    }
}

impl SongLibrary for FakeLibrary {
    fn list(&self) -> Result<Vec<SongEntry>, LibraryError> {
        Ok(self.ids.iter().map(|(id, name)| entry(*id, name)).collect())
    }

    fn suggest(&self, text: &str) -> Result<Vec<SongEntry>, LibraryError> {
        Ok(self
            .ids
            .iter()
            .filter(|(_, n)| n.contains(text.trim()))
            .map(|(i, n)| entry(*i, n))
            .collect())
    }

    fn filename_by_id(&self, id: i64) -> Result<Option<String>, LibraryError> {
        Ok(self.ids.get(&id).cloned())
    }

    fn cached_search(&self, query: &str) -> Result<Option<SongRecord>, LibraryError> {
        Ok(self.cached.get(query).cloned())
    }

    fn register(&self, query: &str, song: &SongRecord) -> Result<(), LibraryError> {
        self.registered
            .borrow_mut()
            .push((query.to_owned(), song.clone()));
        Ok(())
    }

    fn song_file(&self, filename: &str) -> Option<PathBuf> {
        self.files
            .contains_key(filename)
            .then(|| PathBuf::from("/songs").join(filename))
    }

    fn reference_notes(&self, filename: &str) -> Result<Vec<ReferenceNote>, LibraryError> {
        Ok(self.files[filename].clone())
    }
}

fn entry(id: i64, name: &str) -> SongEntry {
    SongEntry {
        id,
        query: None,
        filename: name.to_owned(),
        title: Some(name.to_owned()),
        artist: None,
    }
}

fn library() -> FakeLibrary {
    FakeLibrary::default()
        .with_song(
            1,
            "chords.mid",
            &[(60, 0.0), (64, 0.01), (72, 1.0), (76, 2.0)],
        )
        .with_song(2, "other.mid", &[(50, 0.0)])
}

struct Harness {
    core: Core,
    songs: FakeLibrary,
    now: f64,
}

impl Harness {
    fn new() -> Self {
        Self {
            core: Core::new(),
            songs: library(),
            now: 100.0,
        }
    }

    fn input(&mut self, input: Input) -> Vec<Effect> {
        self.core.handle(input, self.now, &self.songs)
    }

    fn command(&mut self, command: Command) -> Vec<Effect> {
        self.input(Input::Command(command))
    }

    fn at(&mut self, seconds: f64) -> &mut Self {
        self.now = seconds;
        self
    }

    fn press(&mut self, note: u8) -> Vec<Effect> {
        self.input(Input::Note(NoteMessage {
            kind: NoteKind::Press,
            note,
            velocity: 127,
        }))
    }

    fn release(&mut self, note: u8) -> Vec<Effect> {
        self.input(Input::Note(NoteMessage {
            kind: NoteKind::Release,
            note,
            velocity: 0,
        }))
    }

    fn tap(&mut self, note: u8) {
        self.press(note);
        self.now += 0.5;
        self.release(note);
    }
}

fn events(effects: Vec<Effect>) -> Vec<Event> {
    effects
        .into_iter()
        .map(|effect| match effect {
            Effect::Send(event) => event,
            other => panic!("expected only events, got {other:?}"),
        })
        .collect()
}

fn by_name(name: &str) -> Command {
    Command::PlaySong(SongRef::Filename(name.to_owned()))
}

fn follow(name: &str) -> Command {
    Command::PlaySongFollow(SongRef::Filename(name.to_owned()))
}

#[test]
fn ping_is_answered() {
    let mut h = Harness::new();
    assert_eq!(events(h.command(Command::Ping)), [Event::BridgeReady]);
}

#[test]
fn listing_and_suggesting() {
    let mut h = Harness::new();
    let Event::SongList { songs, suggest } = events(h.command(Command::ListSongs)).remove(0) else {
        panic!("expected a song list");
    };
    assert_eq!((songs.len(), suggest), (2, false));

    let query = "chords".to_owned();
    let Event::SongList { songs, suggest } =
        events(h.command(Command::SearchSuggest { query })).remove(0)
    else {
        panic!("expected suggestions");
    };
    assert_eq!((songs.len(), suggest), (1, true));
}

// -- playing songs ----------------------------------------------------------------------

#[test]
fn playing_a_song_by_name_or_id_starts_playback() {
    let mut h = Harness::new();
    assert_eq!(
        h.command(by_name("chords.mid")),
        [Effect::StartPlayback {
            id: 0,
            path: "/songs/chords.mid".into()
        }]
    );
    assert_eq!(
        h.command(Command::PlaySong(SongRef::Id(2))),
        [Effect::StartPlayback {
            id: 1,
            path: "/songs/other.mid".into()
        }]
    );
}

#[test]
fn unknown_songs_get_an_error_and_change_nothing() {
    let mut h = Harness::new();
    let not_in_db = Event::PlayError(NOT_IN_DATABASE.into());
    for command in [
        Command::PlaySong(SongRef::Id(99)),
        Command::PlaySong(SongRef::Missing),
        Command::PlaySongFollow(SongRef::Id(99)),
        Command::PlaySongFollow(SongRef::Missing),
    ] {
        assert_eq!(events(h.command(command)), std::slice::from_ref(&not_in_db));
    }
    let missing_file = Event::PlayError(FILE_MISSING.into());
    assert_eq!(
        events(h.command(by_name("ghost.mid"))),
        std::slice::from_ref(&missing_file)
    );
    assert_eq!(events(h.command(follow("ghost.mid"))), [missing_file]);
    // Nothing started, so a demonstration is still allowed.
    assert_eq!(
        h.command(Command::PlayExample(Challenge::SoftTouch)),
        [Effect::StartExample(Challenge::SoftTouch)]
    );
}

#[test]
fn stop_song_cancels_playback_and_clears_the_scene() {
    let mut h = Harness::new();
    h.command(by_name("chords.mid"));
    assert_eq!(
        h.command(Command::StopSong),
        [Effect::StopPlayback, Effect::Send(Event::ClearScene)]
    );
    // Safe with nothing playing, too.
    assert_eq!(
        h.command(Command::StopSong),
        [Effect::StopPlayback, Effect::Send(Event::ClearScene)]
    );
}

#[test]
fn a_finished_playback_frees_the_bridge_only_if_it_is_the_current_one() {
    let mut h = Harness::new();
    h.command(by_name("chords.mid")); // id 0
    h.command(by_name("other.mid")); // id 1
    h.input(Input::PlaybackFinished(0)); // the replaced song winding down
    assert_eq!(
        events(h.command(Command::PlayExample(Challenge::Legato))),
        [Event::ExampleDone]
    );
    h.input(Input::PlaybackFinished(1));
    assert_eq!(
        h.command(Command::PlayExample(Challenge::Legato)),
        [Effect::StartExample(Challenge::Legato)]
    );
}

// -- follow-me ----------------------------------------------------------------------------

#[test]
fn following_serves_chords_then_clears_the_scene() {
    let mut h = Harness::new();
    assert_eq!(
        events(h.command(follow("chords.mid"))),
        [Event::ExpectNotes(vec![60, 64])]
    );
    assert_eq!(
        events(h.command(Command::NextStep)),
        [Event::ExpectNotes(vec![72])]
    );
    assert_eq!(
        events(h.command(Command::NextStep)),
        [Event::ExpectNotes(vec![76])]
    );
    assert_eq!(events(h.command(Command::NextStep)), [Event::ClearScene]);
    assert_eq!(events(h.command(Command::NextStep)), [Event::ClearScene]);
}

#[test]
fn next_step_without_a_song_clears_the_scene() {
    let mut h = Harness::new();
    assert_eq!(events(h.command(Command::NextStep)), [Event::ClearScene]);
}

#[test]
fn following_a_new_song_restarts_the_sequence() {
    let mut h = Harness::new();
    h.command(follow("chords.mid"));
    h.command(Command::NextStep);
    assert_eq!(
        events(h.command(follow("other.mid"))),
        [Event::ExpectNotes(vec![50])]
    );
}

#[test]
fn a_failed_follow_request_leaves_the_current_song_alone() {
    let mut h = Harness::new();
    h.command(follow("chords.mid"));
    h.command(follow("ghost.mid"));
    assert_eq!(
        events(h.command(Command::NextStep)),
        [Event::ExpectNotes(vec![72])]
    );
}

#[test]
fn stop_song_ends_following_but_keeps_the_position() {
    let mut h = Harness::new();
    h.command(follow("chords.mid"));
    h.command(Command::StopSong);
    assert_eq!(
        events(h.command(Command::NextStep)),
        [Event::ExpectNotes(vec![72])]
    );
}

#[test]
fn demonstrations_are_skipped_while_following() {
    let mut h = Harness::new();
    h.command(follow("chords.mid"));
    assert_eq!(
        events(h.command(Command::PlayExample(Challenge::Staccato))),
        [Event::ExampleDone]
    );
    h.command(Command::StopSong);
    assert_eq!(
        h.command(Command::PlayExample(Challenge::Staccato)),
        [Effect::StartExample(Challenge::Staccato)]
    );
}

#[test]
fn finishing_a_song_allows_demonstrations_again() {
    let mut h = Harness::new();
    h.command(follow("other.mid"));
    h.command(Command::NextStep); // past the only chord
    assert_eq!(
        h.command(Command::PlayExample(Challenge::Crescendo)),
        [Effect::StartExample(Challenge::Crescendo)]
    );
}

#[test]
fn stopping_an_example_is_just_an_effect() {
    let mut h = Harness::new();
    assert_eq!(h.command(Command::StopExample), [Effect::StopExample]);
}

// -- keyboard notes ---------------------------------------------------------------------

#[test]
fn keyboard_notes_are_always_relayed_with_unit_velocity() {
    let mut h = Harness::new();
    assert_eq!(
        events(h.press(60)),
        [Event::Press {
            note: 60,
            velocity: 1.0
        }]
    );
    assert_eq!(
        events(h.release(60)),
        [Event::Release {
            note: 60,
            velocity: 0.0
        }]
    );
}

fn report_of(effects: Vec<Effect>) -> SessionData {
    match effects.into_iter().next() {
        Some(Effect::RenderReport(data)) => data,
        other => panic!("expected a report request, got {other:?}"),
    }
}

#[test]
fn a_free_play_take_goes_into_the_report() {
    let mut h = Harness::new();
    h.command(Command::NoteHand {
        note: 60,
        hand: Hand::Right,
    });
    h.command(Command::StartFaiTu);
    h.at(10.0).tap(60);
    h.at(11.0).tap(48);
    let data = report_of(h.command(Command::GenerateReport));
    assert_eq!(data.version, 1);
    let played: Vec<_> = data
        .fai_tu_notes
        .iter()
        .map(|n| (n.note, n.start, n.end, n.hand))
        .collect();
    assert_eq!(
        played,
        [(60, 10.0, 10.5, Some(Hand::Right)), (48, 11.0, 11.5, None)]
    );
    assert!(data.follow_sessions.is_empty());
}

#[test]
fn follow_me_sessions_with_notes_go_into_the_report() {
    let mut h = Harness::new();
    h.command(follow("chords.mid"));
    h.at(5.0).tap(60);
    h.command(follow("other.mid")); // never played
    let data = report_of(h.command(Command::GenerateReport));
    assert_eq!(data.follow_sessions.len(), 1);
    let session = &data.follow_sessions[0];
    assert_eq!(
        (session.filename.as_str(), session.title.as_str()),
        ("chords.mid", "chords")
    );
    assert_eq!(session.reference_notes.len(), 4);
    assert_eq!(session.user_notes.len(), 1);
}

#[test]
fn report_status_reflects_the_material() {
    let mut h = Harness::new();
    let status = |h: &mut Harness| events(h.command(Command::GetReportStatus));
    assert_eq!(
        status(&mut h),
        [Event::ReportStatus {
            has_material: false
        }]
    );
    h.command(Command::StartFaiTu);
    h.press(60);
    assert_eq!(
        status(&mut h),
        [Event::ReportStatus {
            has_material: false
        }],
        "still held"
    );
    h.release(60);
    assert_eq!(status(&mut h), [Event::ReportStatus { has_material: true }]);
    h.command(Command::StartFaiTu);
    assert_eq!(
        status(&mut h),
        [Event::ReportStatus {
            has_material: false
        }]
    );
}

#[test]
fn notes_after_a_stopped_take_or_song_are_not_material() {
    let mut h = Harness::new();
    h.command(Command::StartFaiTu);
    h.command(Command::StopFaiTu);
    h.tap(60);
    h.command(follow("chords.mid"));
    h.command(Command::StopSong);
    h.tap(62);
    assert_eq!(
        events(h.command(Command::GetReportStatus)),
        [Event::ReportStatus {
            has_material: false
        }]
    );
}

// -- reports ------------------------------------------------------------------------------

#[test]
fn the_first_report_is_never_debounced_even_early_on() {
    let mut h = Harness::new();
    assert!(matches!(
        h.at(0.5).command(Command::GenerateReport).as_slice(),
        [Effect::RenderReport(_)]
    ));
}

#[test]
fn requests_within_five_seconds_of_a_report_are_dropped() {
    let mut h = Harness::new();
    h.at(100.0).command(Command::GenerateReport);
    h.input(Input::ReportFinished(ReportOutcome::Failed));
    assert!(h.at(104.9).command(Command::GenerateReport).is_empty());
    assert!(matches!(
        h.at(105.0).command(Command::GenerateReport).as_slice(),
        [Effect::RenderReport(_)]
    ));
}

#[test]
fn only_one_report_renders_at_a_time() {
    let mut h = Harness::new();
    h.at(0.0).command(Command::GenerateReport);
    assert!(
        h.at(30.0).command(Command::GenerateReport).is_empty(),
        "first still rendering"
    );
    h.input(Input::ReportFinished(ReportOutcome::Failed));
    assert!(matches!(
        h.at(31.0).command(Command::GenerateReport).as_slice(),
        [Effect::RenderReport(_)]
    ));
}

#[test]
fn generating_a_report_ends_the_free_play_take_even_when_dropped() {
    let mut h = Harness::new();
    h.at(0.0).command(Command::GenerateReport);
    h.command(Command::StartFaiTu);
    h.at(1.0).command(Command::GenerateReport); // dropped
    h.tap(60);
    assert_eq!(
        events(h.command(Command::GetReportStatus)),
        [Event::ReportStatus {
            has_material: false
        }]
    );
}

#[test]
fn report_results_are_forwarded_and_failures_become_an_empty_report() {
    let mut h = Harness::new();
    let ready = ReportOutcome::Ready {
        file: "f.pdf".into(),
        folder: "dir".into(),
        pages: 3,
    };
    assert_eq!(
        events(h.input(Input::ReportFinished(ready))),
        [Event::ReportReady {
            file: "f.pdf".into(),
            folder: "dir".into(),
            pages: 3
        }]
    );
    assert_eq!(
        events(h.input(Input::ReportFinished(ReportOutcome::Failed))),
        [Event::ReportReady {
            file: String::new(),
            folder: String::new(),
            pages: 0
        }]
    );
}

// -- search --------------------------------------------------------------------------------

fn record(name: &str) -> SongRecord {
    SongRecord {
        filename: name.to_owned(),
        title: Some("T".into()),
        artist: None,
    }
}

#[test]
fn blank_searches_are_ignored() {
    let mut h = Harness::new();
    for query in ["", "   "] {
        assert!(
            h.command(Command::SearchSong {
                query: query.into()
            })
            .is_empty()
        );
    }
}

#[test]
fn a_cached_search_is_answered_without_going_online() {
    let mut h = Harness::new();
    h.songs.cached.insert("moonlight".into(), record("m.mid"));
    let reply = h.command(Command::SearchSong {
        query: "  MoonLight ".into(),
    });
    assert_eq!(
        events(reply),
        [Event::SearchResult(SearchOutcome::Found {
            filename: "m.mid".into(),
            title: Some("T".into()),
            artist: None
        })]
    );
}

#[test]
fn an_uncached_search_goes_online_with_the_cleaned_query() {
    let mut h = Harness::new();
    assert_eq!(
        h.command(Command::SearchSong {
            query: " New Song ".into()
        }),
        [Effect::Search {
            query: "new song".into()
        }]
    );
}

#[test]
fn search_results_are_saved_and_the_first_one_is_reported() {
    let mut h = Harness::new();
    let found = vec![record("a.mid"), record("b.mid")];
    let reply = h.input(Input::SearchFinished {
        query: "q".into(),
        found,
    });
    assert_eq!(
        events(reply),
        [Event::SearchResult(SearchOutcome::Found {
            filename: "a.mid".into(),
            title: Some("T".into()),
            artist: None
        })]
    );
    let saved: Vec<_> = h
        .songs
        .registered
        .borrow()
        .iter()
        .map(|(q, s)| (q.clone(), s.filename.clone()))
        .collect();
    assert_eq!(
        saved,
        [
            ("q".to_owned(), "a.mid".to_owned()),
            ("q".to_owned(), "b.mid".to_owned())
        ]
    );
}

#[test]
fn an_empty_search_result_means_not_found() {
    let mut h = Harness::new();
    let reply = h.input(Input::SearchFinished {
        query: "q".into(),
        found: vec![],
    });
    assert_eq!(
        events(reply),
        [Event::SearchResult(SearchOutcome::NotFound)]
    );
    assert!(h.songs.registered.borrow().is_empty());
}
