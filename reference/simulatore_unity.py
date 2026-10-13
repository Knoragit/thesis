import os
import json
import socket
import threading
import time
import random
import subprocess
import sys

# ==========================================
# CONFIGURAZIONE INDIRIZZI (porte opposte a quelle del bridge)
# ==========================================
UDP_IP = "127.0.0.1"
PORT_TO_PYTHON = 5006   # Porta di ascolto del bridge
PORT_FROM_PYTHON = 5005 # Porta dove il bridge trasmette i dati

# Percorso dello script bridge: il simulatore puo' avviarlo da solo, come fa Unity
# (vedi UdpReceiver.AvviaScriptPython), oppure usare un bridge gia' in esecuzione.
BRIDGE_SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "midi_bridge (1).py")

sock_send = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# ==========================================
# STATO DEL SIMULATORE (replica di Unity)
# ==========================================
stat_lock = threading.Lock()
bridge_pronto = False           # come UdpReceiver.bridgePronto
ping_non_risposti = 0           # 3 ping senza risposta -> bridge non pronto
stato = "menu"                  # menu | fai_tu | seguimi | osservatore | tutorial
note_attese = []                # note attese in modalita' Seguimi (da "expect_notes")
avviso_comando_prima_pronto = False

# Stato del viewer del report (replica di GameManager: pagineReport, paginaReportCorrente)
pagine_report = []              # percorsi PNG caricati dopo "report_ready"
pagina_report_corrente = 0
viewer_aperto = False
timer_report_attivo = True      # True finche' il report non e' arrivato (annulla il timeout)

# Simulazione tastiera
note_auto_attive = False
conteggio_note = 0
ultimo_riepilogo = 0.0

processo_bridge = None          # processo del bridge avviato dal simulatore


def stampa_menu():
    """Stampa il pannello di controllo: i numeri replicano i comandi che Unity invia al bridge."""
    with stat_lock:
        auto = note_auto_attive
        viewer = viewer_aperto
    print("=== PANNELLO DI CONTROLLO SIMULATORE UNITY ===")
    print(" 1: Avvia 'Fai Tu' (start_fai_tu)")
    print(" 2: Ferma 'Fai Tu' (stop_fai_tu)")
    print(" 3: Inietta una nota (come se la suonassi sulla tastiera)")
    print(" 4: Genera Report (generate_report)")
    print(" 5: Chiudi report / torna al menu")
    print(" 6: Suona Brano 'Osservatore' (play_song)")
    print(" 7: Suona Brano 'Seguimi' (play_song_follow)")
    print(" 8: Prossimo step Seguimi (next_step)")
    print(" 9: Ferma Brani (stop_song)")
    print("10: Esempio tutorial (play_example sfida 1-4)")
    print("11: Ferma esempio tutorial (stop_example)")
    print("12: CERCA E SCARICA CANZONE (search_song)")
    print("13: SUGGERIMENTI LIVE (search_suggest)")
    print("14: ELENCA BRANI DISPONIBILI (list_songs)")
    print("15: Apri pagina corrente del report (PNG)")
    print("16: Note automatiche: %s" % ("ON" if auto else "OFF"))
    print("17: Richiedi stato report (get_report_status)")
    print(" 0: Esci")
    if viewer:
        print("   [viewer report] a=avanti i=indietro o=apri PNG c=chiudi")


def chiedi_int(prompt):
    """Legge un numero intero dall'utente (senza crashare su input non numerici)."""
    while True:
        try:
            return int(input(prompt))
        except ValueError:
            print("Inserisci un numero valido.")


def invia_a_python(dizionario):
    """Invia un comando JSON al bridge sulla porta 5006 (come UdpReceiver.InviaJsonAPython).
    Prima del segnale bridge_ready Unity continua a inviare ma avverte che i comandi verranno persi."""
    global avviso_comando_prima_pronto
    action = dizionario.get("action")
    if not bridge_pronto and action != "ping":
        if not avviso_comando_prima_pronto:
            avviso_comando_prima_pronto = True
            print(f"[SIMULATORE] Comando inviato prima che il ponte fosse pronto: verra' perso. {json.dumps(dizionario)}")
    try:
        sock_send.sendto(json.dumps(dizionario).encode('utf-8'), (UDP_IP, PORT_TO_PYTHON))
    except Exception as e:
        print(f"[SIMULATORE] Errore invio UDP: {e}")


