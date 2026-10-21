//! Observer mode: plays a song on the piano and streams its notes to Unity.
//!
//! A song can start part way through and stop before its end (follow-me demonstrations), and
//! while it plays Unity is told the position every [`PROGRESS_EVERY`] so it can draw a timeline
//! the user can drag.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use super::services::Services;
use crate::core::{Input, PlaybackId};
use crate::midi::{NoteKind, Timeline, analysis};
use crate::protocol::Event;

const POLL: Duration = Duration::from_millis(1);
/// How often `song_progress` is sent while a song plays.
const PROGRESS_EVERY: Duration = Duration::from_millis(250);

/// The song currently playing, if any. At most one plays at a time.
#[derive(Debug, Default)]
pub struct PlaybackSlot {
    current: Option<(Arc<AtomicBool>, JoinHandle<()>)>,
}

impl PlaybackSlot {
    /// Replaces whatever is playing. The old song is fully wound down first (its "stop" output
    /// is sent before the new song's first message), so two songs never overlap.
    pub fn start(
        &mut self,
        id: PlaybackId,
        path: PathBuf,
        (start_ms, end_ms): (u64, Option<u64>),
        services: &Services,
    ) {
        self.cancel();
        if let Some((_, thread)) = self.current.take() {
            let _ = thread.join();
        }
        let cancel = Arc::new(AtomicBool::new(false));
        let thread = {
            let (cancel, services) = (Arc::clone(&cancel), services.clone());
            thread::spawn(move || {
                let end = end_ms.map(Duration::from_millis);
                play(
                    &path,
                    Duration::from_millis(start_ms),
                    end,
                    &cancel,
                    &services,
                );
                services.wake(Input::PlaybackFinished(id));
            })
        };
        self.current = Some((cancel, thread));
    }

    /// Asks the current song to stop; it ends within a millisecond or so.
    pub fn cancel(&self) {
        if let Some((cancel, _)) = &self.current {
            cancel.store(true, Ordering::Relaxed);
        }
    }
}

/// Sleeps until `target`, waking often enough to notice a cancellation.
/// Returns `false` if cancelled.
fn wait_until(target: Instant, cancel: &AtomicBool) -> bool {
    loop {
        if cancel.load(Ordering::Relaxed) {
            return false;
        }
        let remaining = target.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return true;
        }
        thread::sleep(remaining.min(POLL));
    }
}

