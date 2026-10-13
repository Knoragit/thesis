import os
import time
import json
import socket
import threading
import wave
import copy
import sqlite3
import hashlib
import urllib.request
import urllib.parse
from concurrent.futures import ThreadPoolExecutor
import numpy as np
import mido
import requests
import re
import matplotlib
matplotlib.use('Agg')
import matplotlib.patches as patches
import matplotlib.pyplot as plt
from matplotlib import font_manager
from matplotlib.backends.backend_pdf import PdfPages

# ==========================================
# UDP NETWORK & FILE CONFIGURATION
# ==========================================
UDP_IP = "127.0.0.1"
PORT_TO_UNITY = 5005
PORT_FROM_UNITY = 5006

DESKTOP_PATH = os.path.join(os.path.expanduser("~"), "Desktop", "Nora")
if not os.path.exists(DESKTOP_PATH):
    os.makedirs(DESKTOP_PATH)

SESSIONI_PATH = os.path.join(DESKTOP_PATH, "Sessioni")
if not os.path.exists(SESSIONI_PATH):
    os.makedirs(SESSIONI_PATH)

CANZONI_PATH = os.path.join(DESKTOP_PATH, "Canzoni")
if not os.path.exists(CANZONI_PATH):
    os.makedirs(CANZONI_PATH)

# ==========================================
# PDF FONT: Raleway, the same one used by the Unity app
# ==========================================
# matplotlib does not know the fonts installed in apps: the TTF file must be
# registered manually, otherwise reports would come out with DejaVu Sans. Raleway/static
# is used because it contains both Regular and Bold (titles use fontweight='bold');
# Resources/Fonts/ of the app only has Regular.
# The family name is not guessed: it is read from the file itself, so it also holds if
# the font were regenerated. If it is missing, a warning is shown and the default font
# is kept: the report must be produced anyway.
#
# "font.family" is a LIST for fallback: Raleway is a Latin font and does not
# contain the arrow U+2192 (used in all the evaluation box comments,
# "-> tocco piu' leggero..."). Without the fallback matplotlib would render a
# missing-glyph box instead of the arrow, so DejaVu Sans is kept behind Raleway,
# since it has the missing symbols.
RALWAY_DIR = os.path.join(DESKTOP_PATH, "Raleway", "static")
try:
    _ral_regular = os.path.join(RALWAY_DIR, "Raleway-Regular.ttf")
    _ral_bold = os.path.join(RALWAY_DIR, "Raleway-Bold.ttf")
    if os.path.exists(_ral_regular):
        font_manager.fontManager.addfont(_ral_regular)
        if os.path.exists(_ral_bold):
            font_manager.fontManager.addfont(_ral_bold)
        _ral_name = font_manager.FontProperties(fname=_ral_regular).get_name()
        plt.rcParams["font.family"] = [_ral_name, "DejaVu Sans"]
    else:
        print(f"[FONT] Raleway non trovato in {RALWAY_DIR}: i PDF useranno il font di default.")
except Exception as e:
    print(f"[FONT] Impossibile registrare Raleway ({e}): i PDF useranno il font di default.")

# Maximum number of results downloaded and saved for each online search
MAX_RESULTS = 4

# ==========================================
# LOCAL DATABASE CONFIGURATION (SQLite)
# ==========================================
DB_PATH = os.path.join(DESKTOP_PATH, "nora_database.db")

def init_db():
    """Creates the local SQLite database, creating (and migrating) the 'midi_files' table."""
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    c.execute('''CREATE TABLE IF NOT EXISTS midi_files
                 (id INTEGER PRIMARY KEY AUTOINCREMENT,
                  query TEXT,
                  filename TEXT UNIQUE,
                  title TEXT,
                  artist TEXT)''')
    # Migration: the previous version had UNIQUE on query, which prevented
    # saving more than one result for the same search. If detected, the table
    # is recreated with UNIQUE on filename and the existing data is copied over.
    sql = c.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name='midi_files'").fetchone()
    old_schema = bool(sql) and re.search(r'query[^,\n]*UNIQUE', sql[0], re.IGNORECASE)
    if old_schema:
        print("[DB] Migrazione tabella midi_files (UNIQUE query -> UNIQUE filename)...")
        c.execute('''CREATE TABLE midi_files_new
                     (id INTEGER PRIMARY KEY AUTOINCREMENT,
                      query TEXT,
                      filename TEXT UNIQUE,
                      title TEXT,
                      artist TEXT)''')
        c.execute('''INSERT OR IGNORE INTO midi_files_new (query, filename, title, artist)
                     SELECT query, filename, title, artist FROM midi_files''')
        c.execute("DROP TABLE midi_files")
        c.execute("ALTER TABLE midi_files_new RENAME TO midi_files")
    conn.commit()
    conn.close()

init_db()

# ==========================================
# MIDI SEARCH AND ANALYSIS ENGINE
# ==========================================
def has_piano_track(filepath):
    """Checks whether the MIDI file contains tracks/programs associated with the piano."""
    try:
        mid = mido.MidiFile(filepath)
        program_changes_seen = False
        
        for msg in mid:
            if msg.type == 'program_change':
                program_changes_seen = True
                # General MIDI: programs 0 to 7 identify the piano family
                if 0 <= msg.program <= 7:
                    return True
                    
        # If there are no 'program_change' events, the MIDI standard assigns
        # the default piano sound (Program 0)
        if not program_changes_seen:
            return True
            
        return False
    except Exception as e:
        print(f"[ERRORE ANALISI MIDI] {e}")
        return False

def is_midi_safe_for_visualizer(filepath):
    """Rejects "hammer" MIDI files (rips from games/sequencers) that saturate the
    visualizer: too many different notes relentlessly re-pressed make every key look
    pressed and "stuck". Returns (ok, reason)."""
    try:
        mid = mido.MidiFile(filepath)
        pitches = set()
        note_msgs = 0
        aperte = {}
        picco_aperte = 0
        for msg in mid:
            if msg.type in ('note_on', 'note_off'):
                note_msgs += 1
                if msg.type == 'note_on' and msg.velocity > 0:
                    pitches.add(msg.note)
                    aperte[msg.note] = True
                    picco_aperte = max(picco_aperte, len(aperte))
                else:
                    aperte.pop(msg.note, None)
        seconds = max(0.001, mid.length)
        evt_s = note_msgs / seconds

        if picco_aperte > 60:
            return False, f"troppe note simultanee ({picco_aperte}) - wall of sound/sequencer non adatto"
        if pitches and (min(pitches) < 21 or max(pitches) > 108):
            return False, f"note fuori dal pianoforte a 88 tasti ({min(pitches)}..{max(pitches)}) - arrangiamento non adatto"
        if evt_s > 150.0:
            return False, f"flusso note estremo ({evt_s:.0f} evt/s) - rip/sequencer non adatto"
        if len(pitches) > 70 and evt_s > 60.0:
            return False, f"copertura tastiera ({len(pitches)} note) e flusso alto ({evt_s:.0f} evt/s)"
        return True, ""
    except Exception as e:
        print(f"[ERRORE ANALISI SICUREZZA] {e}")
        return True, ""

def extract_song_metadata(html):
    """Extracts (title, artist) from a freemidi.org download page HTML."""
    title = None
    artist = None

    # Title 1 (exact): <div class=download-title-container style=display:none>Title</div>
    m = re.search(
        r'class=["\']?download-title-container["\']?[^>]*style=["\']?display:none["\']?>([^<]+)',
        html
    )
    if not m:
        # Title 2 (fallback): <div class=download-title-container>\n<h1>Title Midi</h1>
        m = re.search(r'class=["\']?download-title-container["\']?[^>]*>\s*<h1>([^<]+)</h1>', html)
    if m:
        title = m.group(1).strip()
        if title.lower().endswith(' midi'):
            title = title[:-5].strip()

    # Artist: <a itemprop=item href="/artist-123-slug"> <span itemprop=name>Name</span>
    m = re.search(
        r'href=["\']?/artist-\d+-[^"\']*["\']?[^>]*>\s*<span itemprop=["\']?name["\']?>([^<]+)',
        html
    )
    if m:
        artist = m.group(1).strip()

    return title, artist
