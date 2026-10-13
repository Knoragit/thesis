//! Handles shared by the event loop and the worker threads.

use std::net::{SocketAddr, UdpSocket};
use std::sync::Arc;
use std::sync::mpsc::Sender;
use std::time::Instant;

use crate::core::Input;
use crate::midi::NoteKind;
use crate::midi::NoteMessage;
use crate::midi::ports::MidiOut;
use crate::protocol::Event;

/// Seconds on a monotonic clock, counted from when the bridge started.
#[derive(Debug, Clone, Copy)]
pub struct Clock(Instant);

impl Clock {
    #[must_use]
    pub fn start() -> Self {
        Self(Instant::now())
    }

    #[must_use]
    pub fn now(self) -> f64 {
        self.0.elapsed().as_secs_f64()
    }
}

/// Something that happened, stamped with when.
#[derive(Debug)]
pub struct Wake {
    pub input: Input,
    pub at: f64,
}

/// Sends events to Unity.
#[derive(Debug, Clone)]
pub struct UnitySender {
    socket: Arc<UdpSocket>,
    target: SocketAddr,
}

impl UnitySender {
    pub fn new(target: SocketAddr) -> std::io::Result<Self> {
        let local = if target.is_ipv4() {
            "0.0.0.0:0"
        } else {
            "[::]:0"
        };
        Ok(Self {
            socket: Arc::new(UdpSocket::bind(local)?),
            target,
        })
    }

    pub fn send(&self, event: &Event) {
        let payload = event.encode();
        tracing::trace!(event = %String::from_utf8_lossy(&payload), "to Unity");
        if let Err(error) = self.socket.send_to(&payload, self.target) {
            tracing::warn!(%error, "could not send to Unity");
        }
    }

    /// Tells Unity about a key press or release.
    pub fn send_note(&self, note: NoteMessage) {
        let velocity = note.unit_velocity();
        self.send(&match note.kind {
            NoteKind::Press => Event::Press {
                note: note.note,
                velocity,
            },
            NoteKind::Release => Event::Release {
                note: note.note,
                velocity,
            },
        });
    }
}

/// Everything a worker thread needs to act on the world and report back.
#[derive(Debug, Clone)]
pub struct Services {
    pub unity: UnitySender,
    pub piano: MidiOut,
    pub wakes: Sender<Wake>,
    pub clock: Clock,
}

impl Services {
    /// Reports a result to the event loop.
    pub fn wake(&self, input: Input) {
        let _ = self.wakes.send(Wake {
            input,
            at: self.clock.now(),
        });
    }
}