fn play(
    path: &Path,
    start: Duration,
    end: Option<Duration>,
    cancel: &AtomicBool,
    services: &Services,
) {
    // Only the piano part is played (and shown): the other instruments and the drums of a
    // band arrangement are left out.
    let timeline = match Timeline::load(path) {
        Ok(timeline) => analysis::piano_part(&timeline),
        Err(error) => {
            tracing::warn!(%error, file = %path.display(), "cannot play song");
            services.unity.send(&Event::PlayError(
                "Impossibile leggere il file del brano.".into(),
            ));
            return;
        }
    };
    let duration = timeline.length();
    let start = start.as_secs_f64().min(duration);
    // Where playback stops: the end of the song unless asked to stop earlier.
    let stop = end.map_or(duration, |end| end.as_secs_f64().clamp(start, duration));
    tracing::info!(file = %path.display(), start, stop, "playing");

    services.piano.all_notes_off();
    services.unity.send(&Event::ClearScene);

    // `clock` is the song time of the step just reached; the song time `start` happens at
    // `origin`, so each step's target is `origin + (clock - start)`.
    let origin = Instant::now();
    let progress = |now: Instant| Event::SongProgress {
        position: round_ms(start + now.duration_since(origin).as_secs_f64()).min(duration),
        duration: round_ms(duration),
    };
    services.unity.send(&progress(origin));
    let mut next_progress = origin + PROGRESS_EVERY;

    let mut clock = 0.0;
    let mut finished = true;
    // Keys whose press fell before `start`: their release is dropped too, so Unity never sees
    // a release without its press.
    let mut skipped: HashSet<(u8, u8)> = HashSet::new();
    for step in &timeline.steps {
        clock += step.delay;
        let key = step
            .message
            .as_ref()
            .and_then(|m| Some((m.as_bytes()[0] & 0x0f, m.note()?)));
        if clock < start {
            // Skipped notes stay silent, but pedals, programs and the like still apply, so the
            // piano sounds the same as if the song had been played from the beginning.
            match key {
                Some((channel, note)) if note.kind == NoteKind::Press => {
                    skipped.insert((channel, note.note));
                }
                Some((channel, note)) => {
                    skipped.remove(&(channel, note.note));
                }
                None => {
                    if let Some(message) = &step.message {
                        services.piano.send(message);
                    }
                }
            }
            continue;
        }
        if let Some((channel, note)) = key
            && note.kind == NoteKind::Release
            && skipped.remove(&(channel, note.note))
        {
            continue;
        }
        // Stopping early: notes released right on the boundary still are, then the rest of the
        // stretch is waited out and the song ends as usual.
        let release_on_boundary =
            clock - stop < 1e-6 && key.is_some_and(|(_, note)| note.kind == NoteKind::Release);
        if clock >= stop && stop < duration && !release_on_boundary {
            finished = wait_until(origin + Duration::from_secs_f64(stop - start), cancel);
            break;
        }
        let target = origin + Duration::from_secs_f64(clock - start);
        // Long rests are waited out in slices, so the position keeps moving in Unity.
        while next_progress < target {
            if !wait_until(next_progress, cancel) {
                finished = false;
                break;
            }
            services.unity.send(&progress(next_progress));
            next_progress += PROGRESS_EVERY;
        }
        if !finished || !wait_until(target, cancel) {
            finished = false;
            break;
        }
        if let Some(message) = &step.message {
            services.piano.send(message);
            if let Some(note) = message.note() {
                services.unity.send_note(note);
            }
        }
    }
    if finished {
        services.unity.send(&Event::SongProgress {
            position: round_ms(stop),
            duration: round_ms(duration),
        });
    }

    services.piano.all_notes_off();
    services.unity.send(&Event::ClearScene);
    tracing::info!(file = %path.display(), "playback ended");
}

/// Keeps the datagrams short: Unity needs no more than millisecond precision.
fn round_ms(seconds: f64) -> f64 {
    (seconds * 1000.0).round() / 1000.0
}

#[cfg(test)]
mod tests {
    use std::net::UdpSocket;
    use std::sync::mpsc;
    use std::sync::{Arc, Mutex};

    use super::*;
    use crate::midi::ports::{MidiOut, MidiSink};
    use crate::midi::testing;
    use crate::runtime::services::{Clock, UnitySender};

    #[derive(Clone, Default)]
    struct Recorder(Arc<Mutex<Vec<Vec<u8>>>>);

    impl MidiSink for Recorder {
        fn send(&mut self, bytes: &[u8]) {
            self.0.lock().unwrap().push(bytes.to_vec());
        }
    }

    const CLEAR_SCENE: &str = r#"{"action": "clear_scene"}"#;

    struct Rig {
        services: Services,
        midi: Recorder,
        unity: UdpSocket,
        wakes: mpsc::Receiver<super::super::services::Wake>,
        dir: tempfile::TempDir,
    }

    fn rig() -> Rig {
        let unity = UdpSocket::bind("127.0.0.1:0").unwrap();
        unity
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let midi = Recorder::default();
        let (tx, wakes) = mpsc::channel();
        let services = Services {
            unity: UnitySender::new(unity.local_addr().unwrap()).unwrap(),
            piano: MidiOut::from_sink("test", midi.clone()),
            wakes: tx,
            clock: Clock::start(),
        };
        Rig {
            services,
            midi,
            unity,
            wakes,
            dir: tempfile::tempdir().unwrap(),
        }
    }

