//! Follow-me ("Seguimi") mode: the chords still to play and what the user played.

use std::collections::HashMap;

use serde::Serialize;

use crate::library::parse_song_info;
use crate::midi::analysis::{self, ReferenceNote};

/// A press paired with its release; the follow-me version has no hand information.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct PlayedNote {
    pub note: u8,
    pub vel: f64,
    pub start: f64,
    pub end: f64,
}

/// One song played in follow-me mode, kept for the report.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct FollowSession {
    pub filename: String,
    pub title: String,
    pub artist: String,
    pub reference_notes: Vec<ReferenceNote>,
    pub user_notes: Vec<PlayedNote>,
}

/// What the next step of a follow-me song is.
#[derive(Debug, PartialEq, Eq)]
pub enum Step {
    Chord(Vec<u8>),
    Finished,
}

#[derive(Debug, Default)]
pub struct Follow {
    active: bool,
    chords: Vec<Vec<u8>>,
    next: usize,
    held: HashMap<u8, (f64, f64)>,
    sessions: Vec<FollowSession>,
}

impl Follow {
    /// Starts following a song; the sequence and history of any earlier song are replaced.
    pub fn begin(&mut self, filename: &str, reference_notes: Vec<ReferenceNote>) {
        let (title, artist) = parse_song_info(filename);
        self.chords = analysis::chords(&reference_notes);
        self.next = 0;
        self.active = true;
        self.held.clear();
        self.sessions.push(FollowSession {
            filename: filename.to_owned(),
            title,
            artist,
            reference_notes,
            user_notes: Vec::new(),
        });
    }

    /// The next chord to show. Past the last one, following ends.
    pub fn advance(&mut self) -> Step {
        if let Some(chord) = self.chords.get(self.next) {
            self.next += 1;
            Step::Chord(chord.clone())
        } else {
            self.active = false;
            Step::Finished
        }
    }

    /// Stops recording the user's notes. The loaded chords stay, as in the original.
    pub fn stop(&mut self) {
        self.active = false;
    }

    #[must_use]
    pub fn is_active(&self) -> bool {
        self.active
    }

    pub fn press(&mut self, note: u8, velocity: f64, now: f64) {
        if self.active {
            self.held.insert(note, (velocity, now));
        }
    }

    pub fn release(&mut self, note: u8, now: f64) {
        if !self.active {
            return;
        }
        if let Some((vel, start)) = self.held.remove(&note)
            && let Some(session) = self.sessions.last_mut()
        {
            session.user_notes.push(PlayedNote {
                note,
                vel,
                start,
                end: now,
            });
        }
    }

    /// The sessions in which the user actually played something.
    #[must_use]
    pub fn played_sessions(&self) -> Vec<FollowSession> {
        self.sessions
            .iter()
            .filter(|s| !s.user_notes.is_empty())
            .cloned()
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn song(notes: &[(u8, f64)]) -> Vec<ReferenceNote> {
        notes
            .iter()
            .map(|&(note, start)| ReferenceNote {
                note,
                vel: 0.5,
                start,
                dur: 0.1,
            })
            .collect()
    }

    #[test]
    fn chords_are_served_in_order_then_it_finishes_repeatedly() {
        let mut follow = Follow::default();
        follow.begin("a.mid", song(&[(60, 0.0), (64, 0.01), (72, 1.0)]));
        assert!(follow.is_active());
        assert_eq!(follow.advance(), Step::Chord(vec![60, 64]));
        assert_eq!(follow.advance(), Step::Chord(vec![72]));
        assert_eq!(follow.advance(), Step::Finished);
        assert!(!follow.is_active());
        assert_eq!(follow.advance(), Step::Finished);
    }

    #[test]
    fn an_empty_song_finishes_immediately() {
        let mut follow = Follow::default();
        follow.begin("a.mid", vec![]);
        assert_eq!(follow.advance(), Step::Finished);
    }

    #[test]
    fn stopping_keeps_the_sequence_but_stops_recording() {
        let mut follow = Follow::default();
        follow.begin("a.mid", song(&[(60, 0.0), (72, 1.0)]));
        assert_eq!(follow.advance(), Step::Chord(vec![60]));
        follow.stop();
        follow.press(60, 0.5, 1.0);
        follow.release(60, 2.0);
        assert!(follow.played_sessions().is_empty());
        assert_eq!(follow.advance(), Step::Chord(vec![72]));
    }

    #[test]
    fn notes_go_to_the_latest_session_and_empty_sessions_are_left_out() {
        let mut follow = Follow::default();
        follow.begin("First - Artist.mid", song(&[(60, 0.0)]));
        follow.press(60, 0.5, 1.0);
        follow.release(60, 1.5);
        follow.begin("second.mid", song(&[(62, 0.0)]));
        follow.begin("third.mid", song(&[(64, 0.0)]));
        follow.press(64, 0.7, 5.0);
        follow.release(64, 5.5);

        let played = follow.played_sessions();
        let names: Vec<_> = played.iter().map(|s| s.filename.as_str()).collect();
        assert_eq!(names, ["First - Artist.mid", "third.mid"]);
        assert_eq!(
            (played[0].title.as_str(), played[0].artist.as_str()),
            ("First", "Artist")
        );
        assert_eq!(
            played[1].user_notes,
            [PlayedNote {
                note: 64,
                vel: 0.7,
                start: 5.0,
                end: 5.5
            }]
        );
    }

    #[test]
    fn a_note_held_across_songs_is_dropped() {
        let mut follow = Follow::default();
        follow.begin("a.mid", song(&[(60, 0.0)]));
        follow.press(60, 0.5, 1.0);
        follow.begin("b.mid", song(&[(60, 0.0)]));
        follow.release(60, 2.0);
        assert!(follow.played_sessions().is_empty());
    }
}