def heartbeat_bridge():
    """Replica HeartbeatBridge di Unity: ping ogni 1s finche' il bridge non e' pronto,
    poi ogni 5s come keep-alive. Dopo 3 ping non risposti di fila il bridge torna non pronto."""
    global bridge_pronto, ping_non_risposti
    while True:
        invia_a_python({"action": "ping"})
        with stat_lock:
            pronto = bridge_pronto
        time.sleep(1.0 if not pronto else 5.0)
        if pronto:
            perso = False
            with stat_lock:
                ping_non_risposti += 1
                if ping_non_risposti >= 3:
                    bridge_pronto = False
                    ping_non_risposti = 0
                    perso = True
            if perso:
                print("[SIMULATORE] Ponte non risponde: i comandi inviati ora verranno persi.")


def ricevi_nota(note, velocity, action):
    """Replica la pipeline Unity: nota -> colonne visualizzatore (PianoVisualizer) + valutazione
    (ValutaNota). Le note arrivano dal bridge (tastiera MIDI reale, brani in Osservatore o
    esempi tutorial); il simulatore puo' anche generarne di sintetiche con 'inietta nota' per
    provare la logica di visualizzazione e il matching di Seguimi senza tastiera collegata."""
    global conteggio_note, ultimo_riepilogo
    with stat_lock:
        attese = list(note_attese)
    if action == "press":
        conteggio_note += 1
        if attese and note in attese:
            print(f"[SEGUIMI] NOTA ATTESA SUONATA! nota={note} velocity={velocity:.2f} (attese: {attese})")
        else:
            print(f"[NOTA] press nota={note} velocity={velocity:.2f}")
    else:
        print(f"[NOTA] release nota={note}")
    now = time.time()
    if now - ultimo_riepilogo >= 2.0:
        print(f"  -> {conteggio_note} eventi nota negli ultimi {now - ultimo_riepilogo:.0f}s")
        conteggio_note = 0
        ultimo_riepilogo = now


def inietta_nota():
    """Simula una pressione di tasto (press) seguita, dopo un breve ritardo, dal rilascio (release).
    In modalita' Seguimi sceglie spesso una delle note attese cosi' si puo' vedere il matching."""
    with stat_lock:
        attese = list(note_attese)
        in_seguimi = (stato == "seguimi")
    if in_seguimi and attese and random.random() < 0.75:
        nota = random.choice(attese)
    else:
        nota = random.randint(21, 108)
    vel = round(random.uniform(0.2, 1.0), 2)
    ricevi_nota(nota, vel, "press")

    def _rilascia():
        time.sleep(random.uniform(0.2, 0.6))
        ricevi_nota(nota, vel, "release")

    threading.Thread(target=_rilascia, daemon=True).start()


def thread_note_auto():
    """Genera note in automatico ogni ~1.5s quando la modalita' 'note automatiche' e' attiva."""
    while True:
        time.sleep(1.5)
        with stat_lock:
            attive = note_auto_attive
        if attive:
            inietta_nota()


def aggiorna_viewer():
    """Replica AggiornaPaginaReport: mostra la pagina corrente e nasconde Indietro/Avanti agli estremi."""
    with stat_lock:
        indice = pagina_report_corrente
        totale = len(pagine_report)
    if totale == 0:
        return
    indietro = "" if indice > 0 else " [Indietro nascosto]"
    avanti = "" if indice < totale - 1 else " [Avanti nascosto]"
    print(f"\n[VIEWER REPORT] Pagina {indice + 1}/{totale}{indietro}{avanti}")


def avanti_pagina():
    """Replica AvantiPaginaReport: avanza se non si e' sull'ultima pagina."""
    global pagina_report_corrente
    with stat_lock:
        if pagina_report_corrente < len(pagine_report) - 1:
            pagina_report_corrente += 1
    aggiorna_viewer()


