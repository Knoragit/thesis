//! MIDI handling: messages, song timelines, song analysis and port selection.

pub mod analysis;
pub mod message;
pub mod ports;
pub mod timeline;

pub use message::{Message, NoteKind, NoteMessage};
pub use timeline::{TimedMessage, Timeline};
