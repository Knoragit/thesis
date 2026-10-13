using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics; // Necessario per avviare/chiudere processi esterni

public class UdpReceiver : MonoBehaviour
{
    public PianoVisualizer visualizer;
    private GameManager gameManager;
    private SongListManager songListManager;
    private Queue<string> messageQueue = new Queue<string>();
    private Queue<string> debugMessages = new Queue<string>();
    private Thread receiveThread;
    private UdpClient client;
    public int port = 5005;
    private bool isRunning = true;

    [Header("Configurazione Python")]
    [Tooltip("Nome del comando python o percorso dell'eseguibile (es. python o python3)")]
    public string pythonPath = "python";
    [Tooltip("Percorso assoluto o relativo dello script python (es. C:/Users/vrlab/Desktop/Nora/midi_bridge (1).py)")]
    public string scriptPath = "C:/Users/vrlab/Desktop/Nora/midi_bridge (1).py";

    private Process pythonProcess;

    // True quando il bridge ha confermato di essere in ascolto ("bridge_ready").
    // Prima di questo segnale i comandi inviati vengono scartati dal sistema:
    // per questo il tutorial aspetta la tastiera prima di chiedere l'esempio.
    private bool bridgePronto = false;
    private int pingNonRisposti = 0;
    private bool avvisoComandoPrimaProntoInviato = false;

    public bool BridgePronto => bridgePronto;

    // Emesso SOLO quando il bridge passa da non-pronto a pronto (il bridge risponde
    // "bridge_ready" anche a ogni ping, quindi senza il guard sulla transizione
    // l'evento si ripeterebbe in continuazione). Chi è interessato (GameManager)
    // ne usa la ricezione per rinnovare comandi che sono caduti prima della prontezza.
    public event System.Action BridgeProntoRicevuto;

    void Awake()
    {
        AvviaScriptPython();
    }

    void Start()
    {
        gameManager = FindFirstObjectByType<GameManager>();
        songListManager = FindFirstObjectByType<SongListManager>();
        if (songListManager == null && gameObject.GetComponent<SongListManager>() == null)
        {
            songListManager = gameObject.AddComponent<SongListManager>();
        }

        isRunning = true;
        receiveThread = new Thread(new ThreadStart(ReceiveData))
        {
            IsBackground = true
        };
        receiveThread.Start();

        StartCoroutine(HeartbeatBridge());
    }

    // Heartbeat: finche' il bridge non si dichiara pronto si interroga ogni secondo
    // (il primo avvio richiede che finisca la scansione dei brani e l'init MIDI),
    // poi ogni 5s come keep-alive. Se smette di rispondere, BridgePronto torna
    // false e i comandi non vengono piu' dati per eseguiti.
    private IEnumerator HeartbeatBridge()
    {
        while (true)
        {
            InviaPing();
            yield return new WaitForSeconds(bridgePronto ? 5f : 1f);

            // Nessun "bridge_ready" nell'intervallo: il bridge non sta rispondendo.
            if (bridgePronto)
            {
                pingNonRisposti++;
                if (pingNonRisposti >= 3)
                {
                    bridgePronto = false;
                    pingNonRisposti = 0;
                    UnityEngine.Debug.LogWarning("[BRIDGE] Ponte non risponde: il bridge e' stato chiuso o non e' in ascolto. I comandi inviati ora verranno persi.");
                }
            }
        }
    }

    private void RiceviBridgeReady()
    {
        if (!bridgePronto)
        {
            UnityEngine.Debug.Log("<color=green>[BRIDGE]</color> Ponte pronto: tastiera collegata, i comandi vengono ricevuti.");
            if (BridgeProntoRicevuto != null) BridgeProntoRicevuto();
        }
        bridgePronto = true;
        pingNonRisposti = 0;
        avvisoComandoPrimaProntoInviato = false;
    }