def indietro_pagina():
    """Replica IndietroPaginaReport: torna indietro se non si e' sulla prima pagina."""
    global pagina_report_corrente
    with stat_lock:
        if pagina_report_corrente > 0:
            pagina_report_corrente -= 1
    aggiorna_viewer()


def apri_pagina_corrente():
    """Apre la pagina PNG corrente con il visualizzatore di sistema (come mostrare il report a Unity)."""
    with stat_lock:
        pagine = list(pagine_report)
        indice = pagina_report_corrente
    if not pagine:
        print("Nessuna pagina disponibile: genera prima un report (comando 4).")
        return
    percorso = pagine[indice]
    print(f"[VIEWER REPORT] Apro la pagina {indice + 1}: {percorso}")
    try:
        os.startfile(percorso)
    except Exception as e:
        print(f"[VIEWER REPORT] Impossibile aprire l'immagine: {e}")


def chiudi_viewer():
    """Replica ChiudiViewerReport: chiude il viewer, mostra il messaggio di conferma
    e dopo 3.5s ritorna automaticamente al menu."""
    global viewer_aperto
    with stat_lock:
        viewer_aperto = False
        pagine_report.clear()
    messaggio_poi_menu("Lo trovi sulla cartella Sessioni insieme alla registrazione della tua esecuzione.")


def messaggio_poi_menu(testo):
    """Mostra un banner stile Unity per 3.5s, poi torna automaticamente al menu."""
    print(f"\n[BANNER] {testo}")

    def _torna():
        time.sleep(3.5)
        print("[SIMULATORE] Ritorno al menu.")

    threading.Thread(target=_torna, daemon=True).start()


def avvia_generazione_report():
    """Replica GeneraReportPDF + MostraInCorsoPoiTimeout: invia il comando, mostra il
    messaggio di attesa e, se dopo 120s il report non e' arrivato, avvisa e torna al menu."""
    global timer_report_attivo
    with stat_lock:
        timer_report_attivo = True
    invia_a_python({"action": "generate_report"})
    print("\nGenerazione report in corso...")

    def _timeout():
        time.sleep(120)
        with stat_lock:
            attivo = timer_report_attivo
        if attivo:
            messaggio_poi_menu("Report non disponibile. Riprova.")

    threading.Thread(target=_timeout, daemon=True).start()


def gestisci_report_ready(msg):
    """Replica ReportPronto + CaricaPagineReport: carica le pagine PNG dalla cartella
    sessione e, se presenti, apre il viewer sulla prima pagina (navigazione simmetrica)."""
    global timer_report_attivo, viewer_aperto, pagina_report_corrente
    cartella = msg.get("folder") or ""
    pagine = int(msg.get("pages") or 0)
    with stat_lock:
        timer_report_attivo = False
    print(f"\n[REPORT PDF PRONTO] File: {msg.get('file')} - pagine: {pagine}")
    pagine_report.clear()
    for i in range(1, pagine + 1):
        percorso = os.path.join(cartella, f"pagina_{i}.png")
        if os.path.exists(percorso):
            pagine_report.append(percorso)
    if not pagine_report:
        messaggio_poi_menu("Report non trovato.\nCerca Report_Completo_Nora.pdf nella cartella Sessione...")
        return
    with stat_lock:
        viewer_aperto = True
        pagina_report_corrente = 0
    aggiorna_viewer()


