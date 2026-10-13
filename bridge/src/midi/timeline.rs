//! A MIDI file flattened into one list of messages with real-time delays.
//!
//! This reproduces how `mido.MidiFile` iterates a file, because the original bridge's playback
//! timing is defined by it: all tracks are merged by absolute tick (stable between tracks), a
//! tempo change applies to the delays after it, and the trailing `end_of_track` keeps its delay.

use std::path::Path;

use midly::{Format, MetaMessage, MidiMessage, Smf, Timing, TrackEventKind, live::LiveEvent};
use thiserror::Error;

use super::Message;

const DEFAULT_TEMPO_MICROS: u32 = 500_000;

#[derive(Debug, Error)]
pub enum TimelineError {
    #[error("cannot read MIDI file: {0}")]
    Io(#[from] std::io::Error),
    #[error("invalid MIDI file: {0}")]
    Parse(#[from] midly::Error),
    #[error("unsupported MIDI file: {0}")]
    Unsupported(&'static str),
}

/// One step of a song: wait `delay` seconds, then send `message` (if it is a playable one).
///
/// Meta events carry no message but still contribute their delay, as in the original.
#[derive(Debug, Clone, PartialEq)]
pub struct TimedMessage {
    pub delay: f64,
    pub message: Option<Message>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Timeline {
    pub steps: Vec<TimedMessage>,
}

impl Timeline {
    /// Reads and flattens a file.
    pub fn load(path: &Path) -> Result<Self, TimelineError> {
        Self::parse(&std::fs::read(path)?)
    }

    pub fn parse(bytes: &[u8]) -> Result<Self, TimelineError> {
        let smf = Smf::parse(bytes)?;
        let Timing::Metrical(ticks_per_beat) = smf.header.timing else {
            return Err(TimelineError::Unsupported("SMPTE time division"));
        };
        if smf.header.format == Format::Sequential {
            return Err(TimelineError::Unsupported("type 2 (asynchronous) file"));
        }
        let ticks_per_beat = f64::from(ticks_per_beat.as_int());

        let mut items = Vec::new();
        for track in &smf.tracks {
            let mut tick = 0u64;
            for event in track {
                tick += u64::from(event.delta.as_int());
                items.push((tick, &event.kind));
            }
        }
        items.sort_by_key(|(tick, _)| *tick); // stable: ties keep track order

        let mut steps = Vec::with_capacity(items.len() + 1);
        let mut tempo = DEFAULT_TEMPO_MICROS;
        let mut previous_tick = 0u64;
        let mut deferred_ticks = 0u64; // delay of dropped end_of_track events

        for (tick, kind) in items {
            let delta = tick - previous_tick;
            previous_tick = tick;
            if matches!(kind, TrackEventKind::Meta(MetaMessage::EndOfTrack)) {
                deferred_ticks += delta;
                continue;
            }
            let delay_ticks = std::mem::take(&mut deferred_ticks) + delta;
            steps.push(TimedMessage {
                delay: seconds(delay_ticks, tempo, ticks_per_beat),
                message: playable(kind),
            });
            if let TrackEventKind::Meta(MetaMessage::Tempo(micros)) = kind {
                tempo = micros.as_int();
            }
        }
        steps.push(TimedMessage {
            delay: seconds(deferred_ticks, tempo, ticks_per_beat),
            message: None,
        });
        Ok(Self { steps })
    }

    /// Total duration in seconds (mido's `MidiFile.length`).
    #[must_use]
    pub fn length(&self) -> f64 {
        self.steps.iter().map(|step| step.delay).sum()
    }

    /// The playable messages with their absolute time in seconds, accumulated the way the
    /// original bridge does (`accumulated_time += msg.time`).
    pub fn timed_messages(&self) -> impl Iterator<Item = (f64, &Message)> {
        let mut clock = 0.0;
        self.steps.iter().filter_map(move |step| {
            clock += step.delay;
            step.message.as_ref().map(|message| (clock, message))
        })
    }
}

/// `mido.tick2second`, with the same operation order so results match bit for bit.
fn seconds(ticks: u64, tempo_micros: u32, ticks_per_beat: f64) -> f64 {
    if ticks == 0 {
        return 0.0;
    }
    let scale = f64::from(tempo_micros) * 1e-6 / ticks_per_beat;
    ticks as f64 * scale
}

fn playable(kind: &TrackEventKind) -> Option<Message> {
    match kind {
        TrackEventKind::Midi { channel, message } => {
            let mut bytes = Vec::with_capacity(3);
            LiveEvent::Midi {
                channel: *channel,
                message: *message,
            }
            .write_std(&mut bytes)
            .ok()?;
            debug_assert!(!matches!(message, MidiMessage::NoteOn { .. }) || bytes.len() == 3);
            Some(Message::from_bytes(bytes))
        }
        TrackEventKind::SysEx(data) => {
            let payload = data.strip_suffix(&[0xf7]).unwrap_or(data);
            let mut bytes = Vec::with_capacity(payload.len() + 2);
            bytes.push(0xf0);
            bytes.extend_from_slice(payload);
            bytes.push(0xf7);
            Some(Message::from_bytes(bytes))
        }
        TrackEventKind::Escape(_) | TrackEventKind::Meta(_) => None,
    }
}

#[cfg(test)]
mod tests {
    use midly::{
        Header, MetaMessage, MidiMessage, Smf, Timing, TrackEvent, TrackEventKind, num::u4,
        num::u7, num::u15, num::u24, num::u28,
    };

    use super::*;

    type Track<'a> = Vec<TrackEvent<'a>>;

    fn at(delta: u32, kind: TrackEventKind<'_>) -> TrackEvent<'_> {
        TrackEvent {
            delta: u28::new(delta),
            kind,
        }
    }

    fn tempo(delta: u32, micros: u32) -> TrackEvent<'static> {
        at(
            delta,
            TrackEventKind::Meta(MetaMessage::Tempo(u24::new(micros))),
        )
    }

    fn end(delta: u32) -> TrackEvent<'static> {
        at(delta, TrackEventKind::Meta(MetaMessage::EndOfTrack))
    }

    fn note(delta: u32, on: bool, key: u8) -> TrackEvent<'static> {
        let (key, vel) = (u7::new(key), u7::new(if on { 100 } else { 0 }));
        let message = if on {
            MidiMessage::NoteOn { key, vel }
        } else {
            MidiMessage::NoteOff { key, vel }
        };
        at(
            delta,
            TrackEventKind::Midi {
                channel: u4::new(0),
                message,
            },
        )
    }

    fn file(format: Format, tracks: Vec<Track<'_>>) -> Vec<u8> {
        let mut smf = Smf::new(Header::new(format, Timing::Metrical(u15::new(480))));
        smf.tracks = tracks;
        let mut out = Vec::new();
        smf.write_std(&mut out).unwrap();
        out
    }

    fn delays(timeline: &Timeline) -> Vec<f64> {
        timeline.steps.iter().map(|s| s.delay).collect()
    }

    #[test]
    fn uses_the_default_tempo_until_one_is_set() {
        let bytes = file(
            Format::SingleTrack,
            vec![vec![note(0, true, 60), note(480, false, 60), end(0)]],
        );
        let timeline = Timeline::parse(&bytes).unwrap();
        assert_eq!(delays(&timeline), [0.0, 0.5, 0.0]);
    }

    #[test]
    fn a_tempo_change_applies_to_the_delays_after_it() {
        let bytes = file(
            Format::SingleTrack,
            vec![vec![
                tempo(0, 250_000),
                note(0, true, 60),
                tempo(480, 125_000),  // this delay still uses 250000
                note(480, false, 60), // this one uses 125000
                end(0),
            ]],
        );
        assert_eq!(
            delays(&Timeline::parse(&bytes).unwrap()),
            [0.0, 0.0, 0.25, 0.125, 0.0]
        );
    }

    #[test]
    fn tracks_are_merged_by_tick_and_ties_keep_track_order() {
        let conductor = vec![tempo(0, 250_000), end(960)];
        let a = vec![note(0, true, 60), note(480, false, 60), end(0)];
        let b = vec![note(0, true, 72), note(480, false, 72), end(0)];
        let timeline = Timeline::parse(&file(Format::Parallel, vec![conductor, a, b])).unwrap();
        let keys: Vec<u8> = timeline
            .timed_messages()
            .map(|(_, m)| m.as_bytes()[1])
            .collect();
        assert_eq!(keys, [60, 72, 60, 72]);
    }

    #[test]
    fn the_longest_end_of_track_delays_the_final_step() {
        let a = vec![note(0, true, 60), note(480, false, 60), end(0)];
        let b = vec![end(1920)];
        let timeline = Timeline::parse(&file(Format::Parallel, vec![a, b])).unwrap();
        // Four beats at 120 BPM = 2 s in total, the last 1.5 s being only the end_of_track wait.
        assert!((timeline.length() - 2.0).abs() < 1e-12);
        let last = timeline.steps.last().unwrap();
        assert_eq!(last.message, None);
        assert!((last.delay - 1.5).abs() < 1e-12);
    }

    #[test]
    fn timed_messages_accumulate_absolute_time() {
        let bytes = file(
            Format::SingleTrack,
            vec![vec![
                tempo(0, 250_000),
                note(0, true, 60),
                note(480, false, 60),
                note(480, true, 62),
                end(0),
            ]],
        );
        let times: Vec<f64> = Timeline::parse(&bytes)
            .unwrap()
            .timed_messages()
            .map(|(t, _)| t)
            .collect();
        assert_eq!(times, [0.0, 0.25, 0.5]);
    }

    #[test]
    fn channel_messages_and_sysex_are_forwarded_as_raw_bytes() {
        let kinds = [
            TrackEventKind::Midi {
                channel: u4::new(1),
                message: MidiMessage::PitchBend {
                    bend: midly::PitchBend(midly::num::u14::new(1000 + 8192)),
                },
            },
            TrackEventKind::Midi {
                channel: u4::new(0),
                message: MidiMessage::Controller {
                    controller: u7::new(64),
                    value: u7::new(127),
                },
            },
            TrackEventKind::Midi {
                channel: u4::new(0),
                message: MidiMessage::ProgramChange {
                    program: u7::new(5),
                },
            },
            TrackEventKind::Midi {
                channel: u4::new(0),
                message: MidiMessage::ChannelAftertouch { vel: u7::new(30) },
            },
            TrackEventKind::SysEx(&[1, 2, 3, 0xf7]),
        ];
        let track: Track = kinds
            .into_iter()
            .map(|k| at(0, k))
            .chain([end(0)])
            .collect();
        let timeline = Timeline::parse(&file(Format::SingleTrack, vec![track])).unwrap();
        let bytes: Vec<Vec<u8>> = timeline
            .timed_messages()
            .map(|(_, m)| m.as_bytes().to_vec())
            .collect();
        assert_eq!(
            bytes,
            [
                vec![0xe1, 0x68, 0x47], // pitch bend 9192 = 0x47 << 7 | 0x68
                vec![0xb0, 64, 127],
                vec![0xc0, 5],
                vec![0xd0, 30],
                vec![0xf0, 1, 2, 3, 0xf7],
            ]
        );
    }

    #[test]
    fn rejects_unsupported_files() {
        let sequential = file(Format::Sequential, vec![vec![end(0)], vec![end(0)]]);
        assert!(matches!(
            Timeline::parse(&sequential),
            Err(TimelineError::Unsupported(_))
        ));
        assert!(Timeline::parse(b"not a midi file").is_err());
        assert!(Timeline::parse(&[]).is_err());
    }
}