    impl Rig {
        fn song(&self, name: &str, notes: &[u8]) -> PathBuf {
            let path = self.dir.path().join(name);
            std::fs::write(&path, testing::melody(notes)).unwrap();
            path
        }

        fn next_datagram(&self) -> String {
            loop {
                let datagram = self.next_datagram_or_progress();
                if !datagram.contains("song_progress") {
                    return datagram;
                }
            }
        }

        fn next_datagram_or_progress(&self) -> String {
            let mut buffer = [0u8; 2048];
            let (len, _) = self.unity.recv_from(&mut buffer).unwrap();
            String::from_utf8(buffer[..len].to_vec()).unwrap()
        }
    }

    #[test]
    fn a_song_is_played_and_reported_between_two_clear_scenes() {
        let rig = rig();
        let path = rig.song("a.mid", &[60]);
        let mut slot = PlaybackSlot::default();
        slot.start(7, path, (0, None), &rig.services);

        assert_eq!(rig.next_datagram(), CLEAR_SCENE);
        assert!(rig.next_datagram().ends_with(r#""action": "press"}"#));
        assert!(rig.next_datagram().ends_with(r#""action": "release"}"#));
        assert_eq!(rig.next_datagram(), CLEAR_SCENE);

        let wake = rig.wakes.recv_timeout(Duration::from_secs(5)).unwrap();
        assert!(matches!(wake.input, Input::PlaybackFinished(7)));
        // Silence before and after, with the song in between.
        let sent = rig.midi.0.lock().unwrap();
        assert_eq!(sent.len(), 16 + 2 + 16);
        assert_eq!(sent[16], [0x90, 60, 100]);
    }

    #[test]
    fn a_song_can_start_part_way_through_and_reports_its_progress() {
        let rig = rig();
        // One beat (0.5 s) each: 60 from 0.0, 62 from 0.5, 64 from 1.0; 1.5 s in all.
        let path = rig.song("abc.mid", &[60, 62, 64]);
        let mut slot = PlaybackSlot::default();
        slot.start(4, path, (600, None), &rig.services);

        assert_eq!(rig.next_datagram_or_progress(), CLEAR_SCENE);
        assert_eq!(
            rig.next_datagram_or_progress(),
            r#"{"action": "song_progress", "position": 0.6, "duration": 1.5}"#
        );
        let mut rest = Vec::new();
        loop {
            let datagram = rig.next_datagram_or_progress();
            if datagram == CLEAR_SCENE {
                break;
            }
            rest.push(datagram);
        }
        let notes: Vec<_> = rest.iter().filter(|d| d.contains("\"note\"")).collect();
        assert_eq!(notes.len(), 2, "only 64 is played: {rest:?}");
        assert!(notes.iter().all(|d| d.contains("\"note\": 64")), "{rest:?}");
        assert_eq!(
            rest.last().unwrap(),
            r#"{"action": "song_progress", "position": 1.5, "duration": 1.5}"#
        );
        // 62 was sounding at 0.6 s but was skipped, so its note-off is not sent either.
        let sent = rig.midi.0.lock().unwrap();
        assert!(
            sent.iter()
                .all(|m| m.len() != 3 || m[1] != 62 || m[0] & 0xf0 == 0xb0)
        );
    }

    #[test]
    fn a_song_can_stop_early_after_waiting_out_the_stretch() {
        let rig = rig();
        // 60 from 0.0, 62 from 0.5, 64 from 1.0: stop at 1.0, so only 62 is played.
        let path = rig.song("abc.mid", &[60, 62, 64]);
        let mut slot = PlaybackSlot::default();
        let started = Instant::now();
        slot.start(5, path, (500, Some(1000)), &rig.services);

        let mut seen = Vec::new();
        assert_eq!(rig.next_datagram_or_progress(), CLEAR_SCENE);
        loop {
            let datagram = rig.next_datagram_or_progress();
            if datagram == CLEAR_SCENE {
                break;
            }
            seen.push(datagram);
        }
        assert!(started.elapsed() >= Duration::from_millis(450));
        let notes: Vec<_> = seen.iter().filter(|d| d.contains("\"note\"")).collect();
        assert_eq!(notes.len(), 2, "{seen:?}");
        assert!(notes.iter().all(|d| d.contains("\"note\": 62")), "{seen:?}");
        assert_eq!(
            seen.last().unwrap(),
            r#"{"action": "song_progress", "position": 1.0, "duration": 1.5}"#
        );
        assert!(matches!(
            rig.wakes
                .recv_timeout(Duration::from_secs(5))
                .unwrap()
                .input,
            Input::PlaybackFinished(5)
        ));
    }

    #[test]
    fn cancelling_stops_quickly_and_still_silences_the_piano() {
        let rig = rig();
        let path = rig.song("long.mid", &[60; 40]); // 20 s
        let mut slot = PlaybackSlot::default();
        slot.start(1, path, (0, None), &rig.services);
        assert_eq!(rig.next_datagram(), CLEAR_SCENE);

        let cancelled_at = Instant::now();
        slot.cancel();
        // Whatever was already on its way, the song ends with a second clear_scene.
        while rig.next_datagram() != CLEAR_SCENE {}
        assert!(cancelled_at.elapsed() < Duration::from_secs(1));
        assert!(matches!(
            rig.wakes
                .recv_timeout(Duration::from_secs(5))
                .unwrap()
                .input,
            Input::PlaybackFinished(1)
        ));
        let sent = rig.midi.0.lock().unwrap();
        assert!(
            sent[sent.len() - 16..]
                .iter()
                .all(|m| m[..] == [m[0], 123, 0] && m[0] & 0xf0 == 0xb0)
        );
    }

    #[test]
    fn starting_a_new_song_winds_the_old_one_down_first() {
        let rig = rig();
        let first = rig.song("a.mid", &[60; 40]);
        let second = rig.song("b.mid", &[72]);
        let mut slot = PlaybackSlot::default();
        slot.start(1, first, (0, None), &rig.services);
        assert_eq!(rig.next_datagram(), CLEAR_SCENE);
        slot.start(2, second, (0, None), &rig.services);

        // Read until the new song has finished (it ends with its own clear_scene).
        let mut seen = Vec::new();
        loop {
            seen.push(rig.next_datagram());
            if seen
                .iter()
                .any(|d| d.contains("\"note\": 72") && d.contains("release"))
            {
                break;
            }
        }
        let first_new_note = seen
            .iter()
            .position(|d| d.contains("\"note\": 72"))
            .unwrap();
        assert!(
            seen[first_new_note..]
                .iter()
                .all(|d| !d.contains("\"note\": 60")),
            "the old song kept playing: {seen:?}"
        );
        let finished: Vec<_> = (0..2)
            .map(|_| {
                match rig
                    .wakes
                    .recv_timeout(Duration::from_secs(5))
                    .unwrap()
                    .input
                {
                    Input::PlaybackFinished(id) => id,
                    other => panic!("unexpected {other:?}"),
                }
            })
            .collect();
        assert_eq!(finished, [1, 2]);
    }

    #[test]
    fn an_unreadable_song_is_reported_without_touching_the_piano() {
        let rig = rig();
        let path = rig.dir.path().join("bad.mid");
        std::fs::write(&path, b"nope").unwrap();
        let mut slot = PlaybackSlot::default();
        slot.start(3, path, (0, None), &rig.services);
        assert!(rig.next_datagram().contains("play_result"));
        assert!(matches!(
            rig.wakes
                .recv_timeout(Duration::from_secs(5))
                .unwrap()
                .input,
            Input::PlaybackFinished(3)
        ));
        assert!(rig.midi.0.lock().unwrap().is_empty());
    }
}