def freemidi_search_candidates(query, max_results=MAX_RESULTS):
    """Extracts up to max_results candidate links from the freemidi.org search page."""
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        session = requests.Session()

        # 1. Search by scraping the HTML results page
        search_url = f"https://freemidi.org/search?q={urllib.parse.quote(query)}"
        resp = session.get(search_url, headers=headers, timeout=12)

        if resp.status_code != 200:
            return []

        # 2. Parse download3-{id}-{slug} links from the results (page order)
        links = list(dict.fromkeys(re.findall(r'download3-\d+-[\w-]+', resp.text)))
        return links[:max_results]

    except Exception as e:
        print(f"[ONLINE SEARCH ERROR] {e}")

    return []

def download_freemidi_candidate(slug):
    """Downloads a single candidate from freemidi.org. Returns the tuple or None."""
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        session = requests.Session()

        track_id = re.search(r'download3-(\d+)', slug).group(1)
        dl_page = f"https://freemidi.org/{slug}"

        # 3. Visit the download page to establish a session cookie and grab metadata
        page_response = session.get(dl_page, headers=headers, timeout=10)
        title, artist = extract_song_metadata(page_response.text)

        # 4. Download the .mid file via getter (requires Referer header)
        getter_url = f"https://freemidi.org/getter-{track_id}"
        file_response = session.get(
            getter_url,
            headers={**headers, 'Referer': dl_page},
            timeout=15
        )

        if file_response.status_code == 200 and file_response.content[:4] == b'MThd':
            filename = slug.replace('download3-', '') + ".mid"
            filepath = os.path.join(CANZONI_PATH, filename)
            with open(filepath, 'wb') as f:
                f.write(file_response.content)
            return filepath, filename, title, artist

    except Exception as e:
        print(f"[DOWNLOAD MIDI ERROR] {slug}: {e}")

    return None

def bitmidi_search_candidates(query, max_results=MAX_RESULTS):
    """Best-effort: extracts candidate links from the bitmidi.com search page."""
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        session = requests.Session()
        search_url = f"https://www.bitmidi.com/search?q={urllib.parse.quote(query)}"
        resp = session.get(search_url, headers=headers, timeout=12)
        if resp.status_code != 200:
            return []
        # Song page links: slugs ending with -mid (page order)
        hrefs = re.findall(r'href="(/[^"]*-mid)"', resp.text)
        candidates = []
        for h in hrefs:
            if h not in candidates:
                candidates.append("https://www.bitmidi.com" + h)
        return candidates[:max_results]
    except Exception as e:
        print(f"[BITMIDI SEARCH ERROR] {e}")
    return []

def download_bitmidi_candidate(page_url):
    """Downloads the first .mid link found in the bitmidi.com song page."""
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        session = requests.Session()
        resp = session.get(page_url, headers=headers, timeout=12)
        if resp.status_code != 200:
            return None

        mid_links = re.findall(r'(?:href|src)="(?:[^"]*)(/uploads/[^"]+\.mid)"', resp.text, re.IGNORECASE)
        if not mid_links:
            mid_links = re.findall(r'(?:href|src)="([^"]+\.mid[^"]*)"', resp.text, re.IGNORECASE)

        for link in mid_links:
            if not link.startswith("http"):
                link = "https://www.bitmidi.com" + link
            r = session.get(link, headers={**headers, 'Referer': page_url}, timeout=15)
            if r.status_code == 200 and r.content[:4] == b'MThd':
                filename = hashlib.md5(page_url.encode()).hexdigest()[:10] + ".mid"
                filepath = os.path.join(CANZONI_PATH, filename)
                with open(filepath, 'wb') as f:
                    f.write(r.content)
                title = None
                artist = None
                m = re.search(r'<title>([^<]+)</title>', resp.text, re.IGNORECASE)
                if m:
                    title = re.sub(r'\s*\.mid\s*.*$', '', m.group(1), flags=re.IGNORECASE).strip()
                return filepath, filename, title, artist

    except Exception as e:
        print(f"[BITMIDI DOWNLOAD ERROR] {page_url}: {e}")

    return None

def download_validated_candidates(candidates, downloader):
    """Downloads candidates in parallel, keeping only those with a piano part."""
    results = []
    if not candidates:
        return results
    with ThreadPoolExecutor(max_workers=len(candidates)) as executor:
        futures = [executor.submit(downloader, cand) for cand in candidates]
        for future in futures:
            try:
                result = future.result()
            except Exception as e:
                print(f"[PARALLEL DOWNLOAD ERROR] {e}")
                continue
            if not result:
                continue
            if has_piano_track(result[0]):
                safe, motivo = is_midi_safe_for_visualizer(result[0])
                if safe:
                    results.append(result)
                else:
                    if os.path.exists(result[0]):
                        os.remove(result[0])
                    print(f"[ERRORE FILTRO] Brano scartato per sicurezza: {motivo}")
            else:
                if os.path.exists(result[0]):
                    os.remove(result[0])
                print("[ERRORE FILTRO] Brano senza pianoforte scartato.")
    return results

def handle_song_request(query):
    """Runs the whole search pipeline: Local DB -> Online -> Piano Filter -> UDP reply."""
    clean_query = query.strip().lower()
    if not clean_query:
        return

    print(f"\n[RICERCA BRANO] Richiesta per: '{clean_query}'")
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    
    # 1. Check the Local Database (Cache)
    c.execute("SELECT filename, title, artist FROM midi_files WHERE query=? ORDER BY id LIMIT 1", (clean_query,))
    row = c.fetchone()
    
    if row:
        filename, db_title, db_artist = row
        filepath = os.path.join(CANZONI_PATH, filename)
        if os.path.exists(filepath):
            print(f"[DATABASE LOCALE] Trovato in memoria: {filename}")
            send_to_unity({"action": "search_result", "status": "success", "filename": filename, "title": db_title, "artist": db_artist})
            conn.close()
            return

    # 2. Search the Online Database: freemidi multi-result
    print("[ONLINE] Ricerca su freemidi.org in corso...")
    results = download_validated_candidates(
        freemidi_search_candidates(clean_query),
        download_freemidi_candidate
    )

    # 3. Fallback to bitmidi.com if freemidi produced no valid results
    if not results:
        print("[ONLINE] Nessun risultato freemidi valido, provo con bitmidi.com...")
        results = download_validated_candidates(
            bitmidi_search_candidates(clean_query),
            download_bitmidi_candidate
        )

    if not results:
        print("[ONLINE] Nessun risultato trovato.")
        send_to_unity({
            "action": "search_result", 
            "status": "error", 
            "message": "Canzone non trovata nel database online."
        })
        conn.close()
        return

    # 4. Save all validated songs to the local DB
    for filepath, filename, title, artist in results:
        try:
            c.execute("INSERT OR IGNORE INTO midi_files (query, filename, title, artist) VALUES (?, ?, ?, ?)",
                      (clean_query, filename, title, artist))
        except Exception as e:
            print(f"[DB] Errore salvataggio {filename}: {e}")
    conn.commit()

    # 5. UDP reply with the first valid song (suggestions will show all)
    filepath, filename, title, artist = results[0]
    print(f"[SUCCESS] Salvati {len(results)} brano/i per '{clean_query}', primo: {filename}")
    send_to_unity({"action": "search_result", "status": "success", "filename": filename, "title": title, "artist": artist})
    
    conn.close()

# ==========================================
# MIDI PORT HANDLING
# ==========================================
inport = None
outport = None

def select_physical_midi_port(port_names, is_output=False):
    """Picks the first physical MIDI port, skipping virtual/loopback/through ones."""
    if not port_names:
        return None
    ignore_keywords = ["loopmidi", "virtual", "through"]
    if is_output:
        ignore_keywords.append("microsoft gs")

    for name in port_names:
        if not any(key in name.lower() for key in ignore_keywords):
            return name
    return port_names[0]

try:
    input_names = mido.get_input_names()
    output_names = mido.get_output_names()
    best_input = select_physical_midi_port(input_names, is_output=False)
    best_output = select_physical_midi_port(output_names, is_output=True)

    if best_input:
        inport = mido.open_input(best_input)
        print(f"[MIDI IN] Connesso a: '{best_input}'")
    if best_output:
        outport = mido.open_output(best_output)
        print(f"[MIDI OUT] Connesso a: '{best_output}'")
