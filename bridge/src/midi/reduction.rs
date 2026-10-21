//! A song reduced to one piano part, for engraving its score.
//!
//! The score shows what follow-me asks for: the notes of the song's piano part (see
//! [`crate::midi::analysis::piano_part`]), with the original tempo, time and key signatures and
//! the same ticks, so the bars of the score are the bars the bridge plays. The notes go on two
//! piano tracks, right hand then left hand (see [`crate::midi::hands`]): the engraver reads two
//! piano tracks in a row as one piano, treble staff and bass staff, instead of guessing the split.
//!
//! Only the engraving is tidied: a note lasts until the next note of the same hand, when that
//! comes soon enough, so a staccato performance reads as notes rather than as a run of rests.

use std::collections::HashMap;

use midly::num::{u4, u7, u28};
use midly::{Format, Header, MetaMessage, MidiMessage, Smf, Timing, TrackEvent, TrackEventKind};

use super::TimelineError;
use super::analysis::PianoChannels;
use super::hands;
use super::timeline::merged;

/// A note is stretched to the next note of its hand when that starts within this many beats.
const LEGATO_BEATS: u64 = 2;

/// A key held in the reduction, in ticks.
#[derive(Clone, Copy)]
struct Held {
    key: u7,
    vel: u7,
    start: u64,
    end: u64,
}

/// Writes the piano reduction of a MIDI file as a new MIDI file with one track per hand.
pub fn piano_reduction(bytes: &[u8]) -> Result<Vec<u8>, TimelineError> {
    let smf = Smf::parse(bytes)?;
    let Timing::Metrical(ticks_per_beat) = smf.header.timing else {
        return Err(TimelineError::Unsupported("SMPTE time division"));
    };

    let items = merged(&smf);
    // As in `analysis::piano_part`: the piano, else every instrument but the drums, else all.
    let keeps_a_note = |mut channels: PianoChannels| {
        items.iter().any(|(_, kind)| match kind {
            TrackEventKind::Midi { channel, message } => {
                channels.keeps(channel.as_int(), program(*message))
                    && matches!(message, MidiMessage::NoteOn { vel, .. } if *vel > 0)
            }
            _ => false,
        })
    };
    let (mut channels, filter) = if keeps_a_note(PianoChannels::default()) {
        (PianoChannels::default(), true)
    } else {
        (
            PianoChannels::melodic(),
            keeps_a_note(PianoChannels::melodic()),
        )
    };
    let mut metas: Vec<(u64, TrackEventKind)> = Vec::new();
    let mut notes: Vec<Held> = Vec::new();
    // How many times each key is sounding (the same key on two channels is one key on a
    // piano), and where its note is in `notes`.
    let mut sounding: HashMap<u7, (u32, usize)> = HashMap::new();
    let mut last_tick = 0;
    for (tick, kind) in items {
        last_tick = last_tick.max(tick);
        if let TrackEventKind::Midi {
            channel: from,
            message,
        } = kind
            && !channels.keeps(from.as_int(), program(*message))
            && filter
        {
            continue;
        }
        match kind {
            TrackEventKind::Meta(
                MetaMessage::Tempo(_)
                | MetaMessage::TimeSignature(..)
                | MetaMessage::KeySignature(..),
            ) => metas.push((tick, *kind)),
            TrackEventKind::Midi { message, .. } => match *message {
                MidiMessage::NoteOn { key, vel } if vel > 0 => {
                    let (count, index) = sounding.entry(key).or_insert((0, 0));
                    if *count > 0 {
                        notes[*index].end = tick;
                    }
                    *count += 1;
                    *index = notes.len();
                    notes.push(Held {
                        key,
                        vel,
                        start: tick,
                        end: u64::MAX,
                    });
                }
                MidiMessage::NoteOn { key, .. } | MidiMessage::NoteOff { key, .. } => {
                    if let Some((count, index)) = sounding.get_mut(&key).filter(|(c, _)| *c > 0) {
                        *count -= 1;
                        if *count == 0 {
                            notes[*index].end = notes[*index].end.min(tick);
                        }
                    }
                }
                _ => {}
            },
            _ => {}
        }
    }
    for held in &mut notes {
        held.end = held.end.min(last_tick).max(held.start);
    }

    let beat = u64::from(ticks_per_beat.as_int());
    let keys: Vec<(u8, u64)> = notes.iter().map(|n| (n.key.as_int(), n.start)).collect();
    let (mut right, mut left) = (Vec::new(), Vec::new());
    for (held, is_right) in notes.into_iter().zip(hands::right_hand(&keys, beat)) {
        if is_right { &mut right } else { &mut left }.push(held);
    }
    let mut out = Smf::new(Header::new(
        Format::Parallel,
        Timing::Metrical(ticks_per_beat),
    ));
    out.tracks
        .push(hand_track(b"Mano destra", legato(right, beat), metas));
    out.tracks
        .push(hand_track(b"Mano sinistra", legato(left, beat), Vec::new()));
    let mut bytes = Vec::new();
    out.write_std(&mut bytes)
        .map_err(|e| TimelineError::Io(std::io::Error::other(e.to_string())))?;
    Ok(bytes)
}

