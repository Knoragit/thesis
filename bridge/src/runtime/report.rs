//! Generating a report in the background.

use std::path::Path;
use std::thread;

use super::python::PythonTools;
use super::services::Services;
use crate::core::Input;
use crate::core::report::{ReportOutcome, SessionData};

/// Renders `data` into a new session folder inside `sessions_dir`, then tells the event loop.
pub fn spawn(
    data: SessionData,
    sessions_dir: &Path,
    pdf_name: &str,
    tools: &PythonTools,
    services: &Services,
) {
    let (sessions_dir, pdf_name) = (sessions_dir.to_owned(), pdf_name.to_owned());
    let (tools, services) = (tools.clone(), services.clone());
    thread::spawn(move || {
        let outcome = match render(&data, &sessions_dir, &pdf_name, &tools) {
            Ok(outcome) => outcome,
            Err(error) => {
                tracing::error!(%error, "report failed");
                ReportOutcome::Failed
            }
        };
        services.wake(Input::ReportFinished(outcome));
    });
}

fn render(
    data: &SessionData,
    sessions_dir: &Path,
    pdf_name: &str,
    tools: &PythonTools,
) -> anyhow::Result<ReportOutcome> {
    let folder = sessions_dir.join(format!(
        "Sessione_{}",
        chrono::Local::now().format("%Y%m%d_%H%M%S")
    ));
    std::fs::create_dir_all(&folder)?;
    let pages = tools.render_report(data, &folder, pdf_name)?;
    tracing::info!(folder = %folder.display(), pages, "report ready");
    Ok(ReportOutcome::Ready {
        file: folder.join(pdf_name).to_string_lossy().into_owned(),
        folder: folder.to_string_lossy().into_owned(),
        pages,
    })
}
