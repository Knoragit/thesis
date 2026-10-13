//! Running the Python tools (report renderer, song search) as subprocesses through `uv`.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;

use serde::Deserialize;
use thiserror::Error;

use crate::core::report::SessionData;

#[derive(Debug, Error)]
pub enum ToolError {
    #[error("the Python project was not found (use --python-project)")]
    NoProject,
    #[error("cannot run uv: {0}")]
    Spawn(#[from] std::io::Error),
    #[error("{tool} failed: {stderr}")]
    Failed { tool: &'static str, stderr: String },
    #[error("{tool} printed something unexpected: {0}", tool = .1)]
    BadOutput(serde_json::Error, &'static str),
}

/// A song downloaded by `song-search`, saved in the folder it was told to use.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct Candidate {
    pub filename: String,
    pub title: Option<String>,
    pub artist: Option<String>,
}

#[derive(Debug, Deserialize)]
struct RenderOutput {
    pages: u32,
}

#[derive(Debug, Clone)]
pub struct PythonTools {
    project: Option<PathBuf>,
}

impl PythonTools {
    /// Uses the given project, or looks for `python/pyproject.toml` beside the executable's
    /// ancestors (a source checkout) and in the working directory.
    #[must_use]
    pub fn discover(configured: Option<&Path>) -> Self {
        let project = configured.map(Path::to_owned).or_else(|| {
            let exe = std::env::current_exe().ok();
            let from_exe = exe.iter().flat_map(|p| p.ancestors().skip(1));
            let from_cwd = std::env::current_dir()
                .ok()
                .into_iter()
                .flat_map(|p| p.ancestors().map(Path::to_owned).collect::<Vec<_>>());
            from_exe
                .map(Path::to_owned)
                .chain(from_cwd)
                .map(|dir| dir.join("python"))
                .find(|dir| dir.join("pyproject.toml").is_file())
        });
        if project.is_none() {
            tracing::warn!("Python project not found: reports and online search are unavailable");
        }
        Self { project }
    }

    /// Installs the Python dependencies in the background so the first report is not slow.
    pub fn warm_up(&self) {
        let Ok(mut command) = self.uv("sync") else {
            return;
        };
        thread::spawn(move || match command.output() {
            Ok(output) if output.status.success() => tracing::info!("Python tools ready"),
            Ok(output) => tracing::warn!(
                stderr = %String::from_utf8_lossy(&output.stderr).trim(),
                "uv sync failed; reports will try again when requested"
            ),
            Err(error) => tracing::warn!(%error, "uv is not available: reports are disabled"),
        });
    }

    /// `uv <subcommand>` pinned to the project and its lock file, without development tools.
    fn uv(&self, subcommand: &str) -> Result<Command, ToolError> {
        let project = self.project.as_ref().ok_or(ToolError::NoProject)?;
        let mut command = Command::new("uv");
        command
            .arg(subcommand)
            .args(["--project"])
            .arg(project)
            .args(["--frozen", "--no-dev"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        Ok(command)
    }

    fn run(&self, tool: &'static str, args: &[&str], stdin: &[u8]) -> Result<Vec<u8>, ToolError> {
        let mut command = self.uv("run")?;
        command.arg(tool).args(args);
        let mut child = command.spawn()?;
        // A tool that exits early closes its stdin; that failure shows up in its exit status.
        let _ = child.stdin.take().expect("stdin is piped").write_all(stdin);
        let output = child.wait_with_output()?;
        if output.status.success() {
            Ok(output.stdout)
        } else {
            Err(ToolError::Failed {
                tool,
                stderr: String::from_utf8_lossy(&output.stderr).trim().to_owned(),
            })
        }
    }

    /// Renders the report into `out_dir`; returns the number of pages.
    pub fn render_report(
        &self,
        data: &SessionData,
        out_dir: &Path,
        pdf_name: &str,
    ) -> Result<u32, ToolError> {
        let json = serde_json::to_vec(data).expect("session data always serializes");
        let out_dir = out_dir.to_string_lossy();
        let stdout = self.run(
            "report-renderer",
            &["--out-dir", &out_dir, "--pdf-name", pdf_name],
            &json,
        )?;
        let parsed: RenderOutput = serde_json::from_slice(&stdout)
            .map_err(|e| ToolError::BadOutput(e, "report-renderer"))?;
        Ok(parsed.pages)
    }

    /// Looks for songs online and downloads them into `dest`, in the order the site ranks them.
    pub fn search(
        &self,
        source: &str,
        query: &str,
        dest: &Path,
    ) -> Result<Vec<Candidate>, ToolError> {
        let dest = dest.to_string_lossy();
        let stdout = self.run(
            "song-search",
            &["--source", source, "--query", query, "--dest", &dest],
            b"",
        )?;
        serde_json::from_slice(&stdout).map_err(|e| ToolError::BadOutput(e, "song-search"))
    }
}