/// Stretches each note to the next onset of its hand when that is within `LEGATO_BEATS`, and
/// towards a later one by up to a beat. Notes are never shortened.
fn legato(mut notes: Vec<Held>, beat: u64) -> Vec<Held> {
    let mut onsets: Vec<u64> = notes.iter().map(|n| n.start).collect();
    onsets.sort_unstable();
    onsets.dedup();
    for held in &mut notes {
        let Some(&next) = onsets.get(onsets.partition_point(|&t| t <= held.start)) else {
            continue;
        };
        if held.end >= next {
            continue;
        }
        held.end = if next <= held.start + LEGATO_BEATS * beat {
            next
        } else {
            held.end.max((held.start + beat).min(next))
        };
    }
    notes
}

/// One hand as a piano track: its name, the given meta events and its notes, with releases
/// before presses on the same tick.
fn hand_track<'a>(
    name: &'a [u8],
    notes: Vec<Held>,
    metas: Vec<(u64, TrackEventKind<'a>)>,
) -> Vec<TrackEvent<'a>> {
    let channel = u4::new(0);
    let note = |key: u7, vel: u7| TrackEventKind::Midi {
        channel,
        message: MidiMessage::NoteOn { key, vel },
    };
    // (tick, order: name, program and metas first, then releases, then presses, event)
    let mut events: Vec<(u64, u8, TrackEventKind)> = vec![
        (0, 0, TrackEventKind::Meta(MetaMessage::TrackName(name))),
        (
            0,
            0,
            TrackEventKind::Midi {
                channel,
                message: MidiMessage::ProgramChange {
                    program: u7::new(0),
                },
            },
        ),
    ];
    events.extend(metas.into_iter().map(|(tick, kind)| (tick, 0, kind)));
    for held in notes {
        events.push((held.start, 2, note(held.key, held.vel)));
        events.push((held.end.max(held.start + 1), 1, note(held.key, u7::new(0))));
    }
    events.sort_by_key(|(tick, order, _)| (*tick, *order));
    let end = events.last().map_or(0, |(tick, _, _)| *tick);
    events.push((end, 3, TrackEventKind::Meta(MetaMessage::EndOfTrack)));

    let mut previous = 0;
    events
        .into_iter()
        .map(|(tick, _, kind)| {
            let delta =
                u28::new((tick - previous).min(u64::from(u28::max_value().as_int())) as u32);
            previous = tick;
            TrackEvent { delta, kind }
        })
        .collect()
}

