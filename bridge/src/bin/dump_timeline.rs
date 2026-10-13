//! Dev tool for the differential tests: prints how a MIDI file is read, as JSON.
//!
//! `dump_timeline FILE...` prints one JSON object per file, keyed by path.

use std::collections::BTreeMap;
use std::process::ExitCode;

use midi_bridge::midi::{Timeline, analysis};
use serde_json::{Value, json};

fn describe(path: &str) -> Value {
    let timeline = match Timeline::load(path.as_ref()) {
        Ok(timeline) => timeline,
        Err(error) => return json!({ "error": error.to_string() }),
    };
    let notes = analysis::reference_notes(&timeline);
    json!({
        "length": timeline.length(),
        "steps": timeline.steps.iter().map(|s| json!({
            "delay": s.delay,
            "bytes": s.message.as_ref().map(|m| m.as_bytes().to_vec()),
        })).collect::<Vec<_>>(),
        "reference_notes": notes.iter().map(|n| json!({
            "note": n.note, "vel": n.vel, "start": n.start, "dur": n.dur,
        })).collect::<Vec<_>>(),
        "chords": analysis::chords(&notes),
        "has_piano_part": analysis::has_piano_part(&timeline),
        "unsafe": analysis::check_visualizer_safety(&timeline).err().map(|reason| reason.to_string()),
    })
}

fn main() -> ExitCode {
    let report: BTreeMap<String, Value> = std::env::args()
        .skip(1)
        .map(|path| (path.clone(), describe(&path)))
        .collect();
    println!(
        "{}",
        serde_json::to_string(&report).expect("plain JSON values always serialize")
    );
    ExitCode::SUCCESS
}
