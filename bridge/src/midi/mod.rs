//! MIDI handling: messages, song timelines, song analysis and port selection.

pub mod analysis;
pub mod message;
pub mod ports;
#[cfg(test)]
pub mod testing;
pub mod timeline;

pub use message::{Message, NoteKind, NoteMessage};
pub use timeline::{TimedMessage, Timeline};
