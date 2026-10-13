//! Tutorial demonstrations: plays a script on the piano and streams it to Unity.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;

use super::services::Services;
use crate::core::examples::{Step, script};
use crate::midi::Message;
use crate::protocol::{Challenge, Event};

/// The demonstration currently running, if any.
#[derive(Debug, Default)]
pub struct ExampleSlot {
    cancel: Option<Arc<AtomicBool>>,
}

impl ExampleSlot {
    /// Starts a demonstration in the background. It ends with `clear_scene` and `example_done`.
    pub fn start(&mut self, challenge: Challenge, services: &Services) {
        let cancel = Arc::new(AtomicBool::new(false));
        self.cancel = Some(Arc::clone(&cancel));
        let services = services.clone();
        thread::spawn(move || {
            tracing::info!(?challenge, "playing demonstration");
            run(&script(challenge), &cancel, &services);
            services.unity.send(&Event::ClearScene);
            services.unity.send(&Event::ExampleDone);
        });
    }

    /// Asks the running demonstration to stop at its next checkpoint.
    pub fn stop(&self) {
        if let Some(cancel) = &self.cancel {
            cancel.store(true, Ordering::Relaxed);
        }
    }
}

fn run(steps: &[Step], cancel: &AtomicBool, services: &Services) {
    for step in steps {
        match step {
            Step::Checkpoint => {
                if cancel.load(Ordering::Relaxed) {
                    return;
                }
            }
            Step::Press {
                note,
                unity_velocity,
                midi_velocity,
            } => {
                services
                    .piano
                    .send(&Message::note_on(0, *note, *midi_velocity));
                services.unity.send(&Event::Press {
                    note: *note,
                    velocity: *unity_velocity,
                });
            }
            Step::Release {
                note,
                unity_velocity,
            } => {
                services.piano.send(&Message::note_off(0, *note));
                services.unity.send(&Event::Release {
                    note: *note,
                    velocity: *unity_velocity,
                });
            }
            Step::Wait(duration) => thread::sleep(*duration),
        }
    }
}
