//! Choosing and opening the MIDI input (keyboard) and output (piano) ports.

use std::sync::{Arc, Mutex};

use midir::{MidiInput, MidiInputConnection, MidiOutput, MidiOutputConnection};
use thiserror::Error;

use super::Message;

const CLIENT_NAME: &str = "midi-bridge";

/// Name fragments of ports that are loopbacks or software devices rather than a piano.
const IGNORED: [&str; 4] = ["loopmidi", "virtual", "through", "iac"];
/// Windows' built-in synthesizer is not the user's piano either (outputs only).
const IGNORED_OUTPUT_ONLY: &str = "microsoft gs";

#[derive(Debug, Error)]
pub enum PortError {
    #[error("no MIDI {0} ports available")]
    None(&'static str),
    #[error("no MIDI {direction} port matches {wanted:?} (available: {available:?})")]
    NoMatch {
        direction: &'static str,
        wanted: String,
        available: Vec<String>,
    },
    #[error("MIDI error: {0}")]
    Backend(String),
}

/// Picks a port by name.
///
/// With `preferred`, the first port containing that text (ignoring case). Otherwise the first
/// port that does not look like a loopback or software device, or the first port at all if
/// every one does.
#[must_use]
pub fn select_port(names: &[String], is_output: bool, preferred: Option<&str>) -> Option<usize> {
    if let Some(wanted) = preferred {
        let wanted = wanted.to_lowercase();
        return names
            .iter()
            .position(|name| name.to_lowercase().contains(&wanted));
    }
    let ignored = |name: &str| {
        let lower = name.to_lowercase();
        IGNORED.iter().any(|key| lower.contains(key))
            || (is_output && lower.contains(IGNORED_OUTPUT_ONLY))
    };
    names
        .iter()
        .position(|name| !ignored(name))
        .or(if names.is_empty() { None } else { Some(0) })
}

/// An open keyboard connection; messages arrive on a thread owned by the MIDI backend.
pub struct InputPort {
    pub name: String,
    _connection: MidiInputConnection<()>,
}

impl std::fmt::Debug for InputPort {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InputPort")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

pub fn open_input(
    preferred: Option<&str>,
    mut on_message: impl FnMut(&[u8]) + Send + 'static,
) -> Result<InputPort, PortError> {
    let input = MidiInput::new(CLIENT_NAME).map_err(|e| PortError::Backend(e.to_string()))?;
    let ports = input.ports();
    let names: Vec<String> = ports
        .iter()
        .filter_map(|p| input.port_name(p).ok())
        .collect();
    let index = pick("input", &names, false, preferred)?;
    let connection = input
        .connect(
            &ports[index],
            "keyboard",
            move |_, bytes, ()| on_message(bytes),
            (),
        )
        .map_err(|e| PortError::Backend(e.to_string()))?;
    Ok(InputPort {
        name: names[index].clone(),
        _connection: connection,
    })
}

fn pick(
    direction: &'static str,
    names: &[String],
    is_output: bool,
    preferred: Option<&str>,
) -> Result<usize, PortError> {
    select_port(names, is_output, preferred).ok_or_else(|| match preferred {
        Some(wanted) if !names.is_empty() => PortError::NoMatch {
            direction,
            wanted: wanted.to_owned(),
            available: names.to_vec(),
        },
        _ => PortError::None(direction),
    })
}

/// Anything that accepts raw MIDI bytes.
pub trait MidiSink: Send {
    fn send(&mut self, bytes: &[u8]);
}

impl MidiSink for MidiOutputConnection {
    fn send(&mut self, bytes: &[u8]) {
        if let Err(error) = MidiOutputConnection::send(self, bytes) {
            tracing::warn!(%error, "MIDI send failed");
        }
    }
}

/// The piano: a cheaply cloneable handle shared by playback and demo threads.
/// Without an output port, sending does nothing.
#[derive(Clone)]
pub struct MidiOut {
    sink: Option<Arc<Mutex<Box<dyn MidiSink>>>>,
    pub name: Option<String>,
}

impl std::fmt::Debug for MidiOut {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MidiOut")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

impl MidiOut {
    #[must_use]
    pub fn disconnected() -> Self {
        Self {
            sink: None,
            name: None,
        }
    }