    private void AvviaScriptPython()
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = true, // Lascia a true per vedere la finestra del terminale con i log
                CreateNoWindow = false
            };

            pythonProcess = Process.Start(startInfo);
            UnityEngine.Debug.Log("<color=cyan>[UDP]</color> Script Python avviato automaticamente.");
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"[UDP ERRORE] Impossibile avviare Python: {e.Message}");
        }
    }

    private void ReceiveData()
    {
        // Retry del bind: il socket di un play precedente a volte impiega
        // qualche secondo a liberarsi (finalizzatore/O.S.).
        for (int tentativo = 0; tentativo < 20; tentativo++)
        {
            bool ok = ApriSocket();
            if (ok) break;

            // Se dopo 10 secondi la porta è ancora occupata, continuiamo a
            // riprovare in background finché non si libera (es. socket orfani
            // rimasti dall'editor), invece di rinunciare del tutto.
            if (tentativo == 19 && isRunning)
            {
                AddDebugMessage("[UDP] Porta occupata: riprovo in background ogni secondo...");
                while (isRunning)
                {
                    Thread.Sleep(1000);
                    if (ApriSocket()) break;
                }
            }
            else
            {
                try { if (client != null) client.Close(); } catch { }
                client = null;
                Thread.Sleep(500);
            }
        }

        if (client == null) return;

        while (isRunning)
        {
            try
            {
                IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = client.Receive(ref anyIP);
                string text = Encoding.UTF8.GetString(data);
                lock (messageQueue)
                {
                    messageQueue.Enqueue(text);
                }
            }
            catch (SocketException)
            {
                if (isRunning) AddDebugMessage("[UDP] Porta UDP chiusa.");
                break;
            }
            catch (System.Exception e)
            {
                AddDebugMessage("[UDP ERRORE RICEZIONE]: " + e.Message);
                break;
            }
        }
    }

    private bool ApriSocket()
    {
        try
        {
            Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.ExclusiveAddressUse = false;
            sock.Bind(new IPEndPoint(IPAddress.Any, port));

            UdpClient nuovo = new UdpClient();
            nuovo.Client = sock;
            client = nuovo;
            return true;
        }
        catch (System.Exception e)
        {
            if (client != null) { try { client.Close(); } catch { } client = null; }
            AddDebugMessage($"[UDP] Bind porta {port} non riuscito (ritento): {e.Message}");
            return false;
        }
    }

    // I messaggi di debug generati dal thread di ricezione vengono
    // stampati sul main thread (in Update) per evitare chiamate Unity API off-main.
    private void AddDebugMessage(string msg)
    {
        lock (debugMessages)
        {
            debugMessages.Enqueue(msg);
        }
    }

    void Update()
    {
        while (true)
        {
            string dbg = null;
            lock (debugMessages)
            {
                if (debugMessages.Count > 0) dbg = debugMessages.Dequeue();
            }
            if (dbg == null) break;
            if (dbg.StartsWith("[UDP ERRORE]"))
                UnityEngine.Debug.LogError(dbg);
            else
                UnityEngine.Debug.Log(dbg);
        }

        while (messageQueue.Count > 0)
        {
            string text = null;
            lock (messageQueue)
            {
                if (messageQueue.Count > 0)
                    text = messageQueue.Dequeue();
            }

            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    // Per i messaggi del bridge (JSON valido con doppi apici) il Replace
                    // con apici singoli è inutile e dannoso (rompe titoli con apostrofo),
                    // quindi proviamo prima il parsing diretto e solo in fallback il Replace.
                    MidiData json = null;
                    json = JsonUtility.FromJson<MidiData>(text);
                    if (json == null || string.IsNullOrEmpty(json.action))
                    {
                        json = JsonUtility.FromJson<MidiData>(text.Replace("'", "\""));
                    }

                    if (json != null)
                    {
                        if (json.action == "report_ready")
                        {
                            UnityEngine.Debug.Log($"<color=green>[REPORT PDF PRONTO]</color> File creato: {json.file}, pagine: {json.pages}");
                            if (gameManager != null)
                                gameManager.ReportPronto(json.folder, json.pages);
                        }
                        else if (json.action == "expect_notes")
                        {
                            if (gameManager != null) gameManager.ImpostaNoteAttese(json.notes);
                        }
                        else if (json.action == "clear_scene")
                        {
                            // Il GameManager decide: durante l'esempio del tutorial il
                            // reset e' differito, altrimenti la colonna dell'esempio
                            // verrebbe distrutta nello stesso frame in cui nasce.
                            if (gameManager != null)
                            {
                                gameManager.NotificaClearScene();
                            }
                            else if (visualizer != null)
                            {
                                visualizer.ResetVisualizer();
                                visualizer.PulisciNoteAtteseVisive();
                            }
                        }
                        else if (json.action == "song_list")
                        {
                            if (songListManager != null)
                            {
                                songListManager.SongListRicevuta(json.songs, json.suggest);
                            }
                        }
                        else if (json.action == "search_result")
                        {
                            if (songListManager != null)
                            {
                                songListManager.SearchResultRicevuta(json.status, json.filename, json.title, json.artist, json.message);
                            }
                        }
                        else if (json.action == "play_result")
                        {
                            if (songListManager != null)
                            {
                                songListManager.PlayResultRicevuta(json.status, json.message);
                            }
                        }
                        else if (json.action == "example_done")
                        {
                            if (gameManager != null) gameManager.EsempioCompletato();
                        }
                        else if (json.action == "bridge_ready")
                        {
                            // PRIMA del fallback "press/release": questo messaggio non
                            // e' una nota e non deve generare nessuna colonna.
                            RiceviBridgeReady();
                        }
                        else if (json.action == "report_status")
                        {
                            // Risposta a "get_report_status": abilita o spegne il
                            // bottone "Genera Report" del menu a seconda del materiale.
                            if (gameManager != null) gameManager.StatoReportRicevuto(json.has_material);
                        }
                        else
                        {
                            // Qui entreranno solo "press" e "release"
                            if (visualizer != null) visualizer.OnNoteReceived(json.note, json.velocity, json.action);
                            if (gameManager != null) gameManager.ValutaNota(json.note, json.velocity, json.action);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError("Errore parsing JSON: " + e.Message);
                }
            }
        }
    }

    public void InviaComandoListaSongs()
    {
        string json = "{\"action\":\"list_songs\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoSuggerimento(string query)
    {
        string json = "{\"action\":\"search_suggest\", \"query\":" + QueryComeStringaJson(query) + "}";
        InviaJsonAPython(json);
    }

    // Esempio dimostrativo del tutorial: il bridge riproduce la sfida indicata
    // (suono dal pianoforte + colonne) e poi invia "example_done".
    public void InviaComandoEsempio(int sfida)
    {
        string json = "{\"action\":\"play_example\", \"sfida\":" + sfida + "}";
        InviaJsonAPython(json);
    }

    public void InviaComandoStopEsempio()
    {
        string json = "{\"action\":\"stop_example\"}";
        InviaJsonAPython(json);
    }

    // Heartbeat verso il bridge: chiede solo se e' vivo e in ascolto.
    public void InviaPing()
    {
        string json = "{\"action\":\"ping\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoRicerca(string query)
    {
        string json = "{\"action\":\"search_song\", \"query\":" + QueryComeStringaJson(query) + "}";
        InviaJsonAPython(json);
    }

    // La query deve arrivare a Python come stringa JSON ("query":"testo"), non come
    // oggetto {"value":"testo"}: con il payload precedente python riceveva un dict e
    // la ricerca non rispondeva mai (AttributeError su query.strip()).
    private static string QueryComeStringaJson(string query)
    {
        string q = (query ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + q + "\"";
    }

    public void InviaComandoRiproduzione(string nomeFileMidi, bool isSeguimi = false)
    {
        // 1. Prima di tutto blocchiamo la riproduzione precedente
        InviaComandoStop();

        // 2. Puliamo subito il visualizzatore in Unity per evitare sovrapposizioni di vecchie colonne
        PianoVisualizer vis = FindFirstObjectByType<PianoVisualizer>();
        if (vis != null)
        {
            vis.ResetVisualizer();
            vis.PulisciNoteAtteseVisive();
        }

        // 3. Mandiamo il comando al nuovo brano
        string azione = isSeguimi ? "play_song_follow" : "play_song";
        string json = "{\"action\":\"" + azione + "\", \"filename\":\"" + nomeFileMidi + "\"}";
        InviaJsonAPython(json);
        UnityEngine.Debug.Log($"[UDP] Richiesto brano ({azione}): {nomeFileMidi}");
    }

    public void InviaComandoRiproduzioneConId(int songId, bool isSeguimi = false)
    {
        InviaComandoStop();

        PianoVisualizer vis = FindFirstObjectByType<PianoVisualizer>();
        if (vis != null)
        {
            vis.ResetVisualizer();
            vis.PulisciNoteAtteseVisive();
        }

        string azione = isSeguimi ? "play_song_follow" : "play_song";
        string json = "{\"action\":\"" + azione + "\", \"id\":" + songId + "}";
        InviaJsonAPython(json);
        UnityEngine.Debug.Log($"[UDP] Richiesto brano ({azione}) con id: {songId}");
    }

    public void InviaComandoStartFaiTu(string nomePerformance = "Improvvisazione_Libera")
    {
        string json = "{\"action\":\"start_fai_tu\", \"filename\":\"" + nomePerformance + "\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoStopFaiTu()
    {
        string json = "{\"action\":\"stop_fai_tu\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoGeneraReport()
    {
        string json = "{\"action\":\"generate_report\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoRichiediStatoReport()
    {
        string json = "{\"action\":\"get_report_status\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoSeguimiAvanti()
    {
        string json = "{\"action\":\"next_step\"}";
        InviaJsonAPython(json);
    }

    public void InviaNotaDaFaiTu(int nota, bool manoSinistra)
    {
        string mano = manoSinistra ? "left" : "right";
        string json = "{\"action\":\"note_hand\",\"note\":" + nota + ",\"hand\":\"" + mano + "\"}";
        InviaJsonAPython(json);
    }

    public void InviaComandoStop()
    {
        string json = "{\"action\":\"stop_song\"}";
        InviaJsonAPython(json);
    }

    private void InviaJsonAPython(string jsonComando)
    {
        try
        {
            // Diagnostica: un comando spedito prima che il bridge sia in ascolto
            // viene scartato dal sistema senza alcun errore. Lo segniamo una volta
            // sola per non ripetere il messaggio a ogni ping.
            if (!bridgePronto && !jsonComando.Contains("\"ping\"") && !avvisoComandoPrimaProntoInviato)
            {
                avvisoComandoPrimaProntoInviato = true;
                UnityEngine.Debug.LogWarning("[BRIDGE] Comando inviato prima che il ponte fosse pronto: verra' perso. " + jsonComando);
            }

            using (UdpClient sendClient = new UdpClient())
            {
                byte[] data = Encoding.UTF8.GetBytes(jsonComando);
                sendClient.Send(data, data.Length, "127.0.0.1", 5006);
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError("[UDP ERRORE INVIO]: " + e.Message);
        }
    }

    private bool terminata = false;

    private void TerminaScriptPython()
    {
        if (terminata) return;
        terminata = true;
        isRunning = false;

        try { InviaComandoStop(); } catch { }

        // 1. Chiudi SEMPRE il socket di ricezione: sblocca Receive() e libera la porta UDP.
        try
        {
            if (client != null)
            {
                client.Close();
                client = null;
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogWarning($"[UDP] Chiusura socket: {e.Message}");
        }

        // Forza l'eventuale finalizzatore del socket: libera la porta UDP
        // subito, senza lasciare porte "orfane" che bloccherebbero il Play successivo.
        try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }

        // 2. Attendi brevemente la fine del thread di ricezione.
        try
        {
            if (receiveThread != null)
            {
                receiveThread.Join(500);
                receiveThread = null;
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogWarning($"[UDP] Join thread: {e.Message}");
        }

        // 3. Termina il processo Python e tutto il suo albero (evita processi orfani).
        if (pythonProcess != null)
        {
            try
            {
                if (!pythonProcess.HasExited)
                {
                    try
                    {
                        using (Process killer = new Process())
                        {
                            killer.StartInfo.FileName = "taskkill";
                            killer.StartInfo.Arguments = $"/PID {pythonProcess.Id} /T /F";
                            killer.StartInfo.CreateNoWindow = true;
                            killer.StartInfo.UseShellExecute = false;
                            killer.Start();
                            killer.WaitForExit(2000);
                        }
                    }
                    catch { }
                    try { pythonProcess.Kill(); } catch { }
                }
                pythonProcess.Dispose();
            }
            catch { }
            pythonProcess = null;
            UnityEngine.Debug.Log("<color=cyan>[UDP]</color> Processo Python terminato.");
        }
    }

    void OnApplicationQuit()
    {
        TerminaScriptPython();
    }

    // Rete di sicurezza: OnDestroy viene chiamato anche su exit bruschi del Play
    // e su domain reload, quando OnApplicationQuit potrebbe non scattare.
    void OnDestroy()
    {
        TerminaScriptPython();
    }

    [System.Serializable]
    public class SongData
    {
        public int id;
        public string query;
        public string filename;
        public string title;
        public string artist;
    }

    [System.Serializable]
    public class StringPayload
    {
        public string value;
    }

    [System.Serializable]
    public class MidiData
    {
        public int note;
        public float velocity;
        public string action;
        public int[] notes;
        public string file;
        public int count;
        public bool suggest;
        public string status;
        public string message;
        public string filename;
        public string title;
        public string artist;
        public string folder;
        public int pages;
        public bool has_material;
        public SongData[] songs;
        public MidiData() { }
    }
}