except Exception as e:
    print(f"[ERRORE MIDI] {e}")

# ==========================================
# GLOBAL STATE AND SESSION HISTORY
# ==========================================
sock_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
send_lock = threading.Lock()

is_recording_fai_tu = False
recorded_notes_fai_tu = [] 
active_pressed_notes = {}   
fai_tu_nota_mano = {}   

t_first_note_fai_tu = None
t_last_note_fai_tu = None

is_playing_observing = False
is_following = False
is_playing_example = False
current_song_filename = "N/A"
follow_notes_sequence = []
follow_current_index = 0

recorded_follow_user_notes = [] 
active_pressed_follow = {}
current_song_reference_notes = [] 

history_follow_sessions = []

def send_to_unity(data_dict):
    """Sends a JSON message to Unity over UDP (thread-safe through a lock)."""
    msg = json.dumps(data_dict)
    with send_lock:
        sock_send.sendto(msg.encode('utf-8'), (UDP_IP, PORT_TO_UNITY))

def get_note_color_rgb(note, velocity, hand=None):
    """Returns an RGB tuple for a note (left hand cyan, right hand amber) based on velocity."""
    v = max(0.0, min(1.0, velocity))
    if hand is not None:
        is_left = (hand == "left")
    else:
        is_left = (note < 60)
    if is_left:
        return (0.0, 1.0 - v, 1.0)
    else:
        return (1.0, 0.92 * (1.0 - v), 0.0)

def parse_song_info(filename):
    """Heuristically parses "Title - Artist" (or "Title-Artist") from a song filename."""
    name_without_ext = os.path.splitext(filename)[0].replace("_", " ")
    if " - " in name_without_ext:
        parts = name_without_ext.split(" - ", 1)
        return {"title": parts[0].strip(), "artist": parts[1].strip()}
    elif "-" in name_without_ext:
        parts = name_without_ext.split("-", 1)
        return {"title": parts[0].strip(), "artist": parts[1].strip()}
    else:
        return {"title": name_without_ext.strip(), "artist": "Artista Non Specificato"}

def get_song_list():
    """Returns all songs stored in the local database."""
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    c.execute("SELECT id, query, filename, title, artist FROM midi_files ORDER BY id")
    rows = c.fetchall()
    conn.close()
    songs = []
    for rid, query, filename, title, artist in rows:
        # Use real metadata when present; heuristic parsing only if both are missing
        if not title and not artist:
            info = parse_song_info(filename)
            title, artist = info["title"], info["artist"]
        elif not title:
            title = "Titolo Sconosciuto"
        elif not artist:
            artist = "Artista Non Specificato"
        songs.append({
            "id": rid,
            "query": query,
            "filename": filename,
            "title": title,
            "artist": artist
        })
    return songs

def get_filename_by_id(song_id):
    """Returns the filename associated with a song ID, or None if not found."""
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    c.execute("SELECT filename FROM midi_files WHERE id=?", (song_id,))
    row = c.fetchone()
    conn.close()
    return row[0] if row else None

def scan_local_folder():
    """Registers all .mid files found in the Songs folder into the local DB."""
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    if not os.path.isdir(CANZONI_PATH):
        print(f"[SCAN CARTELLA] Cartella non trovata: {CANZONI_PATH}")
        conn.close()
        return 0
    aggiunti = 0
    for nome_file in os.listdir(CANZONI_PATH):
        if not nome_file.lower().endswith(".mid"):
            continue
        info = parse_song_info(nome_file)
        query = nome_file.lower()
        filepath = os.path.join(CANZONI_PATH, nome_file)
        safe, motivo = is_midi_safe_for_visualizer(filepath)
        if not safe:
            print(f"[SCAN CARTELLA] Brano a rischio saltato ({motivo}): {nome_file}")
            continue
        try:
            c.execute(
                "INSERT OR IGNORE INTO midi_files (query, filename, title, artist) VALUES (?, ?, ?, ?)",
                (query, nome_file, info["title"], info["artist"])
            )
            if c.rowcount and c.rowcount > 0:
                aggiunti += 1
        except Exception as e:
            print(f"[SCAN CARTELLA] Errore per {nome_file}: {e}")
    conn.commit()
    conn.close()
    print(f"[SCAN CARTELLA] Aggiunti {aggiunti} brani locali al database.")
    return aggiunti

def suggest_songs(query, limit=20):
    """Live partial search in the local DB for suggestions while typing."""
    clean = query.strip().lower()
    if not clean:
        return []
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()
    like = f"%{clean}%"
    c.execute(
        "SELECT id, query, filename, title, artist FROM midi_files "
        "WHERE title LIKE ? OR artist LIKE ? OR filename LIKE ? OR query LIKE ? "
        "ORDER BY id LIMIT ?",
        (like, like, like, like, limit)
    )
    rows = c.fetchall()
    conn.close()
    songs = []
    for rid, query, filename, title, artist in rows:
        songs.append({
            "id": rid,
            "query": query,
            "filename": filename,
            "title": title,
            "artist": artist
        })
    return songs

# ==========================================
# WAV AUDIO SYNTHESIZER
# ==========================================
def generate_wav_from_midi_notes(notes_list, output_filepath, sample_rate=44100):
    """Synthesizes a WAV audio file from the given MIDI notes (sine + harmonic timbre)."""
    if not notes_list:
        print("[AUDIO WARN] Lista note vuota, genero un tono di test.")
        notes_list = [{"note": 60, "vel": 0.8, "start": 0.0, "end": 1.0}]

    t0 = min(n["start"] for n in notes_list)
    t_end = max(n["end"] for n in notes_list)
    
    total_duration = max(2.0, (t_end - t0) + 1.0)
    total_samples = int(total_duration * sample_rate)
    audio_buffer = np.zeros(total_samples, dtype=np.float32)

    for n in notes_list:
        start_sec = n["start"] - t0
        raw_dur = n["end"] - n["start"]
        dur_sec = max(0.3, raw_dur)
        
        freq = 440.0 * (2.0 ** ((n["note"] - 69) / 12.0))
        vel = max(0.2, min(1.0, n["vel"]))

        num_samples_note = int(dur_sec * sample_rate)
        t_note = np.linspace(0, dur_sec, num_samples_note, endpoint=False)

        sine_fundamental = np.sin(2 * np.pi * freq * t_note)
        sine_harmonic = 0.3 * np.sin(2 * np.pi * 2 * freq * t_note)
        raw_wave = (sine_fundamental + sine_harmonic) * vel

        attack_len = int(min(0.01 * sample_rate, num_samples_note / 2))
        envelope = np.ones(num_samples_note, dtype=np.float32)
        if attack_len > 0:
            envelope[:attack_len] = np.linspace(0, 1, attack_len)
        
        decay = np.exp(-3.0 * t_note / dur_sec)
        note_audio = raw_wave * envelope * decay

        start_idx = int(start_sec * sample_rate)
        end_idx = start_idx + len(note_audio)

        if end_idx <= total_samples:
            audio_buffer[start_idx:end_idx] += note_audio

    max_val = np.max(np.abs(audio_buffer))
    if max_val > 0:
        audio_buffer = (audio_buffer / max_val) * 0.85
    else:
        t_fallback = np.linspace(0, 1.0, sample_rate, endpoint=False)
        audio_buffer[:sample_rate] = 0.5 * np.sin(2 * np.pi * 440.0 * t_fallback)

    audio_int16 = (audio_buffer * 32767).astype(np.int16)
    with wave.open(output_filepath, 'wb') as wf:
        wf.setnchannels(1) 
        wf.setsampwidth(2) 
        wf.setframerate(sample_rate)
        wf.writeframes(audio_int16.tobytes())

    print(f"[AUDIO SUCCESS] WAV generato ed udibile in: {output_filepath}")

