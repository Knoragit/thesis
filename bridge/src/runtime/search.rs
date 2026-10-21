//! Online song search: download candidates with the Python tool, keep the playable ones.

use std::path::{Path, PathBuf};
use std::thread;

use super::python::{Candidate, PythonTools};
use super::services::Services;
use crate::core::Input;
use crate::library::SongRecord;
use crate::midi::{Timeline, analysis};

/// The sites to try, in order: the second only if the first yields nothing usable.
const SOURCES: [&str; 2] = ["freemidi", "bitmidi"];

pub fn spawn(query: String, songs_dir: &Path, tools: &PythonTools, services: &Services) {
    let (songs_dir, tools, services) = (songs_dir.to_owned(), tools.clone(), services.clone());
    thread::spawn(move || {
        let found = search(&query, &songs_dir, &tools);
        services.wake(Input::SearchFinished { query, found });
    });
}

fn search(query: &str, songs_dir: &Path, tools: &PythonTools) -> Vec<SongRecord> {
    for source in SOURCES {
        tracing::info!(source, query, "searching online");
        match download(source, query, songs_dir, tools) {
            Ok(found) if !found.is_empty() => return found,
            Ok(_) => tracing::info!(source, "no usable results"),
            Err(error) => tracing::warn!(source, %error, "search failed"),
        }
    }
    Vec::new()
}

fn download(
    source: &str,
    query: &str,
    songs_dir: &Path,
    tools: &PythonTools,
) -> anyhow::Result<Vec<SongRecord>> {
    // Downloads land in a scratch folder inside the data folder, so moving them is a rename.
    let scratch = tempfile::Builder::new()
        .prefix(".incoming-")
        .tempdir_in(songs_dir.parent().unwrap_or(songs_dir))?;
    let candidates = tools.search(source, query, scratch.path())?;
    Ok(candidates
        .into_iter()
        .filter_map(|candidate| keep_if_playable(candidate, scratch.path(), songs_dir))
        .collect())
}

/// Moves a downloaded song into the library if it has a piano part and suits the visualizer.
fn keep_if_playable(candidate: Candidate, scratch: &Path, songs_dir: &Path) -> Option<SongRecord> {
    // The name comes from another program: only a bare file name may touch the songs folder.
    if Path::new(&candidate.filename).file_name() != Some(candidate.filename.as_ref()) {
        tracing::warn!(file = %candidate.filename, "discarding download with a suspicious name");
        return None;
    }
    let downloaded: PathBuf = scratch.join(&candidate.filename);
    let timeline = match Timeline::load(&downloaded) {
        Ok(timeline) => timeline,
        Err(error) => {
            tracing::info!(file = %candidate.filename, %error, "discarding unreadable download");
            return None;
        }
    };
    if !analysis::has_enough_piano(&timeline) {
        tracing::info!(file = %candidate.filename, "discarding song without a piano part");
        return None;
    }
    if let Err(reason) = analysis::check_visualizer_safety(&timeline) {
        tracing::info!(file = %candidate.filename, %reason, "discarding unsuitable song");
        return None;
    }
    if let Err(error) = std::fs::rename(&downloaded, songs_dir.join(&candidate.filename)) {
        tracing::warn!(file = %candidate.filename, %error, "cannot store song");
        return None;
    }
    Some(SongRecord {
        filename: candidate.filename,
        title: candidate.title,
        artist: candidate.artist,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::midi::testing;

    fn candidate(name: &str) -> Candidate {
        Candidate {
            filename: name.to_owned(),
            title: Some("T".into()),
            artist: None,
        }
    }

    struct Dirs {
        _root: tempfile::TempDir,
        scratch: PathBuf,
        songs: PathBuf,
    }

    fn dirs() -> Dirs {
        let root = tempfile::tempdir().unwrap();
        let (scratch, songs) = (root.path().join("scratch"), root.path().join("songs"));
        std::fs::create_dir(&scratch).unwrap();
        std::fs::create_dir(&songs).unwrap();
        Dirs {
            _root: root,
            scratch,
            songs,
        }
    }

    #[test]
    fn a_playable_piano_song_is_moved_into_the_library() {
        let d = dirs();
        std::fs::write(d.scratch.join("ok.mid"), testing::melody(&[60])).unwrap();
        let kept = keep_if_playable(candidate("ok.mid"), &d.scratch, &d.songs).unwrap();
        assert_eq!(kept.filename, "ok.mid");
        assert!(d.songs.join("ok.mid").is_file());
        assert!(!d.scratch.join("ok.mid").exists());
    }

    #[test]
    fn unusable_downloads_stay_out_of_the_library() {
        let d = dirs();
        std::fs::write(d.scratch.join("violin.mid"), testing::with_program(40, 60)).unwrap();
        std::fs::write(d.scratch.join("hammer.mid"), testing::hammer()).unwrap();
        std::fs::write(d.scratch.join("junk.mid"), b"<html>").unwrap();
        for name in ["violin.mid", "hammer.mid", "junk.mid", "missing.mid"] {
            assert!(
                keep_if_playable(candidate(name), &d.scratch, &d.songs).is_none(),
                "{name}"
            );
        }
        assert_eq!(std::fs::read_dir(&d.songs).unwrap().count(), 0);
    }

    #[test]
    fn names_with_path_components_are_refused() {
        let d = dirs();
        std::fs::write(d.scratch.join("ok.mid"), testing::melody(&[60])).unwrap();
        for name in ["../ok.mid", "sub/ok.mid", "", ".."] {
            assert!(
                keep_if_playable(candidate(name), &d.scratch, &d.songs).is_none(),
                "{name:?}"
            );
        }
    }
}
