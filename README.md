# VR piano

A mixed-reality piano-learning app. The player wears a headset and plays a real MIDI keyboard.
A Unity app shows the keyboard, the songs and the reports. A separate bridge program connects
Unity to the keyboard.

| Folder | Contents |
|---|---|
| [`unity/`](unity) | The Unity project (Unity 6000.3.11f1). |
| [`bridge/`](bridge) | The bridge: Rust, plus Python tools for the PDF reports and online song search. See [`bridge/README.md`](bridge/README.md). |
| [`reference/`](reference) | The original Python bridge, used by the bridge tests as the reference implementation, and the original Unity simulator. Not used at run time. |

Unity starts the bridge when the scene starts and stops it when the scene stops. They talk over
UDP on `127.0.0.1` (ports 5005 and 5006).

## Setup on Windows

### 1. Install the tools (once)

In PowerShell:

```powershell
winget install Git.Git
winget install Rustlang.Rustup
winget install astral-sh.uv
winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

The last line installs the C++ build tools that Rust needs on Windows. Skip it if Visual Studio is
already installed with the "Desktop development with C++" workload.

Close and reopen PowerShell, then restart Unity Hub, so that both see the new `PATH`.

### 2. Clone and build the bridge

```powershell
git clone <repository-url> C:\Users\vrlab\Desktop\vr-piano
cd C:\Users\vrlab\Desktop\vr-piano\bridge
cargo build --release
uv sync --project python --frozen --no-dev
```

`cargo build` downloads the pinned Rust version on first use and produces
`bridge\target\release\midi-bridge.exe`. `uv sync` downloads Python and the report dependencies.
The bridge also runs it at start-up, but the first download needs a few minutes and an internet
connection.

### 3. Open the Unity project

In Unity Hub: **Add → Add project from disk**, select the `unity` folder, and open it with Unity
6000.3.11f1. Open `Assets/Scenes/SampleScene` and press Play. A console window opens with the bridge
logs.

The `UdpReceiver` component (in the scene) holds the launch settings:

- `bridgeExecutable`: `../bridge/target/release/midi-bridge.exe`, relative to the `unity` folder.
  An absolute path also works.
- `bridgeArguments`: the data folder (songs, song database, session reports) and the file names
  inside it. The default points at the data folder of the previous Python setup, so existing songs
  and reports stay available. Run `midi-bridge.exe --help` for all options.

### Updating

Stop Play mode first: Windows cannot replace `midi-bridge.exe` while it runs.

```powershell
cd C:\Users\vrlab\Desktop\vr-piano
git pull
cd bridge
cargo build --release
```

## Troubleshooting

| Symptom | Check |
|---|---|
| Unity logs `Impossibile avviare il bridge (<path>)` | `midi-bridge.exe` exists at that path (step 2). |
| The bridge window shows `no MIDI input` or picks the wrong port | Add `--midi-in "<part of the port name>"` (and `--midi-out`) to `bridgeArguments`. The window lists the ports it connected to. |
| `uv is not available: reports are disabled` | `uv --version` works in a new PowerShell; restart Unity Hub after installing `uv`. |
| The bridge window closes right after opening | Run `bridge\target\release\midi-bridge.exe` with the same arguments in PowerShell to read the error. `port 5006 is already in use` means another bridge is still running: close its window. |

## Development

The bridge tests run on macOS; see [`bridge/README.md`](bridge/README.md).
