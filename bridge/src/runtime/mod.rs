//! The part of the bridge that touches the outside world: sockets, MIDI ports, files,
//! threads and subprocesses. The decisions are made by [`crate::core`].

mod examples;
mod playback;
mod python;
mod report;
mod search;
mod services;
mod udp;

use std::sync::mpsc;

use anyhow::Context;

use crate::config::Config;
use crate::core::{Core, Effect, Input};
use crate::library::{Library, SongLibrary};
use crate::midi::Message;
use crate::midi::ports::{self, MidiOut};
use crate::protocol::Event;

use examples::ExampleSlot;
use playback::PlaybackSlot;
use python::PythonTools;
use services::{Clock, Services, UnitySender, Wake};

/// Runs the bridge until the process is stopped.
pub fn run(config: &Config) -> anyhow::Result<()> {
    std::fs::create_dir_all(config.songs_dir()).context("cannot create the songs folder")?;
    std::fs::create_dir_all(config.sessions_dir()).context("cannot create the sessions folder")?;

    let library = Library::open(&config.db_path(), &config.songs_dir())
        .context("cannot open the song database")?;
    library
        .scan_folder()
        .context("cannot scan the songs folder")?;

    let clock = Clock::start();
    let (wakes, inbox) = mpsc::channel();
    let unity = UnitySender::new(config.udp_unity).context("cannot create the Unity socket")?;
    let piano = open_piano(config);
    let services = Services {
        unity,
        piano,
        wakes,
        clock,
    };

    let commands = udp::bind(config.udp_listen).with_context(|| {
        format!(
            "port {} is already in use: close the other bridge and try again",
            config.udp_listen.port()
        )
    })?;
    let _keyboard = open_keyboard(config, &services);
    udp::spawn_receiver(commands, services.wakes.clone(), clock);
    tracing::info!(listen = %config.udp_listen, unity = %config.udp_unity, "bridge ready");
    // Unity waits for this before sending commands; it also asks again with pings.
    services.unity.send(&Event::BridgeReady);

    let tools = PythonTools::discover(config.python_project.as_deref());
    tools.warm_up();

    let mut executor = Executor {
        config,
        services: &services,
        tools: &tools,
        playback: PlaybackSlot::default(),
        examples: ExampleSlot::default(),
    };
    let mut core = Core::new();
    for Wake { input, at } in inbox {
        for effect in core.handle(input, at, &library as &dyn SongLibrary) {
            executor.execute(effect);
        }
    }
    Ok(())
}

fn open_piano(config: &Config) -> MidiOut {
    match MidiOut::open(config.midi_out.as_deref()) {
        Ok(piano) => {
            tracing::info!(port = ?piano.name, "MIDI output connected");
            piano
        }
        Err(error) => {
            tracing::warn!(%error, "no MIDI output: songs will not sound");
            MidiOut::disconnected()
        }
    }
}

/// Connects the keyboard. The returned handle must stay alive, or the connection closes.
fn open_keyboard(config: &Config, services: &Services) -> Option<ports::InputPort> {
    let (wakes, clock) = (services.wakes.clone(), services.clock);
    let on_message = move |bytes: &[u8]| {
        if let Some(note) = Message::from_bytes(bytes.to_vec()).note() {
            let _ = wakes.send(Wake {
                input: Input::Note(note),
                at: clock.now(),
            });
            tracing::trace!(note = note.note, "from keyboard");
        }
    };
    match ports::open_input(config.midi_in.as_deref(), on_message) {
        Ok(port) => {
            tracing::info!(port = %port.name, "MIDI input connected");
            Some(port)
        }
        Err(error) => {
            tracing::warn!(%error, "no MIDI input: only commands from Unity will work");
            None
        }
    }
}

/// Carries out the effects the core asks for.
struct Executor<'a> {
    config: &'a Config,
    services: &'a Services,
    tools: &'a PythonTools,
    playback: PlaybackSlot,
    examples: ExampleSlot,
}

impl Executor<'_> {
    fn execute(&mut self, effect: Effect) {
        match effect {
            Effect::Send(event) => self.services.unity.send(&event),
            Effect::StartPlayback { id, path } => self.playback.start(id, path, self.services),
            Effect::StopPlayback => self.playback.cancel(),
            Effect::StartExample(challenge) => self.examples.start(challenge, self.services),
            Effect::StopExample => self.examples.stop(),
            Effect::RenderReport(data) => report::spawn(
                data,
                &self.config.sessions_dir(),
                &self.config.report_name,
                self.tools,
                self.services,
            ),
            Effect::Search { query } => {
                search::spawn(query, &self.config.songs_dir(), self.tools, self.services);
            }
        }
    }
}
