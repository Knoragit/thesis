//! A single MIDI message as raw bytes, with just enough interpretation for this bridge.

/// A complete MIDI message, ready to be sent to a port.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message(Vec<u8>);

/// What Unity cares about: a key going down or up.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoteKind {
    Press,
    Release,
}

/// A note event. `NoteOn` with velocity 0 counts as a release, like in the original bridge.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NoteMessage {
    pub kind: NoteKind,
    pub note: u8,
    pub velocity: u8,
}

impl NoteMessage {
    /// The velocity as Unity receives it: 0.0 to 1.0.
    #[must_use]
    pub fn unit_velocity(self) -> f64 {
        f64::from(self.velocity) / 127.0
    }
}

impl Message {
    #[must_use]
    pub fn from_bytes(bytes: Vec<u8>) -> Self {
        Self(bytes)
    }

    #[must_use]
    pub fn note_on(channel: u8, note: u8, velocity: u8) -> Self {
        Self(vec![0x90 | (channel & 0x0f), note, velocity])
    }

    /// A note-off with the default release velocity 64, as the tutorial demos send it.
    #[must_use]
    pub fn note_off(channel: u8, note: u8) -> Self {
        Self(vec![0x80 | (channel & 0x0f), note, 64])
    }

    /// Control change 123 (all notes off) with value 0.
    #[must_use]
    pub fn all_notes_off(channel: u8) -> Self {
        Self(vec![0xb0 | (channel & 0x0f), 123, 0])
    }

    #[must_use]
    pub fn as_bytes(&self) -> &[u8] {
        &self.0
    }

    /// The note this message carries, if it is a note-on or note-off.
    #[must_use]
    pub fn note(&self) -> Option<NoteMessage> {
        let [status, note, velocity] = *self.0.as_slice() else {
            return None;
        };
        let kind = match status & 0xf0 {
            0x90 if velocity > 0 => NoteKind::Press,
            0x90 | 0x80 => NoteKind::Release,
            _ => return None,
        };
        Some(NoteMessage {
            kind,
            note,
            velocity,
        })
    }

    /// The program number if this is a program change on any channel.
    #[must_use]
    pub fn program_change(&self) -> Option<u8> {
        match self.0.as_slice() {
            [status, program] if status & 0xf0 == 0xc0 => Some(*program),
            _ => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn note_messages_are_classified_like_unity_expects() {
        let press = Message::from_bytes(vec![0x93, 60, 100]).note().unwrap();
        assert_eq!(
            (press.kind, press.note, press.velocity),
            (NoteKind::Press, 60, 100)
        );

        let off = Message::from_bytes(vec![0x80, 60, 40]).note().unwrap();
        assert_eq!((off.kind, off.velocity), (NoteKind::Release, 40));

        let zero = Message::from_bytes(vec![0x90, 60, 0]).note().unwrap();
        assert_eq!((zero.kind, zero.velocity), (NoteKind::Release, 0));
    }

    #[test]
    fn other_messages_are_not_notes() {
        for bytes in [
            vec![0xb0, 64, 127],
            vec![0xe0, 0, 64],
            vec![0xc0, 5],
            vec![0xf0, 1, 0xf7],
        ] {
            assert_eq!(Message::from_bytes(bytes).note(), None);
        }
    }

    #[test]
    fn unit_velocity_scales_to_the_midi_range() {
        let full = Message::note_on(0, 60, 127).note().unwrap();
        assert!((full.unit_velocity() - 1.0).abs() < f64::EPSILON);
    }

    #[test]
    fn program_changes_on_any_channel() {
        assert_eq!(
            Message::from_bytes(vec![0xc3, 40]).program_change(),
            Some(40)
        );
        assert_eq!(
            Message::from_bytes(vec![0x90, 40, 1]).program_change(),
            None
        );
    }

    #[test]
    fn constructors_build_the_expected_bytes() {
        assert_eq!(Message::all_notes_off(15).as_bytes(), [0xbf, 123, 0]);
        assert_eq!(Message::note_off(0, 60).as_bytes(), [0x80, 60, 64]);
        assert_eq!(Message::note_on(2, 72, 64).as_bytes(), [0x92, 72, 64]);
    }
}