# ==========================================
# LOADING THE TARGET SONG
# ==========================================
def load_follow_sequence(filename):
    """Loads a MIDI file and builds the chord sequence and reference notes for 'Seguimi' mode."""
    global follow_notes_sequence, follow_current_index, current_song_reference_notes, current_song_filename
    current_song_filename = filename
    filepath = os.path.join(CANZONI_PATH, filename)
    if not os.path.exists(filepath):
        print(f"[ERRORE] File MIDI non trovato: {filepath}")
        return

    mid = mido.MidiFile(filepath)
    current_song_reference_notes = []

    active_ref_notes = {}
    accumulated_time = 0.0

    for msg in mid:
        accumulated_time += msg.time
        if msg.type == 'note_on' and msg.velocity > 0:
            active_ref_notes[msg.note] = {"start": accumulated_time, "vel": msg.velocity / 127.0}
        elif msg.type == 'note_off' or (msg.type == 'note_on' and msg.velocity == 0):
            if msg.note in active_ref_notes:
                ref_info = active_ref_notes.pop(msg.note)
                dur = max(0.05, accumulated_time - ref_info["start"])
                current_song_reference_notes.append({
                    "note": msg.note,
                    "vel": ref_info["vel"],
                    "start": ref_info["start"],
                    "dur": dur
                })

    current_song_reference_notes.sort(key=lambda x: x["start"])

    grouped_chords = []
    current_chord = []
    last_t = -1.0

    for n in current_song_reference_notes:
        t = n["start"]
        note = n["note"]
        if last_t < 0 or (t - last_t) < 0.05:
            current_chord.append(note)
        else:
            grouped_chords.append(current_chord)
            current_chord = [note]
        last_t = t

    if current_chord:
        grouped_chords.append(current_chord)

    follow_notes_sequence = grouped_chords
    follow_current_index = 0

def send_next_follow_step():
    """Sends the next expected chord to Unity, or completes the 'Seguimi' session."""
    global follow_current_index, is_following
    if follow_current_index < len(follow_notes_sequence):
        chord_notes = list(dict.fromkeys(follow_notes_sequence[follow_current_index]))
        send_to_unity({"action": "expect_notes", "notes": chord_notes})
        follow_current_index += 1
    else:
        send_to_unity({"action": "clear_scene"})
        is_following = False
        print("[SEGUIMI] Brano completato!")

# ==========================================
# MIDI INPUT LISTENER
# ==========================================
def midi_input_loop():
    """Watches the MIDI input port, forwards notes to Unity and records them in the active modes."""
    global is_recording_fai_tu, t_first_note_fai_tu, t_last_note_fai_tu
    if not inport:
        return

    for msg in inport:
        if msg.type in ['note_on', 'note_off']:
            note = msg.note
            vel = msg.velocity / 127.0
            action = "press" if (msg.type == 'note_on' and msg.velocity > 0) else "release"
            now = time.time()

            send_to_unity({"note": note, "velocity": vel, "action": action})

            if is_recording_fai_tu:
                if action == "press":
                    if t_first_note_fai_tu is None:
                        t_first_note_fai_tu = now
                    active_pressed_notes[note] = {"vel": vel, "start": now}
                elif action == "release" and note in active_pressed_notes:
                    start_info = active_pressed_notes.pop(note)
                    recorded_notes_fai_tu.append({
                        "note": note,
                        "vel": start_info["vel"],
                        "start": start_info["start"],
                        "end": now,
                        "hand": fai_tu_nota_mano.pop(note, None)
                    })
                    t_last_note_fai_tu = now

            if is_following:
                if action == "press":
                    active_pressed_follow[note] = {"vel": vel, "start": now}
                elif action == "release" and note in active_pressed_follow:
                    start_info = active_pressed_follow.pop(note)
                    note_obj = {
                        "note": note,
                        "vel": start_info["vel"],
                        "start": start_info["start"],
                        "end": now
                    }
                    recorded_follow_user_notes.append(note_obj)
                    if history_follow_sessions:
                        history_follow_sessions[-1]["user_notes"].append(note_obj)

# ==========================================
# OBSERVER PLAYBACK
# ==========================================
def play_observing_thread(filename):
    """Replays a song through the piano and streams its notes to Unity ('Observer' mode)."""
    global is_playing_observing, current_song_filename
    
    if is_playing_observing:
        is_playing_observing = False
        time.sleep(0.15)

    current_song_filename = filename
    filepath = os.path.join(CANZONI_PATH, filename)
    if not os.path.exists(filepath):
        print(f"[ERRORE AUDIO] File non trovato: {filepath}")
        return

    if outport:
        for ch in range(16):
            outport.send(mido.Message('control_change', channel=ch, control=123, value=0))
    send_to_unity({"action": "clear_scene"})

    is_playing_observing = True
    mid = mido.MidiFile(filepath)

    try:
        start_real_time = time.time()
        for msg in mid:
            if not is_playing_observing:
                break
            
            if msg.time > 0:
                target_time = start_real_time + msg.time
                while time.time() < target_time:
                    if not is_playing_observing:
                        break
                    time.sleep(0.001)
                start_real_time = target_time

            if not is_playing_observing:
                break

            if outport and not msg.is_meta:
                outport.send(msg)

            if msg.type in ['note_on', 'note_off']:
                vel = msg.velocity / 127.0
                action = "press" if (msg.type == 'note_on' and msg.velocity > 0) else "release"
                send_to_unity({"note": msg.note, "velocity": vel, "action": action})
                
    except Exception as e:
        print(f"[ERRORE PLAYING] {e}")
    finally:
        is_playing_observing = False
        if outport:
            for ch in range(16):
                outport.send(mido.Message('control_change', channel=ch, control=123, value=0))
        send_to_unity({"action": "clear_scene"})
        print(f"[PLAYING] Terminato o interrotto: {filename}")

# ==========================================
# TUTORIAL EXAMPLES (demonstrative playback)
# ==========================================
# Every challenge first shows HOW it should be played: notes always start from middle C
# (MIDI 60). The examples are sent both to the piano (sound) and to Unity (columns).
def play_tutorial_example(sfida):
    """Plays the tutorial demonstration for the given challenge (1-4) on piano and streams it to Unity."""
    global is_playing_example
    if is_playing_observing or is_following:
        is_playing_example = False
        print("[ESEMPIO] Saltato (osservatore/seguimi attivi): avanzo comunque.")
        send_to_unity({"action": "example_done"})
        return

    if sfida == 4:
        # Legato (C -> E): the E starts while the C is still held, with
        # two well-defined attacks apart in time ("first one, then the other").
        is_playing_example = True
        print(f"[ESEMPIO] Riproduzione esempio sfida {sfida}")
        try:
            if outport:
                outport.send(mido.Message('note_on', note=60, velocity=64))
            send_to_unity({"note": 60, "velocity": 0.5, "action": "press"})
            time.sleep(0.60)
            if outport:
                outport.send(mido.Message('note_on', note=64, velocity=64))
            send_to_unity({"note": 64, "velocity": 0.5, "action": "press"})
            time.sleep(0.85)
            for nota in (64, 60):
                if outport:
                    outport.send(mido.Message('note_off', note=nota))
                send_to_unity({"note": nota, "velocity": 0.5, "action": "release"})
                time.sleep(0.10)
        except Exception as e:
            print(f"[ERRORE ESEMPIO] {e}")
        finally:
            is_playing_example = False
            send_to_unity({"action": "clear_scene"})
            send_to_unity({"action": "example_done"})
            print("[ESEMPIO] Terminato")
        return

    # (note, velocity, duration_sec, pause_sec)
    if sfida == 1:
        sequenza = [(60, 0.18, 1.20, 0.10)]                       # Do centrale, tocco piano
    elif sfida == 2:
        sequenza = [(60, 0.25, 0.35, 0.40),                        # Do -> Re -> Mi -> Fa
                    (62, 0.35, 0.35, 0.40),
                    (64, 0.45, 0.35, 0.40),
                    (65, 0.55, 0.35, 0.10)]
    elif sfida == 3:
        # Staccato: C, wide pause, D (well separated, no legato effect)
        sequenza = [(60, 0.50, 0.18, 0.40),
                    (62, 0.50, 0.15, 0.10)]
    else:
        is_playing_example = False
        return

    is_playing_example = True
    print(f"[ESEMPIO] Riproduzione esempio sfida {sfida}")

    try:
        for nota, vel, durata, pausa in sequenza:
            if not is_playing_example:
                break
            if outport:
                outport.send(mido.Message('note_on', note=nota, velocity=int(vel * 127)))
            send_to_unity({"note": nota, "velocity": vel, "action": "press"})
            time.sleep(durata)
            if outport:
                outport.send(mido.Message('note_off', note=nota))
            send_to_unity({"note": nota, "velocity": vel, "action": "release"})
            time.sleep(pausa)
    except Exception as e:
        print(f"[ERRORE ESEMPIO] {e}")
    finally:
        is_playing_example = False
        send_to_unity({"action": "clear_scene"})
        send_to_unity({"action": "example_done"})
        print("[ESEMPIO] Terminato")

