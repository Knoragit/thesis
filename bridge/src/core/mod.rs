//! The bridge's state machine, without I/O.
//!
//! [`Core::handle`] takes one [`Input`] (a command from Unity, a note from the keyboard, or the
//! result of background work) and returns the [`Effect`]s for the runtime to carry out. Time is
//! passed in as an argument and the song library is reached through a trait; the module uses no
//! sockets, files, threads or clocks.
//!
//! State: the follow-me chord cursor, the recorded notes for the report, and whether a song is
//! playing.

pub mod examples;
pub mod follow;
pub mod recorder;
pub mod report;

use std::path::PathBuf;

use crate::library::{SongLibrary, SongRecord};
use crate::midi::{NoteKind, NoteMessage};
use crate::protocol::{Challenge, Command, Event, SearchOutcome, SongRef};

use follow::{Follow, Step};
use recorder::Recorder;
use report::{ReportOutcome, SessionData};

/// Identifies one observer playback, so a late "finished" report cannot be mistaken for the
/// current song's.
pub type PlaybackId = u64;

const NOT_IN_DATABASE: &str = "Brano non trovato nel database.";
const FILE_MISSING: &str = "File del brano non trovato.";
const FILE_UNREADABLE: &str = "Impossibile leggere il file del brano.";

/// Requests closer together than this produce one report.
const REPORT_DEBOUNCE_SECONDS: f64 = 5.0;

#[derive(Debug)]
pub enum Input {
    Command(Command),
    /// A key pressed or released on the keyboard.
    Note(NoteMessage),
    PlaybackFinished(PlaybackId),
    ReportFinished(ReportOutcome),
    /// The online search for `query` ended; `found` holds the songs that passed validation.
    SearchFinished {
        query: String,
        found: Vec<SongRecord>,
    },
}

#[derive(Debug, PartialEq)]
pub enum Effect {
    Send(Event),
    /// Stop any current song, then play this one on the piano and stream it to Unity, starting
    /// `start_ms` milliseconds in.
    StartPlayback {
        id: PlaybackId,
        path: PathBuf,
        start_ms: u64,
    },
    StopPlayback,
    StartExample(Challenge),
    StopExample,
    RenderReport(SessionData),
    Search {
        query: String,
    },
}

#[derive(Debug, Default)]
pub struct Core {
    playback: Option<PlaybackId>,
    next_playback_id: PlaybackId,
    follow: Follow,
    recorder: Recorder,
    last_report_at: Option<f64>,
    report_in_flight: bool,
}

impl Core {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Processes one input. `now` is a monotonic clock in seconds.
    pub fn handle(&mut self, input: Input, now: f64, songs: &dyn SongLibrary) -> Vec<Effect> {
        let mut effects = Vec::new();
        match input {
            Input::Command(command) => self.command(command, now, songs, &mut effects),
            Input::Note(note) => self.note(note, now, &mut effects),
            Input::PlaybackFinished(id) => {
                if self.playback == Some(id) {
                    self.playback = None;
                }
            }
            Input::ReportFinished(outcome) => {
                self.report_in_flight = false;
                effects.push(Effect::Send(outcome.into_event()));
            }
            Input::SearchFinished { query, found } => {
                effects.push(Effect::Send(Event::SearchResult(finish_search(
                    &query, &found, songs,
                ))));
            }
        }
        effects
    }

    fn command(
        &mut self,
        command: Command,
        now: f64,
        songs: &dyn SongLibrary,
        effects: &mut Vec<Effect>,
    ) {
        match command {
            Command::Ping => effects.push(send(Event::BridgeReady)),
            Command::ListSongs => {
                if let Some(list) = log_error("list songs", songs.list()) {
                    effects.push(send(Event::SongList {
                        songs: list,
                        suggest: false,
                    }));
                }
            }
            Command::SearchSuggest { query } => {
                if let Some(list) = log_error("suggest songs", songs.suggest(&query)) {
                    effects.push(send(Event::SongList {
                        songs: list,
                        suggest: true,
                    }));
                }
            }
            Command::SearchSong { query } => Self::search(&query, songs, effects),
            Command::PlaySong { song, start_ms } => self.play_song(&song, start_ms, songs, effects),
            Command::PlaySongFollow(song) => self.follow_song(&song, songs, effects),
            Command::NextStep => effects.push(self.next_step()),
            Command::StopSong => {
                self.playback = None;
                self.follow.stop();
                effects.push(Effect::StopPlayback);
                effects.push(send(Event::ClearScene));
            }
            Command::PlayExample(challenge) => {
                // Demonstrations never run over a song; Unity is told to carry on regardless.
                if self.playback.is_some() || self.follow.is_active() {
                    effects.push(send(Event::ExampleDone));
                } else {
                    effects.push(Effect::StartExample(challenge));
                }
            }
            Command::StopExample => effects.push(Effect::StopExample),
            Command::StartFaiTu => self.recorder.start(),
            Command::StopFaiTu => self.recorder.stop(),
            Command::NoteHand { note, hand } => self.recorder.hint_hand(note, hand),
            Command::GenerateReport => self.generate_report(now, effects),
            Command::GetReportStatus => {
                effects.push(send(Event::ReportStatus {
                    has_material: self.has_report_material(),
                }));
            }
        }
    }

