//! What the bridge needs to know about a song: its notes, chords, and whether it is usable.

use std::collections::{HashMap, HashSet};
use std::fmt;

use serde::Serialize;

use super::{Message, NoteKind, Timeline};

/// A note of the song as played by the original performer.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct ReferenceNote {
    pub note: u8,
    /// Velocity as 0.0 to 1.0.
    pub vel: f64,
    /// Seconds from the start of the song.
    pub start: f64,
    pub dur: f64,
}

/// Shortest duration given to a reference note.
const MIN_NOTE_DURATION: f64 = 0.05;
/// Notes starting less than this after the previous note belong to the same chord.
const CHORD_WINDOW: f64 = 0.05;

/// Pairs note-ons with their note-offs, ordered by start time.
///
/// Notes are matched by pitch alone (not channel), and are listed in the order they ended
/// before the stable sort by start: the original bridge's behaviour, which fixes the order of
/// notes inside a chord.
#[must_use]
pub fn reference_notes(timeline: &Timeline) -> Vec<ReferenceNote> {
    let mut sounding: HashMap<u8, (f64, f64)> = HashMap::new();
    let mut notes = Vec::new();
    for (time, message) in timeline.timed_messages() {
        let Some(event) = message.note() else {
            continue;
        };
        match event.kind {
            NoteKind::Press => {
                sounding.insert(event.note, (time, event.unit_velocity()));
            }
            NoteKind::Release => {
                if let Some((start, vel)) = sounding.remove(&event.note) {
                    let dur = (time - start).max(MIN_NOTE_DURATION);
                    notes.push(ReferenceNote {
                        note: event.note,
                        vel,
                        start,
                        dur,
                    });
                }
            }
        }
    }
    notes.sort_by(|a, b| a.start.total_cmp(&b.start));
    notes
}

/// Groups notes into chords: each note joins the chord if it starts within the chord window of
/// the *previous note* (so a run of closely spaced notes chains into one chord). Repeated pitches
/// inside a chord are listed once.
#[must_use]
pub fn chords(notes: &[ReferenceNote]) -> Vec<Vec<u8>> {
    let mut chords: Vec<Vec<u8>> = Vec::new();
    let mut previous_start: Option<f64> = None;
    for note in notes {
        let continues = previous_start.is_none_or(|prev| note.start - prev < CHORD_WINDOW);
        match chords.last_mut() {
            Some(chord) if continues => {
                if !chord.contains(&note.note) {
                    chord.push(note.note);
                }
            }
            _ => chords.push(vec![note.note]),
        }
        previous_start = Some(note.start);
    }
    chords
}

/// Whether the song has a piano part: a General MIDI piano-family program (0-7), or no program
/// changes at all (which defaults to piano).
#[must_use]
pub fn has_piano_part(timeline: &Timeline) -> bool {
    let mut any_program = false;
    for step in &timeline.steps {
        if let Some(program) = step.message.as_ref().and_then(Message::program_change) {
            any_program = true;
            if program <= 7 {
                return true;
            }
        }
    }
    !any_program
}

/// Why a song would swamp the visualizer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Unsafe {
    TooManySimultaneousNotes(usize),
    OutsidePianoRange {
        lowest: u8,
        highest: u8,
    },
    ExtremeEventRate(u32),
    WideAndBusy {
        pitches: usize,
        events_per_second: u32,
    },
}

impl fmt::Display for Unsafe {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::TooManySimultaneousNotes(n) => write!(f, "too many simultaneous notes ({n})"),
            Self::OutsidePianoRange { lowest, highest } => {
                write!(f, "notes outside the 88-key range ({lowest}..{highest})")
            }
            Self::ExtremeEventRate(rate) => write!(f, "extreme note rate ({rate} events/s)"),
            Self::WideAndBusy {
                pitches,
                events_per_second,
            } => {
                write!(
                    f,
                    "wide keyboard coverage ({pitches} pitches) and high rate ({events_per_second} events/s)"
                )
            }
        }
    }
}

