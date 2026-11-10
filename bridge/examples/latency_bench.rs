//! Measures how long the bridge takes to send its data to Unity, without Unity or a keyboard.
//!
//! 1. Keyboard -> Unity: the same steps as `open_keyboard` and the event loop (`runtime/mod.rs`):
//!    MIDI log line, `Message::from_bytes`, mpsc to the loop, `Core::handle`, `Event::encode`,
//!    `send_to` on UDP loopback, and a receiving socket in place of Unity's receive thread.
//! 2. Unity -> bridge -> Unity: the real `midi-bridge.exe`, `ping` answered by `bridge_ready`.
//! 3. Song playback: the real `midi-bridge.exe` plays a song; each note's arrival is compared with
//!    the time the MIDI file says it should be sent.
//!
//! `cargo run --release --example latency_bench -- <bridge.exe> <song.mid> <out.csv folder>`

#![allow(dead_code, dropping_copy_types, clippy::all, clippy::pedantic, clippy::zombie_processes)]

use std::fs::File;
use std::io::Write;
use std::net::UdpSocket;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::{Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

use midi_bridge::core::{Core, Effect, Input};
use midi_bridge::library::{LibraryError, SongLibrary, SongRecord};
use midi_bridge::midi::analysis::{self, ReferenceNote};
use midi_bridge::midi::{Message, Timeline};
use midi_bridge::protocol::SongEntry;

/// A library with no songs: notes never touch it.
struct NoSongs;

impl SongLibrary for NoSongs {
    fn list(&self) -> Result<Vec<SongEntry>, LibraryError> {
        Ok(Vec::new())
    }
    fn suggest(&self, _: &str) -> Result<Vec<SongEntry>, LibraryError> {
        Ok(Vec::new())
    }
    fn filename_by_id(&self, _: i64) -> Result<Option<String>, LibraryError> {
        Ok(None)
    }
    fn cached_search(&self, _: &str) -> Result<Option<SongRecord>, LibraryError> {
        Ok(None)
    }
    fn register(&self, _: &str, _: &SongRecord) -> Result<(), LibraryError> {
        Ok(())
    }
    fn song_file(&self, _: &str) -> Option<PathBuf> {
        None
    }
    fn reference_notes(&self, _: &str) -> Result<Vec<ReferenceNote>, LibraryError> {
        Ok(Vec::new())
    }
    fn bar_starts(&self, _: &str) -> Result<Vec<f64>, LibraryError> {
        Ok(Vec::new())
    }
}

fn us(d: Duration) -> f64 {
    d.as_secs_f64() * 1e6
}

#[derive(Debug, Clone, Copy)]
struct Stats {
    n: usize,
    mean: f64,
    p50: f64,
    p95: f64,
    p99: f64,
    max: f64,
}

fn stats(values: &[f64]) -> Stats {
    let mut v = values.to_vec();
    v.sort_by(f64::total_cmp);
    let at = |q: f64| v[((v.len() - 1) as f64 * q).round() as usize];
    Stats {
        n: v.len(),
        mean: v.iter().sum::<f64>() / v.len() as f64,
        p50: at(0.5),
        p95: at(0.95),
        p99: at(0.99),
        max: v[v.len() - 1],
    }
}

fn print_row(name: &str, s: Stats) {
    println!(
        "{name:<44} n={:<5} media={:>9.1} mediana={:>9.1} p95={:>9.1} p99={:>9.1} max={:>9.1} us",
        s.n, s.mean, s.p50, s.p95, s.p99, s.max
    );
}

struct Wake {
    seq: usize,
    input: Input,
}

/// Times of one note through the keyboard path, all from the same `Instant`.
#[derive(Default, Clone, Copy)]
struct Stamps {
    callback: Duration,
    logged: Duration,
    queued: Duration,
    loop_got: Duration,
    core_done: Duration,
    sent: Duration,
    received: Duration,
}

/// Keyboard -> Unity. `gap` is the time between notes; `chord` notes arrive back to back.
fn keyboard_path(notes: usize, gap: Duration, chord: usize, log: Option<&Path>) -> Vec<Stamps> {
    let base = Instant::now();
    let unity = UdpSocket::bind("127.0.0.1:0").unwrap();
    unity
        .set_read_timeout(Some(Duration::from_secs(2)))
        .unwrap();
    let target = unity.local_addr().unwrap();
    let total = notes * 2; // press + release

    let receiver = thread::spawn(move || {
        let mut buf = [0u8; 2048];
        let mut got = Vec::with_capacity(total);
        while got.len() < total {
            match unity.recv(&mut buf) {
                Ok(_) => got.push(base.elapsed()),
                Err(_) => break,
            }
        }
        got
    });

    let (wakes, inbox) = mpsc::channel::<Wake>();
    let event_loop = thread::spawn(move || {
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let mut core = Core::new();
        let mut out = Vec::with_capacity(total);
        let clock = Instant::now();
        for Wake { seq, input } in inbox {
            let loop_got = base.elapsed();
            let effects = core.handle(input, clock.elapsed().as_secs_f64(), &NoSongs);
            let core_done = base.elapsed();
            for effect in effects {
                if let Effect::Send(event) = effect {
                    socket.send_to(&event.encode(), target).unwrap();
                }
            }
            out.push((seq, loop_got, core_done, base.elapsed()));
        }
        out
    });

    let file = log.map(|dir| {
        std::fs::create_dir_all(dir).unwrap();
        Mutex::new(File::create(dir.join("Note_bench.txt")).unwrap())
    });
    let clock = Instant::now();
    // Same work as the `on_message` callback in `open_keyboard`.
    let on_message = |seq: usize, bytes: &[u8], stamps: &mut Vec<Stamps>| {
        let callback = base.elapsed();
        if let Some(file) = &file
            && (0x80..0xf0).contains(&bytes[0])
        {
            let hex: Vec<String> = bytes.iter().map(|b| format!("{b:02x}")).collect();
            let line = format!(
                "{:.6} in {}\n",
                clock.elapsed().as_secs_f64(),
                hex.join(" ")
            );
            file.lock().unwrap().write_all(line.as_bytes()).unwrap();
        }
        let logged = base.elapsed();
        if let Some(note) = Message::from_bytes(bytes.to_vec()).note() {
            let _ = wakes.send(Wake {
                seq,
                input: Input::Note(note),
            });
        }
        stamps.push(Stamps {
            callback,
            logged,
            queued: base.elapsed(),
            ..Stamps::default()
        });
    };

    let mut stamps = Vec::with_capacity(total);
    let mut seq = 0;
    let mut next = Instant::now();
    for group in 0..notes.div_ceil(chord) {
        for kind in [0x90u8, 0x80] {
            for i in 0..chord {
                let index = group * chord + i;
                if index >= notes {
                    break;
                }
                let note = 36 + (index % 48) as u8;
                on_message(
                    seq,
                    &[kind, note, if kind == 0x90 { 80 } else { 0 }],
                    &mut stamps,
                );
                seq += 1;
            }
            next += gap;
            spin_until(next);
        }
    }
    drop(on_message);
    drop(wakes);
    let looped = event_loop.join().unwrap();
    let received = receiver.join().unwrap();
    for (seq, loop_got, core_done, sent) in looped {
        stamps[seq].loop_got = loop_got;
        stamps[seq].core_done = core_done;
        stamps[seq].sent = sent;
    }
    for (stamp, at) in stamps.iter_mut().zip(received) {
        stamp.received = at;
    }
    stamps
}

/// Waits without the 1 ms granularity of `sleep`, so the test pacing adds no noise.
fn spin_until(target: Instant) {
    while Instant::now() < target {
        if target - Instant::now() > Duration::from_millis(2) {
            thread::sleep(Duration::from_millis(1));
        } else {
            std::hint::spin_loop();
        }
    }
}

fn report_keyboard(title: &str, stamps: &[Stamps], csv: &mut File) {
    println!("\n== {title} ==");
    let col = |f: &dyn Fn(&Stamps) -> f64| -> Vec<f64> { stamps.iter().map(f).collect() };
    let d = |a: Duration, b: Duration| us(a) - us(b);
    let rows: [(&str, Vec<f64>); 5] = [
        ("T2a scrittura log MIDI", col(&|s| d(s.logged, s.callback))),
        (
            "T2b parse + passaggio al ciclo (mpsc)",
            col(&|s| d(s.loop_got, s.logged)),
        ),
        ("T3b Core::handle", col(&|s| d(s.core_done, s.loop_got))),
        (
            "T3c encode JSON + send_to",
            col(&|s| d(s.sent, s.core_done)),
        ),
        (
            "T4  loopback fino al socket ricevente",
            col(&|s| d(s.received, s.sent)),
        ),
    ];
    for (name, values) in &rows {
        let s = stats(values);
        print_row(name, s);
        writeln!(
            csv,
            "{title};{name};{};{:.1};{:.1};{:.1};{:.1};{:.1}",
            s.n, s.mean, s.p50, s.p95, s.p99, s.max
        )
        .unwrap();
    }
    let total = col(&|s| d(s.received, s.callback));
    let s = stats(&total);
    print_row("TOTALE callback MIDI -> pacchetto ricevuto", s);
    writeln!(
        csv,
        "{title};TOTALE;{};{:.1};{:.1};{:.1};{:.1};{:.1}",
        s.n, s.mean, s.p50, s.p95, s.p99, s.max
    )
    .unwrap();
}

fn spawn_bridge(exe: &Path, data: &Path, listen: u16, unity: u16) -> Child {
    Command::new(exe)
        .args(["--data-dir"])
        .arg(data)
        .args([
            "--midi-in",
            "nessuna-porta-benchmark",
            "--midi-out",
            "nessuna-porta-benchmark",
            "--udp-listen",
            &format!("127.0.0.1:{listen}"),
            "--udp-unity",
            &format!("127.0.0.1:{unity}"),
        ])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("cannot start the bridge")
}

fn wait_for(socket: &UdpSocket, action: &str, timeout: Duration) -> Option<(Instant, String)> {
    let deadline = Instant::now() + timeout;
    let mut buf = [0u8; 65_536];
    while Instant::now() < deadline {
        if let Ok(len) = socket.recv(&mut buf) {
            let at = Instant::now();
            let text = String::from_utf8_lossy(&buf[..len]).into_owned();
            if text.contains(&format!("\"{action}\"")) {
                return Some((at, text));
            }
        }
    }
    None
}

fn command_round_trip(exe: &Path, data: &Path, csv: &mut File) {
    let unity = UdpSocket::bind("127.0.0.1:15105").unwrap();
    unity
        .set_read_timeout(Some(Duration::from_millis(50)))
        .unwrap();
    let mut bridge = spawn_bridge(exe, data, 15106, 15105);
    wait_for(&unity, "bridge_ready", Duration::from_secs(20)).expect("bridge did not start");
    thread::sleep(Duration::from_millis(500));
    while unity.recv(&mut [0u8; 65_536]).is_ok() {}

    let ping = br#"{"action": "ping"}"#;
    let mut rtt = Vec::new();
    for _ in 0..1000 {
        let sent = Instant::now();
        unity.send_to(ping, "127.0.0.1:15106").unwrap();
        if let Some((at, _)) = wait_for(&unity, "bridge_ready", Duration::from_secs(1)) {
            rtt.push(us(at - sent));
        }
        thread::sleep(Duration::from_millis(5));
    }
    let _ = bridge.kill();
    println!("\n== Comando Unity -> bridge -> Unity (midi-bridge.exe reale) ==");
    let s = stats(&rtt);
    print_row("ping -> bridge_ready (andata e ritorno)", s);
    writeln!(
        csv,
        "comando;ping->bridge_ready RTT;{};{:.1};{:.1};{:.1};{:.1};{:.1}",
        s.n, s.mean, s.p50, s.p95, s.p99, s.max
    )
    .unwrap();
}

/// Plays a song with the real bridge and compares each note's arrival with the MIDI file.
fn playback(exe: &Path, song: &Path, data: &Path, csv: &mut File) {
    let songs = data.join("Canzoni");
    std::fs::create_dir_all(&songs).unwrap();
    let name = song.file_name().unwrap().to_string_lossy().into_owned();
    std::fs::copy(song, songs.join(&name)).unwrap();

    // When each note should reach Unity, relative to the song start.
    let timeline = analysis::piano_part(&Timeline::load(song).unwrap());
    let mut clock = 0.0;
    let mut expected = Vec::new();
    for step in &timeline.steps {
        clock += step.delay;
        if step.message.as_ref().and_then(Message::note).is_some() {
            expected.push(clock);
        }
    }

    let unity = UdpSocket::bind("127.0.0.1:15205").unwrap();
    unity
        .set_read_timeout(Some(Duration::from_millis(50)))
        .unwrap();
    let mut bridge = spawn_bridge(exe, data, 15206, 15205);
    wait_for(&unity, "bridge_ready", Duration::from_secs(20)).expect("bridge did not start");
    thread::sleep(Duration::from_millis(500));
    while unity.recv(&mut [0u8; 65_536]).is_ok() {}

    let command = format!(r#"{{"action": "play_song", "filename": "{name}"}}"#);
    let asked = Instant::now();
    unity
        .send_to(command.as_bytes(), "127.0.0.1:15206")
        .unwrap();

    let mut buf = [0u8; 65_536];
    let mut first_progress = None;
    let mut arrivals = Vec::new();
    let mut clears = 0;
    let deadline = asked + Duration::from_secs_f64(clock + 10.0);
    while Instant::now() < deadline && clears < 2 {
        let Ok(len) = unity.recv(&mut buf) else {
            continue;
        };
        let at = Instant::now();
        let text = String::from_utf8_lossy(&buf[..len]);
        if text.contains("\"clear_scene\"") {
            clears += 1;
        } else if text.contains("\"song_progress\"") && first_progress.is_none() {
            first_progress = Some(at);
        } else if text.contains("\"press\"") || text.contains("\"release\"") {
            arrivals.push(at);
        }
    }
    let _ = bridge.kill();
    let origin = first_progress.expect("no song_progress");
    println!("\n== Riproduzione brano (midi-bridge.exe reale): {name} ==");
    println!(
        "play_song -> primo song_progress: {:.1} ms (lettura del file MIDI + avvio)",
        us(origin - asked) / 1000.0
    );
    writeln!(
        csv,
        "brano;play_song->primo song_progress;1;{:.1};;;;",
        us(origin - asked)
    )
    .unwrap();
    let n = arrivals.len().min(expected.len());
    println!("note attese {} ricevute {}", expected.len(), arrivals.len());
    let late: Vec<f64> = (0..n)
        .map(|i| us(arrivals[i] - origin) - expected[i] * 1e6)
        .collect();
    // The first progress is sent just before `origin` inside the bridge, so `late` is the
    // scheduling delay of each note relative to the song clock (plus loopback, ~tens of us).
    let s = stats(&late);
    print_row("ritardo nota rispetto al tempo del file", s);
    writeln!(
        csv,
        "brano;ritardo nota vs file MIDI;{};{:.1};{:.1};{:.1};{:.1};{:.1}",
        s.n, s.mean, s.p50, s.p95, s.p99, s.max
    )
    .unwrap();
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let exe = PathBuf::from(&args[1]);
    let song = PathBuf::from(&args[2]);
    let out = PathBuf::from(&args[3]);
    std::fs::create_dir_all(&out).unwrap();
    let tmp = tempfile::tempdir().unwrap();
    let mut csv = File::create(out.join("latenza_software.csv")).unwrap();
    writeln!(
        csv,
        "scenario;passo;n;media_us;mediana_us;p95_us;p99_us;max_us"
    )
    .unwrap();

    // Warm-up, then the real runs.
    let _ = keyboard_path(200, Duration::from_millis(2), 1, None);
    let s = keyboard_path(
        1000,
        Duration::from_millis(20),
        1,
        Some(&tmp.path().join("log")),
    );
    report_keyboard(
        "Tastiera, note singole, log MIDI attivo (come la scena)",
        &s,
        &mut csv,
    );
    let s = keyboard_path(1000, Duration::from_millis(20), 1, None);
    report_keyboard("Tastiera, note singole, senza log MIDI", &s, &mut csv);
    let s = keyboard_path(
        1000,
        Duration::from_millis(100),
        10,
        Some(&tmp.path().join("log")),
    );
    report_keyboard(
        "Tastiera, accordi da 10 note, log MIDI attivo",
        &s,
        &mut csv,
    );
    // Last note of each chord: what the user waits for to see the whole chord.
    let chord_last: Vec<f64> = s
        .chunks(10)
        .map(|c| us(c[c.len() - 1].received - c[0].callback))
        .collect();
    let st = stats(&chord_last);
    print_row("accordo: prima nota in -> ultima ricevuta", st);
    writeln!(csv, "Tastiera, accordi da 10 note, log MIDI attivo;accordo intero;{};{:.1};{:.1};{:.1};{:.1};{:.1}", st.n, st.mean, st.p50, st.p95, st.p99, st.max).unwrap();

    command_round_trip(&exe, &tmp.path().join("cmd"), &mut csv);
    playback(&exe, &song, &tmp.path().join("play"), &mut csv);
}
