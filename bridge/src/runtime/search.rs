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
    let downloaded: PathBuf = scratch.join(&candidate.filename);
    let timeline = match Timeline::load(&downloaded) {
        Ok(timeline) => timeline,
        Err(error) => {
            tracing::info!(file = %candidate.filename, %error, "discarding unreadable download");
            return None;
        }
    };
    if !analysis::has_piano_part(&timeline) {
        tracing::info!(file = %candidate.filename, "discarding song without piano");
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
