//! Which hand plays each note of a song, for engraving it on the treble and bass staff.
//!
//! The notes are taken chord by chord (the notes starting together). Each hand has a position,
//! where it last played; every way of cutting the chord into a left part (the lower notes) and a
//! right part (the upper ones) is scored, and the cheapest wins:
//! - each note costs its distance from the hand that plays it;
//! - a hand reaches about an octave: a part spanning more costs a lot;
//! - a note on the other hand's side of middle C costs more, the further across it is, so the
//!   split falls at middle C unless the other hand is far away, as in printed piano music.
//!
//! So a melody that dips below middle C over a low bass stays in the right hand and a bass line
//! that climbs above it under a high melody stays in the left; a hand does not take over the
//! other's notes just because the other is resting. A resting hand drifts back home.

/// Middle C: the default split between the hands.
const MIDDLE_C: f64 = 60.0;
/// Where the hands start: right hand around G4, left hand around E3. Halfway between them is
/// just below middle C, so a lone note goes to the hand of its side of middle C.
const RIGHT_HOME: f64 = 67.0;
const LEFT_HOME: f64 = 52.0;
/// Widest part a hand plays without cost, in semitones (an octave).
const REACH: f64 = 12.0;
/// Cost of each semitone a part spans beyond `REACH`.
const STRETCH_COST: f64 = 6.0;
/// Cost of each semitone a note is from the hand that plays it.
const MOVE_COST: f64 = 0.5;
/// Cost of a note on the other hand's side of middle C, and of each semitone it is across.
const CROSS_COST: f64 = 3.0;
const SIDE_COST: f64 = 2.0;
/// Cost of each note beyond five in one hand.
const FINGER_COST: f64 = 20.0;
/// Notes starting this close together (a fraction of a beat) are one chord.
const CHORD_FRACTION: u64 = 24;
/// A hand silent for this many beats moves halfway back to its home position.
const REST_BEATS: u64 = 4;

/// For each note, in the given order, whether the right hand plays it. `notes` are
/// `(key, start tick)` sorted by start; `beat` is the ticks in a beat.
#[must_use]
pub fn right_hand(notes: &[(u8, u64)], beat: u64) -> Vec<bool> {
    let mut result = vec![false; notes.len()];
    let mut right = Hand::new(RIGHT_HOME);
    let mut left = Hand::new(LEFT_HOME);
    let window = (beat / CHORD_FRACTION).max(1);
    let rest = beat * REST_BEATS;

    let mut first = 0;
    while first < notes.len() {
        let start = notes[first].1;
        let mut end = first;
        while end < notes.len() && notes[end].1 <= start + window {
            end += 1;
        }
        let mut chord: Vec<usize> = (first..end).collect();
        chord.sort_by_key(|&i| notes[i].0);
        let keys: Vec<f64> = chord.iter().map(|&i| f64::from(notes[i].0)).collect();

        right.rest_until(start, rest);
        left.rest_until(start, rest);
        // Cut `k`: the lowest `k` notes to the left hand, the others to the right.
        let cut = (0..=keys.len())
            .min_by(|&a, &b| {
                let cost = |k: usize| left.cost(&keys[..k], false) + right.cost(&keys[k..], true);
                cost(a).total_cmp(&cost(b))
            })
            .unwrap_or(0);
        left.play(&keys[..cut], start);
        right.play(&keys[cut..], start);
        for &i in &chord[cut..] {
            result[i] = true;
        }
        first = end;
    }
    result
}

struct Hand {
    home: f64,
    position: f64,
    last: Option<u64>,
}

impl Hand {
    fn new(home: f64) -> Self {
        Self {
            home,
            position: home,
            last: None,
        }
    }