# ==========================================
# PDF REPORT GENERATION
# ==========================================
last_report_time = 0


def compute_radar_metrics(notes, is_ref=False):
    """Computes the 5 fingerprint metrics (same formulas as the 'Fai Tu' mode)."""
    if not notes:
        return [0.5, 0.0, 0.5, 0.0, 0.5]

    t0 = min(n["start"] for n in notes)
    data = []
    for n in notes:
        start = n["start"] - t0
        if is_ref:
            dur = max(0.05, n.get("dur", 0.1))
        else:
            dur = max(0.05, n.get("end", n["start"] + 0.1) - n["start"])
        data.append({"note": n["note"], "vel": n["vel"], "start": start, "dur": dur})

    velocities = [n["vel"] for n in data]
    durations = [n["dur"] for n in data]
    starts = sorted(n["start"] for n in data)

    mean_vel = float(np.mean(velocities)) if velocities else 0.5
    std_vel = float(np.std(velocities)) if len(velocities) > 1 else 0.0
    mean_dur = float(np.mean(durations)) if durations else 0.2
    iois = np.diff(starts) if len(starts) > 1 else [0.0]
    std_ioi = float(np.std(iois)) if len(iois) > 1 else 0.0
    vel_sx = [n["vel"] for n in data if n["note"] < 60]
    vel_dx = [n["vel"] for n in data if n["note"] >= 60]
    mean_sx = float(np.mean(vel_sx)) if vel_sx else 0.0
    mean_dx = float(np.mean(vel_dx)) if vel_dx else 0.0

    v1 = min(1.0, mean_vel * 1.2)
    v2 = min(1.0, std_vel * 4.0)
    v3 = min(1.0, mean_dur * 2.0)
    v4 = min(1.0, std_ioi * 3.0)
    v5 = min(1.0, (mean_dx / (mean_sx + 0.001)) * 0.5) if mean_sx > 0 else 0.5
    return [v1, v2, v3, v4, v5]


def classifica_articolazione(notes):
    """Classifies each note as legato/staccato/medio based on the gap to the next onset.
    Returns (counts, labels in input order)."""
    if not notes:
        return {"legato": 0, "staccato": 0, "medio": 0}, []

    t0 = min(n["start"] for n in notes)
    indexed = []
    for idx, n in enumerate(notes):
        start = n["start"] - t0
        if "end" in n:
            dur = max(0.05, n["end"] - n["start"])
        else:
            dur = max(0.05, n.get("dur", 0.1))
        indexed.append([idx, start, dur])

    indexed.sort(key=lambda x: x[1])
    gaps = [indexed[i + 1][1] - indexed[i][1] for i in range(len(indexed) - 1) if indexed[i + 1][1] - indexed[i][1] > 0]
    med_gap = float(np.median(gaps)) if gaps else 0.2

    labels = [None] * len(notes)
    counts = {"legato": 0, "staccato": 0, "medio": 0}
    for i, (idx, start, dur) in enumerate(indexed):
        if i + 1 < len(indexed):
            gap = max(0.05, indexed[i + 1][1] - start)
        else:
            gap = max(0.05, med_gap)
        ratio = dur / gap
        if ratio >= 0.85:
            tag = "legato"
        elif ratio <= 0.55:
            tag = "staccato"
        else:
            tag = "medio"
        labels[idx] = tag
        counts[tag] += 1
    return counts, labels


def allinea_note(usr_notes, ref_notes, tol=0.35, ref_window=None):
    """Matches user and reference notes by pitch, chronologically, using relative time
    (each series normalized to its own start). Note: user notes use wall-clock time
    (time.time()), reference notes use MIDI file time, so the comparison must happen on
    a relative basis, not on absolute onsets.
    If ref_window is given (seconds, relative to the reference start), the comparison only
    considers target notes within that span: "missed" then measures the guide notes in the
    actually performed segment, not the whole song."""
    if not usr_notes or not ref_notes:
        return {"matched": 0, "missed": 0, "extra": 0, "vel_ratios": [], "dur_ratios": [], "onset_errors": []}

    u_t0 = min(n["start"] for n in usr_notes)
    r_t0 = min(n["start"] for n in ref_notes)

    if ref_window is not None:
        ref_notes = [r for r in ref_notes if (r["start"] - r_t0) <= ref_window]
        if not ref_notes:
            return {"matched": 0, "missed": 0, "extra": 0, "vel_ratios": [], "dur_ratios": [], "onset_errors": []}

    usr_by_pitch = {}
    for u in usr_notes:
        usr_by_pitch.setdefault(u["note"], []).append([u["start"] - u_t0, u])
    ref_by_pitch = {}
    for r in ref_notes:
        ref_by_pitch.setdefault(r["note"], []).append([r["start"] - r_t0, r])

    for k in usr_by_pitch:
        usr_by_pitch[k].sort(key=lambda x: x[0])
    for k in ref_by_pitch:
        ref_by_pitch[k].sort(key=lambda x: x[0])

    matched = 0
    extra = 0
    vel_ratios = []
    dur_ratios = []
    onset_errors = []

    for note, u_list in usr_by_pitch.items():
        r_list = ref_by_pitch.get(note)
        if not r_list:
            extra += len(u_list)
            continue
        for i, (u_rel, u) in enumerate(u_list):
            if i >= len(r_list):
                extra += 1
                continue
            r_rel, rr = r_list[i]
            err = abs(u_rel - r_rel)
            if err > tol:
                extra += 1
                continue
            matched += 1
            ref_vel = max(0.001, rr["vel"])
            usr_dur = max(0.05, u.get("end", u["start"] + 0.1) - u["start"])
            ref_dur = max(0.05, rr.get("dur", 0.1))
            vel_ratios.append(min(2.0, u["vel"] / ref_vel))
            dur_ratios.append(min(2.0, usr_dur / ref_dur))
            onset_errors.append(err)

    missed = max(0, len(ref_notes) - matched)
    return {
        "matched": matched,
        "missed": missed,
        "extra": extra,
        "vel_ratios": vel_ratios,
        "dur_ratios": dur_ratios,
        "onset_errors": onset_errors,
    }


def has_report_material():
    """Returns True if there is something to generate a report from: recorded
    Fai Tu notes or at least one Follow-me session with user notes."""
    if recorded_notes_fai_tu:
        return True
    return any(s.get("user_notes") for s in history_follow_sessions)


