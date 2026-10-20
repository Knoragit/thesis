//! The tutorial demonstrations, as data.
//!
//! Each challenge shows the user how a gesture should sound, always starting from middle C.
//! The runtime plays a script on the piano and streams it to Unity.
//!
//! A cancel is only noticed at a [`Step::Checkpoint`], and checkpoints are only placed where no
//! key is held, so a cancelled demo never leaves a note sounding.

use std::time::Duration;

use crate::protocol::Challenge;

/// Middle C and the notes the demos use.
const C4: u8 = 60;
const D4: u8 = 62;
const E4: u8 = 64;
const F4: u8 = 65;
const G4: u8 = 67;
const A4: u8 = 69;

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

fn press(note: u8, velocity: f64) -> Step {
    Step::Press {
        note,
        unity_velocity: velocity,
        midi_velocity: (velocity * 127.0) as u8,
    }
}

fn release(note: u8, velocity: f64) -> Step {
    Step::Release {
        note,
        unity_velocity: velocity,
    }
}

/// A line of notes where each one starts `overlap` ms before the previous one is released.
fn legato_line(notes: &[u8], velocity: f64, hold: u64, overlap: u64) -> Vec<Step> {
    let mut steps = vec![Step::Checkpoint];
    for (i, &current) in notes.iter().enumerate() {
        steps.push(press(current, velocity));
        if i > 0 {
            steps.push(Step::Wait(ms(overlap)));
            steps.push(release(notes[i - 1], velocity));
        }
        steps.push(Step::Wait(ms(hold - overlap)));
    }
    steps.push(Step::Wait(ms(overlap)));
    steps.push(release(notes[notes.len() - 1], velocity));
    steps.push(Step::Wait(ms(100)));
    steps
}

/// Two notes struck together, the top one louder, held for `hold`.
fn dyad(low: u8, high: u8, low_velocity: f64, high_velocity: f64, hold: u64) -> Vec<Step> {
    vec![
        Step::Checkpoint,
        press(low, low_velocity),
        press(high, high_velocity),
        Step::Wait(ms(hold)),
        release(low, low_velocity),
        release(high, high_velocity),
        Step::Wait(ms(350)),
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
        // Staccato: two short notes separated by a wide gap.
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
        // Dynamic echo: C D E F loud, a breath, then the same four soft.
        Challenge::DynamicEcho => {
            let line = |velocity, pause_after_last| {
                [
                    note(C4, velocity, 280, 120),
                    note(D4, velocity, 280, 120),
                    note(E4, velocity, 280, 120),
                    note(F4, velocity, 280, pause_after_last),
                ]
                .concat()
            };
            [line(0.8, 600), line(0.2, 100)].concat()
        }
        // Legato then staccato: C and D joined, then E and F short and apart.
        Challenge::LegatoThenStaccato => [
            vec![
                Step::Checkpoint,
                press(C4, 0.5),
                Step::Wait(ms(450)),
                press(D4, 0.5),
                Step::Wait(ms(80)),
                release(C4, 0.5),
                Step::Wait(ms(420)),
                release(D4, 0.5),
                Step::Wait(ms(400)),
            ],
            note(E4, 0.5, 150, 350).to_vec(),
            note(F4, 0.5, 150, 100).to_vec(),
        ]
        .concat(),
        // First-beat accent: two bars of C D E F, the C of each bar clearly louder.
        Challenge::FirstBeatAccent => {
            let bar = [
                note(C4, 0.8, 250, 150),
                note(D4, 0.35, 250, 150),
                note(E4, 0.35, 250, 150),
                note(F4, 0.35, 250, 150),
            ]
            .concat();
            [bar.clone(), bar].concat()
        }
        // Ritardando: C D E F G, the gaps growing towards the end like a sigh.
        Challenge::Ritardando => [
            note(C4, 0.5, 300, 100),
            note(D4, 0.5, 300, 100),
            note(E4, 0.5, 350, 150),
            note(F4, 0.5, 450, 250),
            note(G4, 0.5, 900, 100),
        ]
        .concat(),
        // Top voice: C+E, D+F, E+G with the upper note brighter.
        Challenge::TopVoice => [
            dyad(C4, E4, 0.3, 0.7, 700),
            dyad(D4, F4, 0.3, 0.7, 700),
            dyad(E4, G4, 0.3, 0.7, 700),
        ]
        .concat(),
        // Cantabile: C D E F G A, every note joined to the next with a short overlap.
        Challenge::Cantabile => legato_line(&[C4, D4, E4, F4, G4, A4], 0.45, 450, 60),
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
    fn every_demo_ends_before_unity_retries() {
        // Unity sends play_example again if example_done has not arrived after 7 s.
        for challenge in Challenge::ALL {
            assert!(total(&script(challenge)) < ms(6000), "{challenge:?}");
        }
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
    fn every_pressed_note_is_released_and_no_checkpoint_leaves_one_held() {
        for challenge in Challenge::ALL {
            let mut held = Vec::new();
            for step in script(challenge) {
                match step {
                    Step::Press { note, .. } => held.push(note),
                    Step::Release { note, .. } => held.retain(|n| *n != note),
                    Step::Checkpoint => assert!(held.is_empty(), "{challenge:?}"),
                    Step::Wait(_) => {}
                }
            }
            assert!(held.is_empty(), "{challenge:?}");
        }
    }

    #[test]
    fn the_new_demos_show_what_they_teach() {
        let presses = |challenge| -> Vec<(u8, f64)> {
            script(challenge)
                .into_iter()
                .filter_map(|s| match s {
                    Step::Press {
                        note,
                        unity_velocity,
                        ..
                    } => Some((note, unity_velocity)),
                    _ => None,
                })
                .collect()
        };
        // The echo repeats the same notes, loud then soft.
        let echo = presses(Challenge::DynamicEcho);
        assert_eq!(echo.len(), 8);
        for i in 0..4 {
            assert_eq!(echo[i].0, echo[i + 4].0);
            assert!(echo[i].1 > echo[i + 4].1);
        }
        // The accent falls on the first note of every group of four.
        for group in presses(Challenge::FirstBeatAccent).chunks(4) {
            assert!(group[1..].iter().all(|(_, v)| *v < group[0].1));
        }
        // The upper note of each chord is the louder one.
        for chord in presses(Challenge::TopVoice).chunks(2) {
            assert!(chord[1].0 > chord[0].0 && chord[1].1 > chord[0].1);
        }
    }
}
