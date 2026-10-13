//! Records what the user plays: the free-play ("Fai Tu") take.

use std::collections::HashMap;

use serde::Serialize;

use crate::protocol::Hand;

/// A key press paired with its release.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct RecordedNote {
    pub note: u8,
    pub vel: f64,
    pub start: f64,
    pub end: f64,
    /// Which hand played it, if Unity's hand tracking said so.
    pub hand: Option<Hand>,
}

/// Pairs presses with releases while recording is on.
#[derive(Debug, Default)]
pub struct Recorder {
    recording: bool,
    held: HashMap<u8, (f64, f64)>,
    notes: Vec<RecordedNote>,
    /// Latest hand hint per key, consumed when that key's note is completed.
    hands: HashMap<u8, Hand>,
}

impl Recorder {
    /// Starts a new take, discarding the previous one.
    pub fn start(&mut self) {
        self.recording = true;
        self.notes.clear();
        self.held.clear();
    }

    pub fn stop(&mut self) {
        self.recording = false;
    }

    pub fn hint_hand(&mut self, note: u8, hand: Hand) {
        self.hands.insert(note, hand);
    }

    pub fn press(&mut self, note: u8, velocity: f64, now: f64) {
        if self.recording {
            self.held.insert(note, (velocity, now));
        }
    }

    pub fn release(&mut self, note: u8, now: f64) {
        if !self.recording {
            return;
        }
        if let Some((vel, start)) = self.held.remove(&note) {
            let hand = self.hands.remove(&note);
            self.notes.push(RecordedNote {
                note,
                vel,
                start,
                end: now,
                hand,
            });
        }
    }

    #[must_use]
    pub fn notes(&self) -> &[RecordedNote] {
        &self.notes
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nothing_is_recorded_until_started() {
        let mut recorder = Recorder::default();
        recorder.press(60, 0.5, 1.0);
        recorder.release(60, 2.0);
        assert!(recorder.notes().is_empty());
    }

    #[test]
    fn a_press_and_release_make_one_note() {
        let mut recorder = Recorder::default();
        recorder.start();
        recorder.press(60, 0.5, 1.0);
        recorder.release(60, 2.0);
        assert_eq!(
            recorder.notes(),
            [RecordedNote {
                note: 60,
                vel: 0.5,
                start: 1.0,
                end: 2.0,
                hand: None
            }]
        );
    }

    #[test]
    fn held_notes_and_stray_releases_are_not_notes() {
        let mut recorder = Recorder::default();
        recorder.start();
        recorder.release(61, 1.0);
        recorder.press(60, 0.5, 1.0);
        assert!(recorder.notes().is_empty());
    }

    #[test]
    fn pressing_again_before_release_restarts_the_note() {
        let mut recorder = Recorder::default();
        recorder.start();
        recorder.press(60, 0.4, 1.0);
        recorder.press(60, 0.8, 1.5);
        recorder.release(60, 2.0);
        assert_eq!(recorder.notes().len(), 1);
        assert_eq!(
            (recorder.notes()[0].vel, recorder.notes()[0].start),
            (0.8, 1.5)
        );
    }

    #[test]
    fn hand_hints_are_used_once_when_the_note_completes() {
        let mut recorder = Recorder::default();
        recorder.hint_hand(60, Hand::Left); // hints may arrive before recording starts
        recorder.start();
        recorder.press(60, 0.5, 1.0);
        recorder.release(60, 2.0);
        recorder.press(60, 0.5, 3.0);
        recorder.release(60, 4.0);
        let hands: Vec<_> = recorder.notes().iter().map(|n| n.hand).collect();
        assert_eq!(hands, [Some(Hand::Left), None]);
    }

    #[test]
    fn stopping_keeps_the_take_and_starting_discards_it() {
        let mut recorder = Recorder::default();
        recorder.start();
        recorder.press(60, 0.5, 1.0);
        recorder.release(60, 2.0);
        recorder.stop();
        recorder.press(62, 0.5, 3.0);
        recorder.release(62, 4.0);
        assert_eq!(recorder.notes().len(), 1);
        recorder.start();
        assert!(recorder.notes().is_empty());
    }
}