    /// The cost of this hand playing `keys` (sorted; none costs nothing).
    fn cost(&self, keys: &[f64], is_right: bool) -> f64 {
        let (Some(low), Some(high)) = (keys.first(), keys.last()) else {
            return 0.0;
        };
        let stretch = (high - low - REACH).max(0.0) * STRETCH_COST;
        let fingers = keys.len().saturating_sub(5) as f64 * FINGER_COST;
        let side: f64 = keys
            .iter()
            .map(|k| {
                if is_right {
                    MIDDLE_C - k
                } else {
                    k - MIDDLE_C + 1.0
                }
            })
            .filter(|wrong| *wrong > 0.0)
            .map(|wrong| CROSS_COST + wrong * SIDE_COST)
            .sum();
        let reach: f64 = keys
            .iter()
            .map(|k| (k - self.position).abs() * MOVE_COST)
            .sum();
        reach + stretch + fingers + side
    }

    fn play(&mut self, keys: &[f64], at: u64) {
        if let (Some(low), Some(high)) = (keys.first(), keys.last()) {
            self.position = (low + high) / 2.0;
            self.last = Some(at);
        }
    }

    /// Drifts back home for every `rest` ticks of silence before `now`.
    fn rest_until(&mut self, now: u64, rest: u64) {
        let Some(last) = self.last else {
            return;
        };
        let rests = (now.saturating_sub(last) / rest).min(8);
        for _ in 0..rests {
            self.position = f64::midpoint(self.position, self.home);
        }
        if rests > 0 {
            self.last = Some(now);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const BEAT: u64 = 480;

    /// Notes one after the other, a beat apart; each entry is a chord.
    fn chords(chords: &[&[u8]]) -> Vec<(u8, u64)> {
        chords
            .iter()
            .enumerate()
            .flat_map(|(i, keys)| keys.iter().map(move |&k| (k, i as u64 * BEAT)))
            .collect()
    }

    fn hands(chords_: &[&[u8]]) -> Vec<bool> {
        right_hand(&chords(chords_), BEAT)
    }

    #[test]
    fn with_nothing_else_to_go_by_the_hands_split_at_middle_c() {
        assert_eq!(hands(&[&[60]]), [true]);
        assert_eq!(hands(&[&[59]]), [false]);
        assert_eq!(hands(&[&[48, 55, 64, 67]]), [false, false, true, true]);
    }

    #[test]
    fn a_low_note_is_always_left_hand_and_a_high_one_right_hand() {
        assert_eq!(hands(&[&[72], &[41], &[84]]), [true, false, true]);
    }

    #[test]
    fn a_melody_dipping_below_middle_c_stays_in_the_right_hand() {
        // Right hand walks down E4 D4 C4 B3 A3 B3 C4 over a low left hand.
        let song: &[&[u8]] = &[
            &[36, 64],
            &[62],
            &[40, 60],
            &[59],
            &[36, 57],
            &[59],
            &[40, 60],
        ];
        let expected = [
            false, true, true, false, true, true, false, true, true, false, true,
        ];
        assert_eq!(hands(song), expected);
    }

    #[test]
    fn a_bass_line_climbing_above_middle_c_stays_in_the_left_hand() {
        // Left hand walks up A3 B3 C#4 D4 under a high right hand.
        let song: &[&[u8]] = &[&[57, 76, 81], &[59, 76, 81], &[61, 76, 81], &[62, 74, 78]];
        let expected = [
            false, true, true, false, true, true, false, true, true, false, true, true,
        ];
        assert_eq!(hands(song), expected);
    }

    #[test]
    fn a_resting_hand_keeps_its_side() {
        // The right hand plays B4 and rests while the left plays B3 A3 next to middle C; the
        // melody coming back on C4 D4 F4 is still the right hand's.
        let song: &[&[u8]] = &[&[71], &[], &[], &[], &[], &[58], &[57], &[60], &[62], &[65]];
        assert_eq!(hands(song), [true, false, false, true, true, true]);
    }

    #[test]
    fn a_chord_wider_than_a_hand_is_shared() {
        // C3 E3 G3 C4 E4 G4: too wide for one hand.
        let parts = hands(&[&[48, 52, 55, 60, 64, 67]]);
        assert!(parts[0] != parts[5], "{parts:?}");
    }
}