def generate_unified_report():
    """Builds the complete PDF report (chromagrams, evaluation, radar, guide) plus page PNGs."""
    global last_report_time
    now = time.time()
    
    if now - last_report_time < 5.0:
        print("[REPORT IGNORED] Richiesta duplicata bloccata.")
        return
    
    last_report_time = now

    timestamp_str = time.strftime("%Y%m%d_%H%M%S")
    session_dir = os.path.join(SESSIONI_PATH, f"Sessione_{timestamp_str}")
    os.makedirs(session_dir, exist_ok=True)

    pdf_filepath = os.path.join(session_dir, "Report_Completo_Nora.pdf")
    wav_filepath = os.path.join(session_dir, "Registrazione_Audio.wav")

    local_fai_tu_notes = copy.deepcopy(recorded_notes_fai_tu)

    generate_wav_from_midi_notes(local_fai_tu_notes, wav_filepath)

    try:
        with PdfPages(pdf_filepath) as pdf:
            page_number = 0

            fig1 = plt.figure(figsize=(8.5, 11))
            fig1.text(0.5, 0.958, "REPORT · MODALITÀ FAI TU", ha='center', va='center', fontsize=15,
                      fontweight='bold', bbox=dict(boxstyle='round,pad=0.45', facecolor='#f8f9fa', edgecolor='#0288d1', linewidth=1.4))

            gs1 = fig1.add_gridspec(3, 1, height_ratios=[1.3, 0.38, 1.0], left=0.12, right=0.88, top=0.90, bottom=0.05, hspace=0.45)

            if local_fai_tu_notes:
                t0 = min(n["start"] for n in local_fai_tu_notes)
                notes_data = [{
                    "note": n["note"],
                    "vel": n["vel"],
                    "start": n["start"] - t0,
                    "end": n["end"] - t0,
                    "hand": n.get("hand")
                } for n in local_fai_tu_notes]

                velocities = [n["vel"] for n in notes_data]
                starts = sorted([n["start"] for n in notes_data])

                std_vel = float(np.std(velocities)) if len(velocities) > 1 else 0.0
                vel_sx = [n["vel"] for n in notes_data if n["note"] < 60]
                vel_dx = [n["vel"] for n in notes_data if n["note"] >= 60]
                mean_sx = float(np.mean(vel_sx)) if vel_sx else 0.0
                mean_dx = float(np.mean(vel_dx)) if vel_dx else 0.0
                
                iois = np.diff(starts) if len(starts) > 1 else [0.0]
                std_ioi = float(np.std(iois)) if len(iois) > 1 else 0.0

                articol_counts, _ = classifica_articolazione(notes_data)
                tot_art = max(1, articol_counts["legato"] + articol_counts["staccato"] + articol_counts["medio"])
                pct_leg = 100.0 * articol_counts["legato"] / tot_art
                pct_sta = 100.0 * articol_counts["staccato"] / tot_art
                pct_med = 100.0 * articol_counts["medio"] / tot_art

                ax1 = fig1.add_subplot(gs1[0])
                ax1.set_title("Cromagramma dell'Esecuzione (Piano Roll)", fontsize=10, pad=8)
                max_end = 1.0
                for n in notes_data:
                    dur = max(0.1, n["end"] - n["start"])
                    if (n["start"] + dur) > max_end:
                        max_end = n["start"] + dur
                    color = get_note_color_rgb(n["note"], n["vel"], n.get("hand"))
                    rect = patches.Rectangle((n["start"], n["note"] - 0.4), dur, 0.8,
                                             linewidth=0.4, edgecolor='black', facecolor=color)
                    ax1.add_patch(rect)

                ax1.set_xlim(0, max_end + 0.5)
                ax1.set_ylim(20, 109)
                ax1.set_ylabel("Pitch MIDI", fontsize=8)
                ax1.set_xlabel("Tempo (secondi)", fontsize=8)
                ax1.tick_params(axis='both', labelsize=8)
                ax1.grid(True, linestyle='--', alpha=0.3)

                ax_leg = fig1.add_subplot(gs1[1])
                ax_leg.axis('off')
                v_g = np.linspace(0, 1, 150)
                sx_rgb = np.stack([np.zeros(150), 1.0 - v_g, np.ones(150)], axis=1)[None, :, :]
                dx_rgb = np.stack([np.ones(150), 0.92 * (1.0 - v_g), np.zeros(150)], axis=1)[None, :, :]
                ax_leg.imshow(sx_rgb, extent=[0.20, 1.55, 0.80, 1.25], aspect='auto', interpolation='nearest')
                ax_leg.imshow(dx_rgb, extent=[1.95, 3.30, 0.80, 1.25], aspect='auto', interpolation='nearest')
                ax_leg.set_xlim(0.0, 3.6)
                ax_leg.set_ylim(0.3, 1.7)
                ax_leg.text(0.88, 1.45, "MANO SINISTRA", fontsize=7.5, ha='center', va='center', color='white',
                            bbox=dict(facecolor='black', alpha=0.5, boxstyle='round,pad=0.18'))
                ax_leg.text(2.63, 1.45, "MANO DESTRA", fontsize=7.5, ha='center', va='center', color='white',
                            bbox=dict(facecolor='black', alpha=0.4, boxstyle='round,pad=0.18'))
                ax_leg.text(0.20, 0.55, "piano", fontsize=7, color='dimgray', ha='center')
                ax_leg.text(1.55, 0.55, "forte", fontsize=7, color='dimgray', ha='center')
                ax_leg.text(1.95, 0.55, "piano", fontsize=7, color='dimgray', ha='center')
                ax_leg.text(3.30, 0.55, "forte", fontsize=7, color='dimgray', ha='center')
                ax_leg.set_title("Intensità per mano (hand tracking)", fontsize=8.5, pad=6, style='italic', color='dimgray')

                ax2 = fig1.add_subplot(gs1[2])
                ax2.axis('off')

                soglia_vel = 0.12
                soglia_ioi = 0.15
                suggerimenti = []

                if std_vel < soglia_vel:
                    suggerimenti.append("La tua esecuzione è dinamicamente piatta. Prova ad accentuare le note chiave.")
                else:
                    suggerimenti.append("Ottima escursione dinamica: tocco vivo e ricco di sfumature.")

                if mean_sx > mean_dx and mean_sx > 0.0:
                    suggerimenti.append("La mano sinistra copre la melodia. Riduci il volume dell'accompagnamento.")
                else:
                    suggerimenti.append("Buon equilibrio: la melodia (destra) risalta sull'accompagnamento.")

                if std_ioi < soglia_ioi:
                    suggerimenti.append("Il fraseggio è meccanico. Prova a rallentare leggermente a fine frase.")
                else:
                    suggerimenti.append("Ottima flessibilità ritmica e naturalezza nel fraseggio (rubato).")

                testo_completo = (
                    "FEEDBACK SULLA TUA ESECUZIONE:\n"
                    f"• Articolazione: {pct_leg:.0f}% note legate · {pct_sta:.0f}% staccate · {pct_med:.0f}% mediane\n"
                    f"• Dinamica: {suggerimenti[0]}\n"
                    f"• Bilanciamento: {suggerimenti[1]}\n"
                    f"• Fraseggio: {suggerimenti[2]}"
                )

                ax2.text(0.0, 0.95, testo_completo, transform=ax2.transAxes, fontsize=8,
                         verticalalignment='top', bbox=dict(boxstyle='round', facecolor='#eef2f5', edgecolor='#c5d0d8', alpha=0.9))
            else:
                ax = fig1.add_subplot(1, 1, 1)
                ax.axis('off')
                ax.text(0.5, 0.5, "Nessuna nota registrata nella modalità Fai Tu.", horizontalalignment='center', fontsize=12)

            pdf.savefig(fig1)
            page_number += 1
            fig1.savefig(os.path.join(session_dir, f"pagina_{page_number}.png"), dpi=120)
            plt.close(fig1)

            valid_sessions = [s for s in history_follow_sessions if len(s.get("user_notes", [])) > 0]

            for s_idx, session in enumerate(valid_sessions, 1):
                title_info = session["title_info"]
                ref_notes = session["reference_notes"]
                usr_notes = session["user_notes"]

                u_t0 = usr_notes[0]["start"]
                max_u_t = max((n["start"] - u_t0) for n in usr_notes)
                time_limit = max(max_u_t + 1.5, 5.0)

                # ═══ PAGE 1: CHROMAGRAMS ═══
                fig_crom = plt.figure(figsize=(8.5, 11))
                fig_crom.text(0.5, 0.958,
                              f"ANALISI COMPARATIVA #{s_idx} · MODALITÀ SEGUIMI",
                              ha='center', va='center', fontsize=13, fontweight='bold',
                              bbox=dict(boxstyle='round,pad=0.45', facecolor='#f8f9fa',
                                        edgecolor='#0288d1', linewidth=1.4))
                fig_crom.text(0.5, 0.932, f"{title_info['title']} — {title_info['artist']}",
                              ha='center', va='center', fontsize=10, color='#555')

                gs_crom = fig_crom.add_gridspec(2, 1, height_ratios=[1.0, 1.0],
                                                left=0.12, right=0.88, top=0.90,
                                                bottom=0.06, hspace=0.32)

                ax_target = fig_crom.add_subplot(gs_crom[0])
                ax_target.set_title("1. Target (Brano Originale - Osservatore)",
                                    fontsize=10, pad=6)
                if ref_notes:
                    for n in ref_notes:
                        if n["start"] <= time_limit:
                            dur = n.get("dur", 0.3)
                            color = get_note_color_rgb(n["note"], n["vel"])
                            rect = patches.Rectangle((n["start"], n["note"] - 0.4),
                                                     dur, 0.8, facecolor=color,
                                                     edgecolor=color, linewidth=0.1)
                            ax_target.add_patch(rect)
                ax_target.set_xlim(0, time_limit)
                ax_target.set_ylim(20, 109)
                ax_target.set_ylabel("Pitch MIDI", fontsize=8)
                ax_target.tick_params(axis='both', labelsize=8)
                ax_target.grid(True, linestyle='--', alpha=0.3)

                ax_user = fig_crom.add_subplot(gs_crom[1])
                ax_user.set_title("2. Tua Esecuzione (Modalità Seguimi)",
                                  fontsize=10, pad=6)
                for n in usr_notes:
                    t_rel = n["start"] - u_t0
                    dur = max(0.05, n.get("end", n["start"] + 0.3) - n["start"])
                    color = get_note_color_rgb(n["note"], n["vel"])
                    rect = patches.Rectangle((t_rel, n["note"] - 0.4),
                                             dur, 0.8, facecolor=color,
                                             edgecolor='black', linewidth=0.3)
                    ax_user.add_patch(rect)
                ax_user.set_xlim(0, time_limit)
                ax_user.set_ylim(20, 109)
                ax_user.set_xlabel("Tempo (secondi)", fontsize=8)
                ax_user.set_ylabel("Pitch MIDI", fontsize=8)
                ax_user.tick_params(axis='both', labelsize=8)
                ax_user.grid(True, linestyle='--', alpha=0.3)

                pdf.savefig(fig_crom)
                page_number += 1
                fig_crom.savefig(os.path.join(session_dir, f"pagina_{page_number}.png"), dpi=120)
                plt.close(fig_crom)

                # ═══ PAGE 2: RADAR + TEXT + GUIDE ═══
                ref_vel_mean = float(np.mean([n["vel"] for n in ref_notes])) if ref_notes else 0.5
                usr_vel_mean = float(np.mean([n["vel"] for n in usr_notes])) if usr_notes else 0.5
                diff_vel = usr_vel_mean - ref_vel_mean
                if diff_vel > 0.1:
                    commento_dinamica = "Tocco più marcato e incisivo rispetto all'originale."
                elif diff_vel < -0.1:
                    commento_dinamica = "Tocco più leggero e delicato rispetto all'originale."
                else:
                    commento_dinamica = "Dinamica e peso del tocco perfettamente in linea con il brano originale."

                ref_dur_mean = float(np.mean([n.get("dur", 0.3) for n in ref_notes])) if ref_notes else 0.3
                usr_dur_mean = float(np.mean([n["end"] - n["start"] for n in usr_notes])) if usr_notes else 0.3
                diff_dur = usr_dur_mean - ref_dur_mean
                if diff_dur < -0.1:
                    commento_articolazione = "Esecuzione tendente allo staccato/sgranato (note più brevi del riferimento)."
                elif diff_dur > 0.1:
                    commento_articolazione = "Esecuzione molto legata e sostenuta (tasti tenuti a lungo)."
                else:
                    commento_articolazione = "Articolazione (durata e fraseggio delle note) bilanciata ed in linea con l'originale."

                commento_tempo = "Ottima aderenza al ritmo e alla sequenza delle note guida." if len(usr_notes) > 5 else "Esecuzione parziale o con pause rilevanti."

                align = allinea_note(usr_notes, ref_notes, ref_window=max_u_t)
                if align["matched"] > 0:
                    med_vel_ratio = float(np.median(align["vel_ratios"]))
                    med_dur_ratio = float(np.median(align["dur_ratios"]))
                    mean_onset_err = float(np.mean(align["onset_errors"]))
                    pct_matched = 100.0 * align["matched"] / max(1, len(usr_notes))
                    if med_dur_ratio < 0.9:
                        commento_dur_allineamento = "più staccato dell'originale"
                    elif med_dur_ratio > 1.1:
                        commento_dur_allineamento = "più legato/sostenuto dell'originale"
                    else:
                        commento_dur_allineamento = "in linea con l'articolazione del target"
                    if med_vel_ratio < 0.9:
                        commento_vel_allineamento = "tocco più leggero del riferimento"
                    elif med_vel_ratio > 1.1:
                        commento_vel_allineamento = "tocco più forte e incisivo del riferimento"
                    else:
                        commento_vel_allineamento = "intensità in linea con il riferimento"
                    blocco_allineamento = (
                        f"• ALLINEAMENTO NOTA-PER-NOTA (tratto eseguito):\n"
                        f"  Riconosciute: {align['matched']} su {len(usr_notes)} suonate ({pct_matched:.0f}%) → note che coincidono con la guida.\n"
                        f"  Mancate: {align['missed']} → note della guida nel tratto eseguito che non hai suonato.\n"
                        f"  Extra: {align['extra']} → note suonate in più rispetto alla guida proposta.\n"
                        f"  Velocity relativa (mediana): {med_vel_ratio * 100:.0f}% del target → {commento_vel_allineamento}.\n"
                        f"  Durata relativa (mediana): {med_dur_ratio * 100:.0f}% → {commento_dur_allineamento}.\n"
                        f"  Errore medio di attacco: {mean_onset_err * 1000:.0f} ms → precisione temporale rispetto alla guida."
                    )
                else:
                    blocco_allineamento = (
                        "• ALLINEAMENTO NOTA-PER-NOTA (tratto eseguito):\n"
                        "  Nessuna nota appaiabile al riferimento (esecuzione molto distante dalla guida)."
                    )

                text_valutazione = (
                    f"DINAMICA (VOLUME E TOCCO):\n"
                    f"  Target: {ref_vel_mean:.2f} · Tuo: {usr_vel_mean:.2f}\n"
                    f"  → {commento_dinamica}\n\n"
                    f"ARTICOLAZIONE:\n"
                    f"  Target: {ref_dur_mean:.2f}s · Tua: {usr_dur_mean:.2f}s\n"
                    f"  → {commento_articolazione}\n\n"
                    f"TEMPO E RITMICA:\n"
                    f"  → {commento_tempo}\n\n"
                    f"{blocco_allineamento}"
                )

                fig_analisi = plt.figure(figsize=(8.5, 11))
                fig_analisi.text(0.5, 0.958,
                                 f"ANALISI COMPARATIVA #{s_idx} · MODALITÀ SEGUIMI",
                                 ha='center', va='center', fontsize=13, fontweight='bold',
                                 bbox=dict(boxstyle='round,pad=0.45', facecolor='#f8f9fa',
                                           edgecolor='#0288d1', linewidth=1.4))
                fig_analisi.text(0.5, 0.932, f"{title_info['title']} — {title_info['artist']}",
                                 ha='center', va='center', fontsize=10, color='#555')

                gs_a = fig_analisi.add_gridspec(4, 1,
                    height_ratios=[0.25, 1.1, 1.8, 0.85],
                    left=0.12, right=0.88, top=0.90, bottom=0.05, hspace=0.42)

                # Centered radar
                gs_radar_row = gs_a[1].subgridspec(1, 3, width_ratios=[0.3, 1, 0.3], wspace=0.05)
                ax_radar_cmp = fig_analisi.add_subplot(gs_radar_row[1], polar=True)

                ref_metrics = compute_radar_metrics(ref_notes, is_ref=True)
                usr_metrics_cmp = compute_radar_metrics(usr_notes)
                cats_cmp = ['Marcato\n(Peso)', 'Varietà\nDinamica', 'Articolazione\n(Legato)',
                            'Rubato\n(Flessibilità)', 'Bilanciamento\nMani']
                NC = len(cats_cmp)
                angs_cmp = [n / float(NC) * 2 * np.pi for n in range(NC)]
                angs_cmp += angs_cmp[:1]

                def disegna_sagoma(ax, valori, colore, label=None):
                    vals = list(valori) + list(valori[:1])
                    ax.plot(angs_cmp, vals, linewidth=1.8, color=colore, label=label)
                    ax.fill(angs_cmp, vals, color=colore, alpha=0.22)

                disegna_sagoma(ax_radar_cmp, ref_metrics, '#4a90e2', label='Target (Osservatore)')
                disegna_sagoma(ax_radar_cmp, usr_metrics_cmp, '#9b59b6', label='Tua Esecuzione (Seguimi)')
                ax_radar_cmp.set_xticks(angs_cmp[:-1])
                ax_radar_cmp.set_xticklabels(cats_cmp, fontsize=7.5)
                ax_radar_cmp.set_ylim(0, 1)
                ax_radar_cmp.set_title("Impronta Digitale dell'Espressività: Target vs Tua Esecuzione",
                                        fontsize=9.5, pad=14)
                ax_radar_cmp.legend(loc='lower center', bbox_to_anchor=(0.5, -0.36), ncol=1, fontsize=6.5)

                # Evaluation text (full width)
                ax_text = fig_analisi.add_subplot(gs_a[2])
                ax_text.axis('off')
                ax_text.text(0.0, 0.98, text_valutazione, transform=ax_text.transAxes,
                             fontsize=7.5, verticalalignment='top',
                             bbox=dict(boxstyle='round', facecolor='#eef2f5',
                                       edgecolor='#c5d0d8', alpha=0.9))

                # Fingerprint guide
                ax_guida = fig_analisi.add_subplot(gs_a[3])
                ax_guida.axis('off')
                testo_guida = (
                    "GUIDA ALL'IMPRONTA DIGITALE (scala 0-1):\n"
                    "• Marcato = intensità media del tocco · 0 = tocco leggerissimo · 1 = tocco molto pesante\n"
                    "• Varietà = escursione dinamica · 0 = dinamica piatta · 1 = massimo contrasto piano/forte\n"
                    "• Articolazione = durata note · 0 = staccato brevissimo · 1 = note lunghe e legate (≥ 0.5s)\n"
                    "• Rubato = flessibilità ritmica · 0 = tempo metronomico rigido · 1 = fraseggio molto libero\n"
                    "• Bilanciamento = melodia vs accomp. · 0 = destra assente · 0.5 = mani bilanciate · 1 = dominante\n"
                    "Blu = Target (Osservatore) · Viola = Tua Esecuzione (Seguimi)"
                )
                ax_guida.text(0.02, 0.95, testo_guida, transform=ax_guida.transAxes,
                              fontsize=7.5, verticalalignment='top',
                              bbox=dict(boxstyle='round', facecolor='#eef2f5',
                                        edgecolor='#c5d0d8', alpha=0.9))

                pdf.savefig(fig_analisi)
                page_number += 1
                fig_analisi.savefig(os.path.join(session_dir, f"pagina_{page_number}.png"), dpi=120)
                plt.close(fig_analisi)

        print(f"[REPORT OK] Generato correttamente in: {session_dir}")
        send_to_unity({"action": "report_ready", "file": pdf_filepath, "folder": session_dir, "pages": page_number})

    except Exception as e:
        print(f"[ERRORE PDF] {e}")