    fn search(query: &str, songs: &dyn SongLibrary, effects: &mut Vec<Effect>) {
        let query = query.trim().to_lowercase();
        if query.is_empty() {
            return;
        }
        match log_error("look up search", songs.cached_search(&query)) {
            Some(Some(SongRecord {
                filename,
                title,
                artist,
            })) => {
                effects.push(send(Event::SearchResult(SearchOutcome::Found {
                    filename,
                    title,
                    artist,
                })));
            }
            Some(None) => effects.push(Effect::Search { query }),
            None => {}
        }
    }

    /// Finds the song file a play command refers to, or the message to tell Unity why not.
    fn locate(song: &SongRef, songs: &dyn SongLibrary) -> Result<String, &'static str> {
        let filename = match song {
            SongRef::Id(id) => log_error("look up song", songs.filename_by_id(*id))
                .flatten()
                .ok_or(NOT_IN_DATABASE)?,
            SongRef::Filename(name) => name.clone(),
            SongRef::Missing => return Err(NOT_IN_DATABASE),
        };
        songs
            .song_file(&filename)
            .map(|_| filename)
            .ok_or(FILE_MISSING)
    }

    fn play_song(
        &mut self,
        song: &SongRef,
        start_ms: u64,
        songs: &dyn SongLibrary,
        effects: &mut Vec<Effect>,
    ) {
        match Self::locate(song, songs) {
            Ok(filename) => {
                let id = self.next_playback_id;
                self.next_playback_id += 1;
                self.playback = Some(id);
                let path = songs.song_file(&filename).expect("located a moment ago");
                effects.push(Effect::StartPlayback { id, path, start_ms });
            }
            Err(message) => effects.push(send(Event::PlayError(message.to_owned()))),
        }
    }

    fn follow_song(&mut self, song: &SongRef, songs: &dyn SongLibrary, effects: &mut Vec<Effect>) {
        let located = Self::locate(song, songs).and_then(|filename| {
            let notes =
                log_error("read song", songs.reference_notes(&filename)).ok_or(FILE_UNREADABLE)?;
            Ok((filename, notes))
        });
        match located {
            Ok((filename, notes)) => {
                self.follow.begin(&filename, notes);
                effects.push(self.next_step());
            }
            Err(message) => effects.push(send(Event::PlayError(message.to_owned()))),
        }
    }

    fn next_step(&mut self) -> Effect {
        send(match self.follow.advance() {
            Step::Chord(notes) => Event::ExpectNotes(notes),
            Step::Finished => Event::ClearScene,
        })
    }

    fn note(&mut self, note: NoteMessage, now: f64, effects: &mut Vec<Effect>) {
        let velocity = note.unit_velocity();
        match note.kind {
            NoteKind::Press => {
                effects.push(send(Event::Press {
                    note: note.note,
                    velocity,
                }));
                self.recorder.press(note.note, velocity, now);
                self.follow.press(note.note, velocity, now);
            }
            NoteKind::Release => {
                effects.push(send(Event::Release {
                    note: note.note,
                    velocity,
                }));
                self.recorder.release(note.note, now);
                self.follow.release(note.note, now);
            }
        }
    }

    fn generate_report(&mut self, now: f64, effects: &mut Vec<Effect>) {
        // Asking for a report ends the free-play take, even if the request is then dropped.
        self.recorder.stop();
        let too_soon = self
            .last_report_at
            .is_some_and(|last| now - last < REPORT_DEBOUNCE_SECONDS);
        if too_soon || self.report_in_flight {
            tracing::debug!("duplicate report request ignored");
            return;
        }
        self.last_report_at = Some(now);
        self.report_in_flight = true;
        effects.push(Effect::RenderReport(SessionData::new(
            self.recorder.notes().to_vec(),
            self.follow.played_sessions(),
        )));
    }

    fn has_report_material(&self) -> bool {
        !self.recorder.notes().is_empty() || !self.follow.played_sessions().is_empty()
    }
}

fn send(event: Event) -> Effect {
    Effect::Send(event)
}

/// Stores a finished search and picks the answer for Unity.
fn finish_search(query: &str, found: &[SongRecord], songs: &dyn SongLibrary) -> SearchOutcome {
    for song in found {
        log_error("save song", songs.register(query, song));
    }
    match found.first() {
        Some(first) => SearchOutcome::Found {
            filename: first.filename.clone(),
            title: first.title.clone(),
            artist: first.artist.clone(),
        },
        None => SearchOutcome::NotFound,
    }
}

/// A library failure never takes the bridge down: it is logged and the request gets no answer.
fn log_error<T>(what: &str, result: Result<T, crate::library::LibraryError>) -> Option<T> {
    result
        .map_err(|error| tracing::warn!(%error, "could not {what}"))
        .ok()
}

#[cfg(test)]
mod tests;
