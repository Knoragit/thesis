//! Engraving a song's score in the background, for the follow-me sheet music.
//!
//! Each song gets a folder in the scores folder with its piano reduction (`brano.mid`), when
//! its bars start (`battute.json`), the pages (`pagina_N.png`) and `score.json` (when each page
//! starts in the song). A folder newer than its song, engraved from the same reduction, is
//! reused as it is.

use std::path::{Path, PathBuf};
use std::thread;

use super::python::PythonTools;
use super::services::Services;
use crate::midi::Timeline;
use crate::midi::reduction::piano_reduction;
use crate::protocol::Event;

const SCORE_INFO: &str = "score.json";
const REDUCTION: &str = "brano.mid";
const BARS: &str = "battute.json";

/// A score request: the song, and the number Unity gave the request.
#[derive(Debug)]
pub struct Song {
    pub request: i64,
    pub filename: String,
    pub path: PathBuf,
}

/// Engraves the song (or finds it engraved already) and sends `score_ready` to Unity.
pub fn spawn(song: Song, scores_dir: &Path, tools: &PythonTools, services: &Services) {
    let Song {
        request,
        filename,
        path,
    } = song;
    let folder = scores_dir.join(folder_name(&filename));
    let (tools, services) = (tools.clone(), services.clone());
    thread::spawn(move || {
        let pages = match engrave(&path, &folder, &tools) {
            Ok(pages) => pages,
            Err(error) => {
                tracing::warn!(%error, file = %path.display(), "score not available");
                0
            }
        };
        services.unity.send(&Event::ScoreReady {
            request,
            filename,
            folder: folder.to_string_lossy().into_owned(),
            pages,
        });
    });
}

fn engrave(song: &Path, folder: &Path, tools: &PythonTools) -> anyhow::Result<u32> {
    let bytes = std::fs::read(song)?;
    let reduced = piano_reduction(&bytes)?;
    let (reduction, bars) = (folder.join(REDUCTION), folder.join(BARS));
    // A different reduction means the bridge changed what it engraves: engrave again.
    let same_reduction = std::fs::read(&reduction).is_ok_and(|old| old == reduced);
    if let Some(pages) = cached_pages(song, folder).filter(|_| same_reduction) {
        tracing::info!(folder = %folder.display(), pages, "score already engraved");
        return Ok(pages);
    }
    std::fs::create_dir_all(folder)?;
    std::fs::write(&reduction, reduced)?;
    std::fs::write(&bars, serde_json::to_vec(&Timeline::bar_starts(&bytes)?)?)?;
    tracing::info!(file = %song.display(), "engraving score");
    let pages = tools.render_score(&reduction, &bars, folder)?;
    tracing::info!(folder = %folder.display(), pages, "score ready");
    Ok(pages)
}

/// The page count of an engraving that is newer than the song, if there is one.
fn cached_pages(song: &Path, folder: &Path) -> Option<u32> {
    let info = folder.join(SCORE_INFO);
    let engraved = std::fs::metadata(&info).ok()?.modified().ok()?;
    let changed = std::fs::metadata(song).ok()?.modified().ok()?;
    if engraved < changed {
        return None;
    }
    let json: serde_json::Value = serde_json::from_slice(&std::fs::read(info).ok()?).ok()?;
    let pages = u32::try_from(json.get("pages")?.as_u64()?).ok()?;
    (pages > 0).then_some(pages)
}

/// A folder name for a song: its file name without the extension, with only safe characters.
fn folder_name(filename: &str) -> String {
    let stem = Path::new(filename)
        .file_stem()
        .map_or_else(|| filename.into(), |s| s.to_string_lossy());
    let name: String = stem
        .chars()
        .map(|c| {
            if c.is_alphanumeric() || " -_.()".contains(c) {
                c
            } else {
                '_'
            }
        })
        .collect();
    let name = name.trim_matches(['.', ' ']);
    if name.is_empty() {
        "brano".into()
    } else {
        name.into()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folder_names_are_safe() {
        assert_eq!(
            folder_name("Let It Be - Beatles.mid"),
            "Let It Be - Beatles"
        );
        assert_eq!(folder_name("a/b:c?.mid"), "b_c_");
        assert_eq!(folder_name("..mid"), "brano");
    }

    #[test]
    fn an_engraving_older_than_its_song_is_not_reused() {
        let dir = tempfile::tempdir().unwrap();
        let song = dir.path().join("a.mid");
        let folder = dir.path().join("a");
        std::fs::create_dir(&folder).unwrap();
        std::fs::write(folder.join(SCORE_INFO), br#"{"pages": 3}"#).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(20));
        std::fs::write(&song, b"x").unwrap();
        assert_eq!(cached_pages(&song, &folder), None);
        std::thread::sleep(std::time::Duration::from_millis(20));
        std::fs::write(folder.join(SCORE_INFO), br#"{"pages": 3}"#).unwrap();
        assert_eq!(cached_pages(&song, &folder), Some(3));
    }
}
