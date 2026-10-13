//! Command-line configuration. Every option can also be set through a `MIDI_BRIDGE_*` variable.

use std::net::SocketAddr;
use std::path::PathBuf;

use clap::Parser;

#[derive(Debug, Clone, Parser)]
#[command(version, about = "Bridges a Unity app and a MIDI keyboard over UDP")]
pub struct Config {
    /// Folder holding the song library, the database and the session reports.
    #[arg(long, env = "MIDI_BRIDGE_DATA_DIR", default_value_os_t = default_data_dir())]
    pub data_dir: PathBuf,

    /// Name of the `SQLite` database file inside the data folder.
    #[arg(long, env = "MIDI_BRIDGE_DB_FILE", default_value = "library.db")]
    pub db_file: String,

    /// Name of the PDF inside each session folder.
    #[arg(long, env = "MIDI_BRIDGE_REPORT_NAME", default_value = "report.pdf")]
    pub report_name: String,

    /// Subfolder of the data folder with the `.mid` songs.
    #[arg(long, env = "MIDI_BRIDGE_SONGS_SUBDIR", default_value = "Canzoni")]
    pub songs_subdir: String,

    /// Subfolder of the data folder where each report session gets its own folder.
    #[arg(long, env = "MIDI_BRIDGE_SESSIONS_SUBDIR", default_value = "Sessioni")]
    pub sessions_subdir: String,

    /// Use the MIDI input port whose name contains this text (default: first physical port).
    #[arg(long, env = "MIDI_BRIDGE_MIDI_IN")]
    pub midi_in: Option<String>,

    /// Use the MIDI output port whose name contains this text (default: first physical port).
    #[arg(long, env = "MIDI_BRIDGE_MIDI_OUT")]
    pub midi_out: Option<String>,

    /// Where commands from Unity arrive.
    #[arg(long, env = "MIDI_BRIDGE_UDP_LISTEN", default_value = "127.0.0.1:5006")]
    pub udp_listen: SocketAddr,

    /// Where events for Unity are sent.
    #[arg(long, env = "MIDI_BRIDGE_UDP_UNITY", default_value = "127.0.0.1:5005")]
    pub udp_unity: SocketAddr,

    /// The Python project with the report renderer and the song search.
    /// Default: `python/` next to the executable's ancestors or the working directory.
    #[arg(long, env = "MIDI_BRIDGE_PYTHON_PROJECT")]
    pub python_project: Option<PathBuf>,
}

fn default_data_dir() -> PathBuf {
    dirs::home_dir()
        .unwrap_or_default()
        .join("Desktop")
        .join("midi-bridge")
}

impl Config {
    #[must_use]
    pub fn songs_dir(&self) -> PathBuf {
        self.data_dir.join(&self.songs_subdir)
    }

    #[must_use]
    pub fn sessions_dir(&self) -> PathBuf {
        self.data_dir.join(&self.sessions_subdir)
    }

    #[must_use]
    pub fn db_path(&self) -> PathBuf {
        self.data_dir.join(&self.db_file)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_match_the_unity_ports() {
        let config = Config::parse_from(["midi-bridge"]);
        assert_eq!(config.udp_listen.to_string(), "127.0.0.1:5006");
        assert_eq!(config.udp_unity.to_string(), "127.0.0.1:5005");
        assert!(config.data_dir.ends_with("Desktop/midi-bridge"));
    }

    #[test]
    fn paths_are_derived_from_the_data_folder() {
        let config = Config::parse_from([
            "midi-bridge",
            "--data-dir",
            "/d",
            "--db-file",
            "x.db",
            "--songs-subdir",
            "songs",
            "--sessions-subdir",
            "sessions",
        ]); // fmt: skip
        assert_eq!(config.songs_dir(), PathBuf::from("/d/songs"));
        assert_eq!(config.sessions_dir(), PathBuf::from("/d/sessions"));
        assert_eq!(config.db_path(), PathBuf::from("/d/x.db"));
    }
}
