//! What the report renderer receives and returns.

use serde::Serialize;

use super::follow::FollowSession;
use super::recorder::RecordedNote;
use crate::protocol::Event;

/// Version of the JSON handed to the renderer.
pub const SCHEMA_VERSION: u32 = 1;

/// Everything a report is made of. Times are seconds; only differences within one list matter.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct SessionData {
    pub version: u32,
    pub fai_tu_notes: Vec<RecordedNote>,
    /// Only songs in which the user played at least one note.
    pub follow_sessions: Vec<FollowSession>,
}

impl SessionData {
    #[must_use]
    pub fn new(fai_tu_notes: Vec<RecordedNote>, follow_sessions: Vec<FollowSession>) -> Self {
        Self {
            version: SCHEMA_VERSION,
            fai_tu_notes,
            follow_sessions,
        }
    }
}

/// How rendering ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReportOutcome {
    Ready {
        file: String,
        folder: String,
        pages: u32,
    },
    Failed,
}

impl ReportOutcome {
    /// A failure is reported as a report with no pages, which Unity already handles
    /// ("report not found") without waiting for its timeout.
    #[must_use]
    pub fn into_event(self) -> Event {
        match self {
            Self::Ready {
                file,
                folder,
                pages,
            } => Event::ReportReady {
                file,
                folder,
                pages,
            },
            Self::Failed => Event::ReportReady {
                file: String::new(),
                folder: String::new(),
                pages: 0,
            },
        }
    }
}
