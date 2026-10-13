//! Tiny MIDI files for unit tests.

use midly::num::{u4, u7, u15, u24, u28};
use midly::{Format, Header, MetaMessage, MidiMessage, Smf, Timing, TrackEvent, TrackEventKind};

fn event(delta: u32, kind: TrackEventKind<'static>) -> TrackEvent<'static> {
    TrackEvent {
        delta: u28::new(delta),
        kind,
    }
}

fn note(delta: u32, on: bool, key: u8) -> TrackEvent<'static> {
    let (key, vel) = (u7::new(key), u7::new(if on { 100 } else { 0 }));
    let message = if on {
        MidiMessage::NoteOn { key, vel }
    } else {
        MidiMessage::NoteOff { key, vel }
    };
    event(
        delta,
        TrackEventKind::Midi {
            channel: u4::new(0),
            message,
        },
    )
}

fn write(track: Vec<TrackEvent<'static>>) -> Vec<u8> {
    let mut smf = Smf::new(Header::new(
        Format::SingleTrack,
        Timing::Metrical(u15::new(480)),
    ));
    smf.tracks.push(track);
    let mut out = Vec::new();
    smf.write_std(&mut out)
        .expect("writing to memory cannot fail");
    out
}

/// Plays each given note for a beat, one after the other (120 BPM).
#[must_use]
pub fn melody(notes: &[u8]) -> Vec<u8> {
    let mut track = vec![event(
        0,
        TrackEventKind::Meta(MetaMessage::Tempo(u24::new(500_000))),
    )];
    for &key in notes {
        track.push(note(0, true, key));
        track.push(note(480, false, key));
    }
    track.push(event(0, TrackEventKind::Meta(MetaMessage::EndOfTrack)));
    write(track)
}

/// More than 60 notes held together: unsafe for the visualizer.
#[must_use]
pub fn hammer() -> Vec<u8> {
    let mut track = Vec::new();
    for key in 21..91 {
        track.push(note(0, true, key));
    }
    for key in 21..91 {
        track.push(note(if key == 21 { 480 } else { 0 }, false, key));
    }
    track.push(event(0, TrackEventKind::Meta(MetaMessage::EndOfTrack)));
    write(track)
}

/// A melody played with the given General MIDI program.
#[must_use]
pub fn with_program(program: u8, key: u8) -> Vec<u8> {
    let change = TrackEventKind::Midi {
        channel: u4::new(0),
        message: MidiMessage::ProgramChange {
            program: u7::new(program),
        },
    };
    write(vec![
        event(0, change),
        note(0, true, key),
        note(480, false, key),
        event(0, TrackEventKind::Meta(MetaMessage::EndOfTrack)),
    ])
}