const MAX_SIMULTANEOUS: usize = 60;
const LOWEST_PIANO_NOTE: u8 = 21;
const HIGHEST_PIANO_NOTE: u8 = 108;
const MAX_EVENTS_PER_SECOND: f64 = 150.0;
const WIDE_COVERAGE_PITCHES: usize = 70;
const WIDE_COVERAGE_EVENTS_PER_SECOND: f64 = 60.0;

/// Rejects "hammer" files (game or sequencer rips) that would leave every key looking stuck.
pub fn check_visualizer_safety(timeline: &Timeline) -> Result<(), Unsafe> {
    let mut pitches = HashSet::new();
    let mut held = HashSet::new();
    let mut peak_held = 0;
    let mut note_events = 0u32;
    for (_, message) in timeline.timed_messages() {
        let Some(event) = message.note() else {
            continue;
        };
        note_events += 1;
        match event.kind {
            NoteKind::Press => {
                pitches.insert(event.note);
                held.insert(event.note);
                peak_held = peak_held.max(held.len());
            }
            NoteKind::Release => {
                held.remove(&event.note);
            }
        }
    }
    let events_per_second = f64::from(note_events) / timeline.length().max(0.001);
    // Whole events per second are enough for the (log-only) reason.
    let rate = events_per_second as u32;

    if peak_held > MAX_SIMULTANEOUS {
        return Err(Unsafe::TooManySimultaneousNotes(peak_held));
    }
    if let (Some(&lowest), Some(&highest)) = (pitches.iter().min(), pitches.iter().max())
        && (lowest < LOWEST_PIANO_NOTE || highest > HIGHEST_PIANO_NOTE)
    {
        return Err(Unsafe::OutsidePianoRange { lowest, highest });
    }
    if events_per_second > MAX_EVENTS_PER_SECOND {
        return Err(Unsafe::ExtremeEventRate(rate));
    }
    if pitches.len() > WIDE_COVERAGE_PITCHES && events_per_second > WIDE_COVERAGE_EVENTS_PER_SECOND
    {
        return Err(Unsafe::WideAndBusy {
            pitches: pitches.len(),
            events_per_second: rate,
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::midi::TimedMessage;

    /// Builds a timeline from (absolute seconds, message) pairs plus a trailing wait.
    fn timeline(events: &[(f64, Message)], end: f64) -> Timeline {
        let mut steps = Vec::new();
        let mut clock = 0.0;
        for (time, message) in events {
            steps.push(TimedMessage {
                delay: time - clock,
                message: Some(message.clone()),
            });
            clock = *time;
        }
        steps.push(TimedMessage {
            delay: end - clock,
            message: None,
        });
        Timeline { steps }
    }

    fn on(note: u8) -> Message {
        Message::note_on(0, note, 100)
    }

    fn off(note: u8) -> Message {
        Message::note_on(0, note, 0)
    }

    #[test]
    fn notes_are_listed_in_note_off_order_then_stably_sorted_by_start() {
        // 67 starts first but ends last: the chord comes out as 60, 64, 67.
        let song = timeline(
            &[
                (0.0, on(67)),
                (0.0, on(60)),
                (0.0, on(64)),
                (0.25, off(60)),
                (0.375, off(64)),
                (0.5, off(67)),
            ],
            0.5,
        );
        let notes = reference_notes(&song);
        assert_eq!(
            notes.iter().map(|n| n.note).collect::<Vec<_>>(),
            [60, 64, 67]
        );
        assert!((notes[0].dur - 0.25).abs() < 1e-12);
        assert!((notes[0].vel - 100.0 / 127.0).abs() < 1e-12);
    }

    #[test]
    fn very_short_notes_get_the_minimum_duration_and_orphans_are_dropped() {
        let song = timeline(
            &[
                (0.0, on(60)),
                (0.01, off(60)),
                (0.02, off(61)),
                (0.03, on(62)),
            ],
            1.0,
        );
        let notes = reference_notes(&song);
        assert_eq!(notes.len(), 1);
        assert!((notes[0].dur - MIN_NOTE_DURATION).abs() < 1e-12);
    }

    #[test]
    fn a_repeated_note_on_replaces_the_pending_one() {
        let song = timeline(&[(0.0, on(60)), (0.5, on(60)), (1.0, off(60))], 1.0);
        let notes = reference_notes(&song);
        assert_eq!(notes.len(), 1);
        assert!((notes[0].start - 0.5).abs() < 1e-12);
    }

    fn note_at(note: u8, start: f64) -> ReferenceNote {
        ReferenceNote {
            note,
            vel: 0.5,
            start,
            dur: 0.1,
        }
    }

    #[test]
    fn closely_spaced_notes_chain_into_one_chord() {
        let notes = [
            note_at(48, 1.0),
            note_at(52, 1.04),
            note_at(55, 1.08),
            note_at(60, 1.2),
        ];
        assert_eq!(chords(&notes), [vec![48, 52, 55], vec![60]]);
    }

    #[test]
    fn the_chord_window_is_exclusive_and_duplicates_collapse() {
        let notes = [
            note_at(60, 0.0),
            note_at(64, 0.05),
            note_at(64, 0.06),
            note_at(64, 0.07),
        ];
        assert_eq!(chords(&notes), [vec![60], vec![64]]);
        assert!(chords(&[]).is_empty());
    }

    #[test]
    fn piano_detection() {
        let program = |p: u8| Message::from_bytes(vec![0xc0, p]);
        assert!(
            has_piano_part(&timeline(&[], 1.0)),
            "no program change defaults to piano"
        );
        assert!(has_piano_part(&timeline(&[(0.0, program(0))], 1.0)));
        assert!(has_piano_part(&timeline(
            &[(0.0, program(40)), (0.1, program(7))],
            1.0
        )));
        assert!(!has_piano_part(&timeline(
            &[(0.0, program(40)), (0.1, program(8))],
            1.0
        )));
    }

    #[test]
    fn ordinary_songs_are_safe() {
        let song = timeline(
            &[(0.0, on(60)), (0.5, off(60)), (0.5, on(64)), (1.0, off(64))],
            1.0,
        );
        assert_eq!(check_visualizer_safety(&song), Ok(()));
        assert_eq!(check_visualizer_safety(&timeline(&[], 0.0)), Ok(()));
    }

    #[test]
    fn too_many_simultaneous_notes_are_unsafe() {
        let held: Vec<_> = (21..=81).map(|n| (0.0, on(n))).collect(); // 61 notes at once
        assert_eq!(
            check_visualizer_safety(&timeline(&held, 1.0)),
            Err(Unsafe::TooManySimultaneousNotes(61))
        );
        assert_eq!(check_visualizer_safety(&timeline(&held[..60], 1.0)), Ok(()));
    }

    #[test]
    fn notes_outside_the_piano_range_are_unsafe() {
        let low = timeline(&[(0.0, on(20)), (0.5, off(20))], 1.0);
        assert_eq!(
            check_visualizer_safety(&low),
            Err(Unsafe::OutsidePianoRange {
                lowest: 20,
                highest: 20
            })
        );
        let high = timeline(&[(0.0, on(109))], 1.0);
        assert!(check_visualizer_safety(&high).is_err());
        let edges = timeline(&[(0.0, on(21)), (0.1, on(108))], 1.0);
        assert_eq!(check_visualizer_safety(&edges), Ok(()));
    }

    #[test]
    fn an_extreme_note_rate_is_unsafe() {
        // 160 note events in one second.
        let events: Vec<_> = (0..80)
            .flat_map(|i| {
                let t = f64::from(i) / 80.0;
                [(t, on(60)), (t + 0.001, off(60))]
            })
            .collect();
        assert!(matches!(
            check_visualizer_safety(&timeline(&events, 1.0)),
            Err(Unsafe::ExtremeEventRate(_))
        ));
    }
}
