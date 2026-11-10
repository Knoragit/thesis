//! An optional log of everything the piano sounds: the keys pressed on the keyboard and the
//! songs and demonstrations the bridge plays on it, each message with the seconds since the
//! bridge started. It is for editing a demo video: the log becomes an audio track that lines up
//! with what happened (`bridge_tools.demo_audio`).
//!
//! Each run of the bridge writes its own `Note_YYYYMMDD_HHMMSS.txt` in the log folder. One line
//! per message, `<seconds> <in|out> <bytes in hex>`, written straight away, so the log is
//! complete even when Unity kills the bridge.

use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use super::services::Clock;

#[derive(Debug)]
pub struct MidiLog {
    file: Mutex<File>,
    clock: Clock,
    pub path: PathBuf,
}

impl MidiLog {
    /// Starts a new log in `folder`.
    pub fn create(folder: &Path, clock: Clock) -> std::io::Result<Arc<Self>> {
        std::fs::create_dir_all(folder)?;
        let path = folder.join(format!(
            "Note_{}.txt",
            chrono::Local::now().format("%Y%m%d_%H%M%S")
        ));
        Ok(Arc::new(Self {
            file: Mutex::new(File::create(&path)?),
            clock,
            path,
        }))
    }

    /// Logs a channel message; system messages (clock, active sensing, sysex) are left out.
    pub fn record(&self, direction: &str, bytes: &[u8]) {
        if !bytes
            .first()
            .is_some_and(|status| (0x80..0xf0).contains(status))
        {
            return;
        }
        let hex: Vec<String> = bytes.iter().map(|b| format!("{b:02x}")).collect();
        let line = format!("{:.6} {direction} {}\n", self.clock.now(), hex.join(" "));
        let mut file = self
            .file
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if let Err(error) = file.write_all(line.as_bytes()) {
            tracing::warn!(%error, "cannot write the MIDI log");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn channel_messages_are_logged_one_per_line() {
        let dir = std::env::temp_dir().join(format!("midi-log-{}", std::process::id()));
        let log = MidiLog::create(&dir, Clock::start()).unwrap();
        let path = log.path.clone();
        log.record("in", &[0x90, 60, 100]);
        log.record("out", &[0xf8]);
        log.record("out", &[0xb0, 64, 127]);
        let text = std::fs::read_to_string(&path).unwrap();
        let lines: Vec<Vec<&str>> = text
            .lines()
            .map(|l| l.split(' ').skip(1).collect())
            .collect();
        assert_eq!(
            lines,
            [vec!["in", "90", "3c", "64"], vec!["out", "b0", "40", "7f"]]
        );
        std::fs::remove_dir_all(dir).unwrap();
    }
}