fn program(message: MidiMessage) -> Option<u8> {
    match message {
        MidiMessage::ProgramChange { program } => Some(program.as_int()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::midi::analysis::reference_notes;
    use crate::midi::{NoteKind, Timeline, testing};

    #[test]
    fn the_reduction_keeps_every_note_at_the_same_time() {
        let original = testing::melody(&[60, 62, 64, 48]);
        let reduced = piano_reduction(&original).unwrap();
        let notes = |bytes: &[u8]| reference_notes(&Timeline::parse(bytes).unwrap());
        assert_eq!(notes(&reduced), notes(&original));
        assert_eq!(
            Timeline::parse(&reduced)
                .unwrap()
                .steps
                .iter()
                .find_map(|step| step.message.as_ref())
                .unwrap()
                .as_bytes(),
            [0xc0, 0]
        );
    }

    #[test]
    fn each_hand_gets_a_track_and_notes_last_until_the_next_one() {
        use midly::num::u15;
        let key = |delta, key: u8, vel| TrackEvent {
            delta: u28::new(delta),
            kind: TrackEventKind::Midi {
                channel: u4::new(0),
                message: MidiMessage::NoteOn {
                    key: u7::new(key),
                    vel: u7::new(vel),
                },
            },
        };
        let mut smf = Smf::new(Header::new(
            Format::SingleTrack,
            Timing::Metrical(u15::new(480)),
        ));
        // Staccato eighths a beat apart on 60 and 64, a bass note held, then a note far later.
        smf.tracks.push(vec![
            key(0, 60, 100),
            key(0, 48, 100),
            key(240, 60, 0),
            key(240, 64, 100),
            key(240, 64, 0),
            key(0, 48, 0),
            key(4 * 480, 67, 100),
            key(240, 67, 0),
            TrackEvent {
                delta: u28::new(0),
                kind: TrackEventKind::Meta(MetaMessage::EndOfTrack),
            },
        ]);
        let mut original = Vec::new();
        smf.write_std(&mut original).unwrap();

        let reduced = piano_reduction(&original).unwrap();
        let tracks: Vec<Vec<(u8, u64, u64)>> = Smf::parse(&reduced)
            .unwrap()
            .tracks
            .iter()
            .map(|track| {
                let (mut tick, mut open, mut spans) = (0, HashMap::new(), Vec::new());
                for event in track {
                    tick += u64::from(event.delta.as_int());
                    if let TrackEventKind::Midi {
                        message: MidiMessage::NoteOn { key, vel },
                        ..
                    } = event.kind
                    {
                        if vel > 0 {
                            open.insert(key.as_int(), tick);
                        } else if let Some(start) = open.remove(&key.as_int()) {
                            spans.push((key.as_int(), start, tick));
                        }
                    }
                }
                spans
            })
            .collect();
        // 60 lasts until 64; 64 waits for 67 at most a beat; 67 and the bass note keep theirs.
        assert_eq!(
            tracks,
            [
                vec![(60, 0, 480), (64, 480, 960), (67, 2640, 2880)],
                vec![(48, 0, 720)]
            ]
        );
    }

    #[test]
    fn only_the_piano_part_is_engraved() {
        use midly::num::u15;
        let event = |delta, channel: u8, message| TrackEvent {
            delta: u28::new(delta),
            kind: TrackEventKind::Midi {
                channel: u4::new(channel),
                message,
            },
        };
        let on = |key: u8| MidiMessage::NoteOn {
            key: u7::new(key),
            vel: u7::new(100),
        };
        let mut smf = Smf::new(Header::new(
            Format::SingleTrack,
            Timing::Metrical(u15::new(480)),
        ));
        smf.tracks.push(vec![
            event(
                0,
                1,
                MidiMessage::ProgramChange {
                    program: u7::new(33),
                },
            ),
            event(0, 0, on(60)),
            event(0, 1, on(40)),
            event(0, 9, on(38)),
            TrackEvent {
                delta: u28::new(480),
                kind: TrackEventKind::Meta(MetaMessage::EndOfTrack),
            },
        ]);
        let mut original = Vec::new();
        smf.write_std(&mut original).unwrap();
        let reduced = Timeline::parse(&piano_reduction(&original).unwrap()).unwrap();
        let keys: Vec<u8> = reduced
            .timed_messages()
            .filter_map(|(_, m)| {
                m.note()
                    .filter(|n| n.kind == NoteKind::Press)
                    .map(|n| n.note)
            })
            .collect();
        assert_eq!(keys, [60]);
    }

    #[test]
    fn a_key_held_on_two_channels_is_released_when_both_let_go() {
        use midly::num::u15;
        let on = |delta, channel: u8, vel| TrackEvent {
            delta: u28::new(delta),
            kind: TrackEventKind::Midi {
                channel: u4::new(channel),
                message: MidiMessage::NoteOn {
                    key: u7::new(60),
                    vel: u7::new(vel),
                },
            },
        };
        let mut smf = Smf::new(Header::new(
            Format::SingleTrack,
            Timing::Metrical(u15::new(480)),
        ));
        smf.tracks.push(vec![
            on(0, 0, 100),
            on(240, 1, 90),
            on(240, 0, 0),
            on(240, 1, 0),
            TrackEvent {
                delta: u28::new(0),
                kind: TrackEventKind::Meta(MetaMessage::EndOfTrack),
            },
        ]);
        let mut original = Vec::new();
        smf.write_std(&mut original).unwrap();

        let notes =
            reference_notes(&Timeline::parse(&piano_reduction(&original).unwrap()).unwrap());
        let spans: Vec<_> = notes.iter().map(|n| (n.start, n.start + n.dur)).collect();
        assert_eq!(spans, [(0.0, 0.25), (0.25, 0.75)]);
    }
}