def gestisci_comando(cmd):
    """Esegue il comando scelto; ritorna False per uscire dal programma."""
    global stato, note_auto_attive
    with stat_lock:
        viewer = viewer_aperto
    if cmd == "0":
        return False
    if viewer:
        if cmd == "a":
            avanti_pagina()
            return True
        if cmd == "i":
            indietro_pagina()
            return True
        if cmd == "o":
            apri_pagina_corrente()
            return True
        if cmd == "c":
            chiudi_viewer()
            return True
    if cmd == "1":
        with stat_lock:
            stato = "fai_tu"
        invia_a_python({"action": "start_fai_tu", "filename": "Improvvisazione_Libera"})
        print("[FAI TU] Registrazione avviata. Suona la tastiera (o usa '3' per note simulate).")
    elif cmd == "2":
        invia_a_python({"action": "stop_fai_tu"})
        print("[FAI TU] Registrazione fermata.")
    elif cmd == "3":
        inietta_nota()
    elif cmd == "4":
        avvia_generazione_report()
    elif cmd == "5":
        chiudi_viewer()
    elif cmd == "6":
        brano_id = chiedi_int("ID del brano (usa 14 per vedere la lista): ")
        invia_a_python({"action": "stop_song"})
        time.sleep(0.1)
        invia_a_python({"action": "play_song", "id": brano_id})
        with stat_lock:
            stato = "osservatore"
    elif cmd == "7":
        brano_id = chiedi_int("ID del brano (usa 14 per vedere la lista): ")
        invia_a_python({"action": "stop_song"})
        time.sleep(0.1)
        invia_a_python({"action": "play_song_follow", "id": brano_id})
        with stat_lock:
            stato = "seguimi"
    elif cmd == "8":
        invia_a_python({"action": "next_step"})
    elif cmd == "9":
        invia_a_python({"action": "stop_song"})
        with stat_lock:
            stato = "menu"
    elif cmd == "10":
        sfida = chiedi_int("Numero sfida tutorial (1-4): ")
        invia_a_python({"action": "play_example", "sfida": sfida})
        with stat_lock:
            stato = "tutorial"
    elif cmd == "11":
        invia_a_python({"action": "stop_example"})
        with stat_lock:
            stato = "menu"
    elif cmd == "12":
        query = input("Digita il titolo o l'artista della canzone da cercare: ")
        invia_a_python({"action": "search_song", "query": query})
    elif cmd == "13":
        query = input("Digita il testo per i suggerimenti live: ")
        invia_a_python({"action": "search_suggest", "query": query})
    elif cmd == "14":
        invia_a_python({"action": "list_songs"})
    elif cmd == "15":
        apri_pagina_corrente()
    elif cmd == "16":
        with stat_lock:
            note_auto_attive = not note_auto_attive
            nuovo = note_auto_attive
        print(f"[SIMULATORE] Note automatiche: {'ON' if nuovo else 'OFF'}")
    elif cmd == "17":
        invia_a_python({"action": "get_report_status"})
    else:
        print("Comando non valido.")
    return True