    #[must_use]
    pub fn from_sink(name: &str, sink: impl MidiSink + 'static) -> Self {
        Self {
            sink: Some(Arc::new(Mutex::new(Box::new(sink)))),
            name: Some(name.to_owned()),
        }
    }

    pub fn open(preferred: Option<&str>) -> Result<Self, PortError> {
        let output = MidiOutput::new(CLIENT_NAME).map_err(|e| PortError::Backend(e.to_string()))?;
        let ports = output.ports();
        let names: Vec<String> = ports
            .iter()
            .filter_map(|p| output.port_name(p).ok())
            .collect();
        let index = pick("output", &names, true, preferred)?;
        let connection = output
            .connect(&ports[index], "piano")
            .map_err(|e| PortError::Backend(e.to_string()))?;
        Ok(Self::from_sink(&names[index], connection))
    }

    pub fn send(&self, message: &Message) {
        if let Some(sink) = &self.sink {
            sink.lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .send(message.as_bytes());
        }
    }

    /// Silences every channel.
    pub fn all_notes_off(&self) {
        for channel in 0..16 {
            self.send(&Message::all_notes_off(channel));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| (*s).to_owned()).collect()
    }

    #[test]
    fn prefers_physical_ports_over_software_ones() {
        let ports = names(&[
            "Driver IAC Bus 1",
            "loopMIDI Port",
            "Midi Through",
            "Roland FP-30",
        ]);
        assert_eq!(select_port(&ports, false, None), Some(3));
    }

    #[test]
    fn virtual_keyboards_win_over_the_mac_loopback() {
        // The original picked "Driver IAC Bus 1" here and never reached the keyboard.
        let ports = names(&["Driver IAC Bus 1", "VMPK Output"]);
        assert_eq!(select_port(&ports, false, None), Some(1));
    }

    #[test]
    fn falls_back_to_the_first_port_when_all_look_like_software() {
        let ports = names(&["Virtual Keyboard", "loopMIDI"]);
        assert_eq!(select_port(&ports, false, None), Some(0));
        assert_eq!(select_port(&[], false, None), None);
    }

    #[test]
    fn the_windows_synth_is_skipped_for_output_only() {
        let ports = names(&["Microsoft GS Wavetable Synth", "Roland Piano"]);
        assert_eq!(select_port(&ports, true, None), Some(1));
        assert_eq!(select_port(&ports, false, None), Some(0));
    }

    #[test]
    fn an_explicit_choice_matches_by_substring_ignoring_case() {
        let ports = names(&["Driver IAC Bus 1", "Bridge Test Keyboard"]);
        assert_eq!(select_port(&ports, false, Some("test keyboard")), Some(1));
        assert_eq!(select_port(&ports, false, Some("iac")), Some(0));
        assert_eq!(select_port(&ports, false, Some("nothing")), None);
    }

    #[derive(Default, Clone)]
    struct Recorder(Arc<Mutex<Vec<Vec<u8>>>>);

    impl MidiSink for Recorder {
        fn send(&mut self, bytes: &[u8]) {
            self.0.lock().unwrap().push(bytes.to_vec());
        }
    }

    #[test]
    fn all_notes_off_covers_the_sixteen_channels() {
        let recorder = Recorder::default();
        MidiOut::from_sink("test", recorder.clone()).all_notes_off();
        let sent = recorder.0.lock().unwrap();
        assert_eq!(sent.len(), 16);
        assert_eq!(sent[0], [0xb0, 123, 0]);
        assert_eq!(sent[15], [0xbf, 123, 0]);
    }

    #[test]
    fn a_disconnected_output_ignores_everything() {
        MidiOut::disconnected().all_notes_off();
    }
}
