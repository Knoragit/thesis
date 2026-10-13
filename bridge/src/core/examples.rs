//! The tutorial demonstrations, as data.
//!
//! Each challenge shows the user how a gesture should sound, always starting from middle C.
//! The runtime plays a script on the piano and streams it to Unity.

use std::time::Duration;

use crate::protocol::Challenge;

/// Middle C and the notes the demos use.
const C4: u8 = 60;
const D4: u8 = 62;
const E4: u8 = 64;
const F4: u8 = 65;

#[derive(Debug, Clone, PartialEq)]
pub enum Step {
    /// Stop here if the demo was cancelled.
    Checkpoint,
    Press {
        note: u8,
        unity_velocity: f64,
        midi_velocity: u8,
    },
    /// The piano always gets the default release velocity; Unity gets the press velocity back.
    Release {
        note: u8,
        unity_velocity: f64,
    },
    Wait(Duration),
}

fn ms(millis: u64) -> Duration {
    Duration::from_millis(millis)
}

/// One note held for `hold`, then `pause` of silence, with a cancel checkpoint before it.
fn note(note: u8, velocity: f64, hold: u64, pause: u64) -> [Step; 5] {
    [
        Step::Checkpoint,
        Step::Press {
            note,
            unity_velocity: velocity,
            midi_velocity: (velocity * 127.0) as u8,
        },
        Step::Wait(ms(hold)),
        Step::Release {
            note,
            unity_velocity: velocity,
        },
        Step::Wait(ms(pause)),
    ]
}

/// The steps of a demonstration.
#[must_use]
pub fn script(challenge: Challenge) -> Vec<Step> {
    match challenge {
        // A soft touch: one long, quiet C.
        Challenge::SoftTouch => note(C4, 0.18, 1200, 100).to_vec(),
        // A crescendo: C D E F, each a little louder.
        Challenge::Crescendo => [
            note(C4, 0.25, 350, 400),
            note(D4, 0.35, 350, 400),
            note(E4, 0.45, 350, 400),
            note(F4, 0.55, 350, 100),
        ]
        .concat(),
        // Staccato: two short notes with a wide gap, so they clearly do not overlap.
        Challenge::Staccato => [note(C4, 0.5, 180, 400), note(D4, 0.5, 150, 100)].concat(),
        // Legato: the E starts while the C is still held, with two distinct attacks.
        // This one cannot be interrupted.
        Challenge::Legato => {
            let press = |note| Step::Press {
                note,
                unity_velocity: 0.5,
                midi_velocity: 64,
            };
            let release = |note| Step::Release {
                note,
                unity_velocity: 0.5,
            };
            vec![
                press(C4),
                Step::Wait(ms(600)),
                press(E4),
                Step::Wait(ms(850)),
                release(E4),
                Step::Wait(ms(100)),
                release(C4),
                Step::Wait(ms(100)),
            ]
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn total(steps: &[Step]) -> Duration {
        steps
            .iter()
            .map(|s| {
                if let Step::Wait(d) = s {
                    *d
                } else {
                    Duration::ZERO
                }
            })
            .sum()
    }

    #[test]
    fn durations_match_the_design() {
        assert_eq!(total(&script(Challenge::SoftTouch)), ms(1300));
        assert_eq!(total(&script(Challenge::Crescendo)), ms(3 * 750 + 450));
        assert_eq!(total(&script(Challenge::Staccato)), ms(830));
        assert_eq!(total(&script(Challenge::Legato)), ms(1650));
    }

    #[test]
    fn midi_velocities_truncate_like_the_original() {
        let velocities: Vec<u8> = script(Challenge::Crescendo)
            .iter()
            .filter_map(|s| {
                if let Step::Press { midi_velocity, .. } = s {
                    Some(*midi_velocity)
                } else {
                    None
                }
            })
            .collect();
        assert_eq!(velocities, [31, 44, 57, 69]);
    }

    #[test]
    fn only_the_legato_demo_cannot_be_cancelled() {
        for challenge in [
            Challenge::SoftTouch,
            Challenge::Crescendo,
            Challenge::Staccato,
        ] {
            assert!(script(challenge).contains(&Step::Checkpoint));
        }
        assert!(!script(Challenge::Legato).contains(&Step::Checkpoint));
    }

    #[test]
    fn every_pressed_note_is_released() {
        for challenge in [
            Challenge::SoftTouch,
            Challenge::Crescendo,
            Challenge::Staccato,
            Challenge::Legato,
        ] {
            let mut held = Vec::new();
            for step in script(challenge) {
                match step {
                    Step::Press { note, .. } => held.push(note),
                    Step::Release { note, .. } => held.retain(|n| *n != note),
                    _ => {}
                }
            }
            assert!(held.is_empty(), "{challenge:?}");
        }
    }
}