def ascolta_backend():
    """Replica ReceiveData + Update di UdpReceiver: riceve i messaggi JSON dal bridge
    e li smista con lo stesso comportamento di Unity."""
    global bridge_pronto, ping_non_risposti
    sock_recv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock_recv.bind((UDP_IP, PORT_FROM_PYTHON))
    except OSError as e:
        print(f"[ERRORE FATALE] Porta {PORT_FROM_PYTHON} gia' in uso (forse il gioco Unity o un altro simulatore e' attivo): {e}")
        os._exit(1)
    print(f"[SIMULATORE UNITY] In ascolto sulla porta {PORT_FROM_PYTHON} ...")

    while True:
        try:
            data, _ = sock_recv.recvfrom(4096)
            msg = json.loads(data.decode('utf-8'))
            action = msg.get("action")
            if action == "bridge_ready":
                segnato = False
                with stat_lock:
                    if not bridge_pronto:
                        segnato = True
                    bridge_pronto = True
                    ping_non_risposti = 0
                if segnato:
                    print("[BRIDGE] Ponte pronto: i comandi vengono ricevuti.")
            elif action == "song_list":
                songs = msg.get("songs", [])
                etichetta = "SUGGERIMENTI" if msg.get("suggest") else f"BRANI DISPONIBILI ({len(songs)})"
                print(f"\n=== {etichetta} ===")
                for s in songs:
                    print(f"  [{s['id']}] {s.get('title', '?')} - {s.get('artist', '?')}  ({s['filename']})")
            elif action == "search_result":
                if msg.get("status") == "success":
                    print(f"\n[RICERCA OK] '{msg.get('title')}' - {msg.get('artist')}  ({msg.get('filename')})")
                else:
                    print(f"\n[RICERCA FALLITA] {msg.get('message')}")
            elif action == "play_result":
                if msg.get("status") == "error":
                    print(f"\n[RIPRODUZIONE ERRORE] {msg.get('message')}")
                else:
                    print(f"\n[RIPRODUZIONE OK] {msg.get('message')}")
            elif action == "expect_notes":
                with stat_lock:
                    note_attese.clear()
                    note_attese.extend(msg.get("notes") or [])
                    attese = list(note_attese)
                print(f"\n[SEGUIMI] Prossime note attese: {sorted(attese)}")
            elif action == "clear_scene":
                with stat_lock:
                    note_attese.clear()
                print("[SCENA] Visualizzatore azzerato (clear_scene).")
            elif action == "example_done":
                print("[TUTORIAL] Esempio completato (example_done).")
            elif action == "report_ready":
                gestisci_report_ready(msg)
            elif action == "report_status":
                stato_report = "DISPONIBILE" if msg.get("has_material") else "SPENTO (nessun materiale)"
                print(f"\n[STATO REPORT] Materiale per il report: {stato_report}")
            elif action in ("press", "release"):
                ricevi_nota(msg.get("note", 0), msg.get("velocity", 0.0), action)
            else:
                print(f"\n[RICEVUTO DA PYTHON] {msg}")
        except json.JSONDecodeError:
            print("[SIMULATORE] Ricevuto un messaggio non JSON.")
        except Exception as e:
            print(f"[ERRORE RICEZIONE] {e}")


def avvia_bridge_python():
    """Replica AvviaScriptPython di UdpReceiver: apre 'midi_bridge (1).py'
    in una nuova finestra console."""
    global processo_bridge
    if not os.path.exists(BRIDGE_SCRIPT):
        print(f"[SIMULATORE] Attenzione: script bridge non trovato: {BRIDGE_SCRIPT}")
        return
    try:
        CREATE_NEW_CONSOLE = 0x00000010
        processo_bridge = subprocess.Popen(
            [sys.executable, BRIDGE_SCRIPT],
            creationflags=CREATE_NEW_CONSOLE
        )
        print(f"[SIMULATORE] Bridge avviato in una nuova finestra: {BRIDGE_SCRIPT}")
    except Exception as e:
        print(f"[SIMULATORE] Impossibile avviare Python: {e}")


def ferma_bridge():
    """Replica TerminaScriptPython: ferma i brani e chiude il processo del bridge
    avviato dal simulatore (taskkill /T /F evita processi orfani)."""
    global processo_bridge
    try:
        invia_a_python({"action": "stop_song"})
    except Exception:
        pass
    if processo_bridge is not None:
        try:
            if processo_bridge.poll() is None:
                subprocess.run(
                    ["taskkill", "/PID", str(processo_bridge.pid), "/T", "/F"],
                    creationflags=subprocess.CREATE_NO_WINDOW
                )
        except Exception as e:
            print(f"[SIMULATORE] Errore chiusura bridge: {e}")
        processo_bridge = None


if __name__ == "__main__":
    print("=== PANNELLO DI CONTROLLO SIMULATORE UNITY ===")
    scelta = input("Avvio anche il bridge Python? (s=Si / n=No, e' gia' in esecuzione): ").strip().lower()
    if scelta.startswith("s"):
        avvia_bridge_python()

    threading.Thread(target=ascolta_backend, daemon=True).start()
    threading.Thread(target=heartbeat_bridge, daemon=True).start()
    threading.Thread(target=thread_note_auto, daemon=True).start()

    stampa_menu()
    try:
        while True:
            try:
                cmd = input("\n> Seleziona comando: ").strip().lower()
            except (EOFError, KeyboardInterrupt):
                print()
                break
            if not gestisci_comando(cmd):
                break
    finally:
        ferma_bridge()
        print("\nChiusura simulatore.")