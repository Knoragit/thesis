//! Follow-me ("Seguimi") mode: the chords still to play and what the user played.

use std::collections::HashMap;

use serde::Serialize;

use crate::library::parse_song_info;
use crate::midi::analysis::{self, ReferenceNote, TimedChord};

/// A chord starting this close after a turn boundary still belongs to that turn: bar times and
/// note times are accumulated differently and can disagree by rounding.
const TURN_TOLERANCE: f64 = 0.001;

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
#[derive(Debug, PartialEq)]
pub enum Step {
    Chord(Vec<u8>),
    /// In turns: a chord for the user and when it starts in the song, in seconds.
    TimedChord(Vec<u8>, f64),
    /// In turns: play the song from `start` to `end` (seconds; `None` is the end of the song)
    /// before the user plays the same stretch.
    Demo {
        start: f64,
        end: Option<f64>,
    },
    Finished,
}

#[derive(Debug, Default)]
pub struct Follow {
    active: bool,
    chords: Vec<TimedChord>,
    next: usize,
    /// In turns: where each turn starts, in seconds. The first is always 0.
    turns: Option<Vec<f64>>,
    /// The turn last demonstrated.
    turn: Option<usize>,
    /// A demonstration is playing: the user's notes are not recorded.
    demonstrating: bool,
    held: HashMap<u8, (f64, f64)>,
    sessions: Vec<FollowSession>,
}

impl Follow {
    /// Starts following a song; the sequence and history of any earlier song are replaced.
    ///
    /// With `turns` (the start of each turn, in seconds), each stretch is demonstrated before
    /// the user plays it; stretches without notes are skipped.
    pub fn begin(
        &mut self,
        filename: &str,
        reference_notes: Vec<ReferenceNote>,
        turns: Option<Vec<f64>>,
    ) {
        let (title, artist) = parse_song_info(filename);
        self.chords = analysis::timed_chords(&reference_notes);
        self.next = 0;
        self.turns = turns.map(|mut turns| {
            turns.retain(|t| *t > 0.0);
            turns.insert(0, 0.0);
            turns
        });
        self.turn = None;
        self.demonstrating = false;
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

    /// The next chord to show, or, in turns, the demonstration of the stretch it starts. Past the
    /// last chord, following ends.
    pub fn advance(&mut self) -> Step {
        self.demonstrating = false;
        let Some(chord) = self.chords.get(self.next) else {
            self.active = false;
            return Step::Finished;
        };
        let Some(turns) = &self.turns else {
            self.next += 1;
            return Step::Chord(chord.notes.clone());
        };
        let turn = turns
            .partition_point(|start| *start <= chord.start + TURN_TOLERANCE)
            .saturating_sub(1);
        if self.turn != Some(turn) {
            self.turn = Some(turn);
            self.demonstrating = true;
            return Step::Demo {
                start: turns[turn],
                end: turns.get(turn + 1).copied(),
            };
        }
        self.next += 1;
        Step::TimedChord(chord.notes.clone(), chord.start)
    }

    #[must_use]
    pub fn is_demonstrating(&self) -> bool {
        self.active && self.demonstrating
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
        if self.active && !self.demonstrating {
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

    impl Follow {
        fn begin_plain(&mut self, filename: &str, notes: Vec<ReferenceNote>) {
            self.begin(filename, notes, None);
        }
    }

    #[test]
    fn in_turns_each_stretch_is_demonstrated_then_played_and_empty_ones_are_skipped() {
        let mut follow = Follow::default();
        // Turns start at 0, 2, 4 and 6 s; nothing happens between 2 and 4 s.
        let notes = song(&[(60, 0.0), (62, 1.0), (64, 4.0), (65, 5.9995), (67, 6.5)]);
        follow.begin("a.mid", notes, Some(vec![2.0, 4.0, 6.0]));
        let demo = |start, end| Step::Demo { start, end };
        assert_eq!(follow.advance(), demo(0.0, Some(2.0)));
        assert!(follow.is_demonstrating());
        follow.press(60, 0.5, 0.1);
        follow.release(60, 0.2);
        assert_eq!(follow.advance(), Step::TimedChord(vec![60], 0.0));
        assert!(!follow.is_demonstrating());
        assert_eq!(follow.advance(), Step::TimedChord(vec![62], 1.0));
        assert_eq!(follow.advance(), demo(4.0, Some(6.0)));
        assert_eq!(follow.advance(), Step::TimedChord(vec![64], 4.0));
        // Just before a boundary by rounding: it belongs to the next turn.
        assert_eq!(follow.advance(), demo(6.0, None));
        assert_eq!(follow.advance(), Step::TimedChord(vec![65], 5.9995));
        assert_eq!(follow.advance(), Step::TimedChord(vec![67], 6.5));
        assert_eq!(follow.advance(), Step::Finished);
        // The note played during the demonstration was not recorded.
        assert!(follow.played_sessions().is_empty());
    }

    #[test]
    fn chords_are_served_in_order_then_it_finishes_repeatedly() {
        let mut follow = Follow::default();
        follow.begin_plain("a.mid", song(&[(60, 0.0), (64, 0.01), (72, 1.0)]));
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
        follow.begin_plain("a.mid", vec![]);
        assert_eq!(follow.advance(), Step::Finished);
    }

    #[test]
    fn stopping_keeps_the_sequence_but_stops_recording() {
        let mut follow = Follow::default();
        follow.begin_plain("a.mid", song(&[(60, 0.0), (72, 1.0)]));
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
        follow.begin_plain("First - Artist.mid", song(&[(60, 0.0)]));
        follow.press(60, 0.5, 1.0);
        follow.release(60, 1.5);
        follow.begin_plain("second.mid", song(&[(62, 0.0)]));
        follow.begin_plain("third.mid", song(&[(64, 0.0)]));
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
        follow.begin_plain("a.mid", song(&[(60, 0.0)]));
        follow.press(60, 0.5, 1.0);
        follow.begin_plain("b.mid", song(&[(60, 0.0)]));
        follow.release(60, 2.0);
        assert!(follow.played_sessions().is_empty());
    }
}