# ==========================================
# UDP LISTENER FOR COMMANDS FROM UNITY
# ==========================================
def udp_command_listener():
    """Listens for JSON commands from Unity on the UDP port and dispatches them to the right handler."""
    global is_following, is_playing_observing, is_recording_fai_tu, is_playing_example
    
    sock_recv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock_recv.bind((UDP_IP, PORT_FROM_UNITY))
    except OSError as e:
        print(f"[ERRORE FATALE] Porta {PORT_FROM_UNITY} gia' in uso: chiudi il bridge Python precedente e riavvia questo.")
        print(f"[DETTAGLIO] {e}")
        return
    print(f"[UDP LISTENER] Avviato ed in ascolto sulla porta {PORT_FROM_UNITY}...")
    # handshake: Unity waits for this signal before sending play_example, otherwise
    # the command would be sent while the port is not yet listening and would be lost
    send_to_unity({"action": "bridge_ready"})

    while True:
        try:
            data, addr = sock_recv.recvfrom(2048)
            text = data.decode('utf-8')
            cmd = json.loads(text)
            action = cmd.get("action")

            if action == "note_hand":
                nota = cmd.get("note")
                mano = cmd.get("hand")
                if nota is not None and mano in ("left", "right"):
                    fai_tu_nota_mano[int(nota)] = mano
                continue

            # Unity heartbeat: confirms the bridge is alive and ready, even if it started
            # before Unity (the initial bridge_ready would have been lost).
            # Always replies as long as the listener is active.
            if action == "ping":
                send_to_unity({"action": "bridge_ready"})
                continue

            if action == "play_song":
                song_id = cmd.get("id")
                filename = cmd.get("filename")
                if song_id is not None:
                    filename = get_filename_by_id(song_id)
                if not filename:
                    send_to_unity({
                        "action": "play_result",
                        "status": "error",
                        "message": "Brano non trovato nel database."
                    })
                    continue
                threading.Thread(target=play_observing_thread, args=(filename,), daemon=True).start()

            elif action == "play_song_follow":
                song_id = cmd.get("id")
                filename = cmd.get("filename")
                if song_id is not None:
                    filename = get_filename_by_id(song_id)
                if not filename:
                    send_to_unity({
                        "action": "play_result",
                        "status": "error",
                        "message": "Brano non trovato nel database."
                    })
                    continue
                is_following = True
                recorded_follow_user_notes.clear()
                active_pressed_follow.clear()
                
                load_follow_sequence(filename)
                
                new_session = {
                    "filename": filename,
                    "title_info": parse_song_info(filename),
                    "reference_notes": copy.deepcopy(current_song_reference_notes),
                    "user_notes": []
                }
                history_follow_sessions.append(new_session)
                
                send_next_follow_step()

            elif action == "list_songs":
                songs = get_song_list()
                send_to_unity({"action": "song_list", "count": len(songs), "songs": songs})
                print(f"[LISTA BRANI] Inviate {len(songs)} canzoni a Unity.")

            elif action == "next_step":
                send_next_follow_step()

            elif action == "stop_song":
                is_playing_observing = False
                is_following = False
                send_to_unity({"action": "clear_scene"})

            elif action == "play_example":
                sfida = cmd.get("sfida", 1)
                threading.Thread(target=play_tutorial_example, args=(sfida,), daemon=True).start()

            elif action == "stop_example":
                is_playing_example = False

            elif action == "start_fai_tu":
                is_recording_fai_tu = True
                recorded_notes_fai_tu.clear()
                active_pressed_notes.clear()
                t_first_note_fai_tu = None
                t_last_note_fai_tu = None
                print("[FAI TU] Registrazione avviata.")

            elif action == "stop_fai_tu":
                is_recording_fai_tu = False
                print("[FAI TU] Registrazione fermata.")

            elif action == "generate_report":
                is_recording_fai_tu = False
                generate_unified_report()

            elif action == "get_report_status":
                fai_tu_count = len(recorded_notes_fai_tu)
                sessions_with_notes = sum(1 for s in history_follow_sessions if s.get("user_notes"))
                print(f"[REPORT STATUS] fai_tu_notes={fai_tu_count} sessions_with_notes={sessions_with_notes}")
                send_to_unity({"action": "report_status", "has_material": has_report_material()})

            elif action == "search_song":
                query = cmd.get("query", "")
                threading.Thread(target=handle_song_request, args=(query,), daemon=True).start()

            elif action == "search_suggest":
                query = cmd.get("query", "")
                songs = suggest_songs(query)
                send_to_unity({"action": "song_list", "count": len(songs), "songs": songs, "suggest": True})
                print(f"[SUGGERIMENTI] Inviate {len(songs)} corrispondenze per '{query}'.")

        except Exception as e:
            print(f"[ERRORE UDP] {e}")

# ==========================================
# MAIN ENTRY POINT
# ==========================================
if __name__ == "__main__":
    print("[NORA BRIDGE v2 - esempi tutorial attivi]")
    scan_local_folder()

    t_midi = threading.Thread(target=midi_input_loop, daemon=True)
    t_udp = threading.Thread(target=udp_command_listener, daemon=True)

    t_midi.start()
    t_udp.start()

    print("[SYSTEM READY] Nora MIDI Bridge attivo.")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\nChiusura bridge.")