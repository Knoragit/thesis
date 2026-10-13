using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.EventSystems;

public class SongListManager : MonoBehaviour
{
    [Header("Contenitori (VerticalLayoutGroup) dei gruppi")]
    public RectTransform contenitoreObserver;
    public RectTransform contenitoreSeguimi;

    private UdpReceiver receiver;
    private GameManager gameManager;
    private TMP_FontAsset fontAsset;
    private Sprite bottoneSpr;
    private Sprite campoSpr;
    private Sprite pannelloSpr;

    private GameObject barraObserver;
    private GameObject barraSeguimi;
    private RectTransform contenutoObserver;
    private RectTransform contenutoSeguimi;
    private IndietroFeedback indietroObserver;
    private IndietroFeedback indietroSeguimi;
    private readonly List<GameObject> bottoniObserver = new List<GameObject>();
    private readonly List<GameObject> bottoniSeguimi = new List<GameObject>();

    private bool menuObserverAperto = true;
    private bool menuSeguimiAperto = true;
    private GameObject cerchioObserver;
    private GameObject cerchioSeguimi;
    private bool transizioneMenu = false;
    private static Sprite spriteCerchio;

    // Stato "modalità ricerca": nasconde l'elenco DB, mostra la tastiera e dopo Cerca
    // i bottoni mostrano SOLO i risultati della ricerca.
    private bool inRicerca = false;
    private string queryRicerca = "";
    private bool inAttesaRicerca = false;
    private RectTransform contenitoreRicerca;
    private GameObject elencoAttivo;
    private GameObject tastieraAttiva;
    private float larghezzaOrigRicerca = 100f;
    private float altezzaScorrRicerca = 300f;
    private Vector2 posOrigRicerca = Vector2.zero;
    private float indietroLocXOrig = -150f;
    private const float LARGHEZZA_RICERCA = 250f;

    // Firme (id in ordine) dell'ultima lista renderizzata: servono a saltare la
    // ricostruzione quando il contenuto non cambia (anti-sfarfallio).
    private List<string> firmaObserver = new List<string>();
    private List<string> firmaSeguimi = new List<string>();
    private Coroutine coroutineDebounce;
    private string queryDebounce = "";
    private PianoVisualizer visualizerCache;
    private RaggioPuntatore raggioCache;

    private GameManager.AppState statoPrecedente = GameManager.AppState.Onboarding;

    void Start()
    {
        receiver = FindFirstObjectByType<UdpReceiver>();
        gameManager = FindFirstObjectByType<GameManager>();

        // Se i contenitori non sono assegnati manualmente, li ricaviamo dal GameManager
        // (observerGroup e seguimiGroup sono i GameObject root dei due gruppi UI).
        if (gameManager != null)
        {
            if (contenitoreObserver == null && gameManager.observerGroup != null)
                contenitoreObserver = gameManager.observerGroup.GetComponent<RectTransform>();
            if (contenitoreSeguimi == null && gameManager.seguimiGroup != null)
                contenitoreSeguimi = gameManager.seguimiGroup.GetComponent<RectTransform>();
        }

        if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();

        if (fontAsset == null)
            fontAsset = GameManager.FontApp != null
                ? GameManager.FontApp
                : (TMP_Settings.defaultFontAsset != null
                    ? TMP_Settings.defaultFontAsset
                    : Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF"));

        // Forma arrotondata uguale a quella dei bottoni statici (menu/FaiTu).
        Sprite arrotondato = GameManager.SpriteArrotondato != null
            ? GameManager.SpriteArrotondato
            : CreaSpriteBianco();
        // bottoneSpr = pannelloSpr = solita forma arrotondata di prima (quella dei
        // bottoni statici menu/FaiTu). Sono due campi distinti solo per ricordare
        // che i pulsanti usano bottoneSpr e gli sfondi pannelloSpr.
        bottoneSpr = arrotondato;
        campoSpr = arrotondato;
        pannelloSpr = arrotondato;

        NascondiBottoniStatici(contenitoreObserver);
        NascondiBottoniStatici(contenitoreSeguimi);

        PreparaContenitore(contenitoreObserver);
        PreparaContenitore(contenitoreSeguimi);

        PosizionaIndietro(contenitoreObserver);
        PosizionaIndietro(contenitoreSeguimi);

        if (contenitoreObserver != null)
        {
            barraObserver = CreaBarraRicerca(contenitoreObserver, true);
            contenutoObserver = CreaElencoScorrevole(contenitoreObserver);
        }
        if (contenitoreSeguimi != null)
        {
            barraSeguimi = CreaBarraRicerca(contenitoreSeguimi, false);
            contenutoSeguimi = CreaElencoScorrevole(contenitoreSeguimi);
        }

        RiallineaBottoni(contenitoreObserver, barraObserver, null);
        RiallineaBottoni(contenitoreSeguimi, null, barraSeguimi);

        indietroObserver = AgganciaIndietro(contenitoreObserver);
        indietroSeguimi = AgganciaIndietro(contenitoreSeguimi);

        cerchioObserver = CreaCerchioPlus(contenitoreObserver);
        cerchioSeguimi = CreaCerchioPlus(contenitoreSeguimi);

        AggiornaGruppoAttivo();
    }

    // Attiva il feedback manuale (verde solo a hover reale) sul bottone "Indietro"
    // del gruppo, così non resta mai "appeso" dopo lo scroll della lista.
    private IndietroFeedback AgganciaIndietro(RectTransform contenitore)
    {
        if (contenitore != null)
        {
            for (int i = 0; i < contenitore.childCount; i++)
            {
                Transform figlio = contenitore.GetChild(i);
                if (figlio.name == "Bottone-Indietro")
                {
                    return figlio.gameObject.GetComponent<IndietroFeedback>()
                        ?? figlio.gameObject.AddComponent<IndietroFeedback>();
                }
            }
        }
        return null;
    }

    // Riporta i bottoni "Indietro" e tutti i bottoni delle canzoni al loro colore
    // base (defensivo: copre ogni refresh/ricerca/scroll che potrebbe aver lasciato
    // uno stato evidenziato residuo, incluso il drag partito sopra una canzone).
    public void RilasciaIndietro()
    {
        if (indietroObserver != null) indietroObserver.Rilascia();
        if (indietroSeguimi != null) indietroSeguimi.Rilascia();
        RilasciaCanzoni(bottoniObserver);
        RilasciaCanzoni(bottoniSeguimi);
    }

    private static void RilasciaCanzoni(List<GameObject> lista)
    {
        for (int i = 0; i < lista.Count; i++)
        {
            if (lista[i] == null) continue;
            IndietroFeedback fb = lista[i].GetComponent<IndietroFeedback>();
            if (fb != null) fb.Rilascia();
        }
    }

    // Riporta lo ScrollRect in cima: senza questo reset, se prima della ricerca ci si
    // era fermati in fondo alla lista piena (48 brani), i risultati/messaggi (content
    // corto, ancorato in alto) verrebbero renderizzati fuori dall'area della Viewport
    // e resterebbero invisibili dietro la maschera.
    private static void PortaScrollInCima(ScrollRect sr)
    {
        if (sr == null) return;
        sr.verticalNormalizedPosition = 1f;
        sr.StopMovement();
        sr.velocity = Vector2.zero;
    }

    private void NascondiBottoniStatici(RectTransform contenitore)
    {
        if (contenitore == null) return;
        for (int i = 0; i < contenitore.childCount; i++)
        {
            Transform figlio = contenitore.GetChild(i);
            if (figlio.GetComponent<BranoButtonUI>() != null)
            {
                figlio.gameObject.SetActive(false);
            }
        }
    }

    private void PreparaContenitore(RectTransform contenitore)
    {
        if (contenitore == null) return;

        VerticalLayoutGroup vlg = contenitore.GetComponent<VerticalLayoutGroup>();
        if (vlg == null) vlg = contenitore.gameObject.AddComponent<VerticalLayoutGroup>();

        vlg.padding = new RectOffset(6, 6, 6, 6);
        vlg.spacing = 5f;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        ContentSizeFitter csf = contenitore.GetComponent<ContentSizeFitter>();
        if (csf == null) csf = contenitore.gameObject.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Gruppo piu' largo (400 -> 620 unita' mondo) cosi' bottoni brano,
        // barra di ricerca e tasti sono piu' comodi da premere col pinch. La
        // crescita e' asimmetrica (solo verso destra): il bordo sinistro resta
        // dov'e' e il bottone "Indietro" viene contro-spostato da PosizionaIndietro.
        const float LARGHEZZA_BASE = 175f;
        const float LARGHEZZA_CORRENTE = 125f;
        float deltaLocal = LARGHEZZA_BASE - LARGHEZZA_CORRENTE;
        contenitore.sizeDelta = new Vector2(LARGHEZZA_BASE, contenitore.sizeDelta.y);
        if (deltaLocal > 0f)
        {
            contenitore.anchoredPosition = new Vector2(
                contenitore.anchoredPosition.x + deltaLocal * 2f,
                contenitore.anchoredPosition.y);
        }
    }

    // Porta il bottone "Indietro" del gruppo in alto a sinistra, nella stessa
    // posizione/dimensione usata da FaiTu e Tutorial (angolo -500,500; 460x230
    // mondo). Il VerticalLayoutGroup lo ignora: niente piu' in fondo alla lista.
    private void PosizionaIndietro(RectTransform contenitore)
    {
        if (contenitore == null) return;

        for (int i = 0; i < contenitore.childCount; i++)
        {
            Transform figlio = contenitore.GetChild(i);
            if (figlio.name != "Bottone-Indietro") continue;

            RectTransform rt = (RectTransform)figlio;
            LayoutElement le = figlio.GetComponent<LayoutElement>();
            if (le == null) le = figlio.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-150f, 125f);
            rt.sizeDelta = new Vector2(115f, 57.5f);

            // Stesso raggio degli angoli dei bottoni "Torna al Menù" delle altre
            // modalità: il gruppo e' scalato x4, quindi il moltiplicatore va
            // aumentato (x4) per ridurre il bordo e pareggiare il raggio di FaiTu.
            Image imgIndietro = figlio.GetComponent<Image>();
            if (imgIndietro != null) imgIndietro.pixelsPerUnitMultiplier = 4f;

            TextMeshProUGUI label = figlio.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                // Le unita' sono locali (il gruppo e' scalato x4): 15 * 4 = 60,
                // stessa altezza apparente del "Torna al Menu" di FaiTu (x1, 60).
                label.fontSize = 15f;
                GameManager.ApplicaStileTesto(label);
            }
            break;
        }
    }

    private static RectTransform TrovaIndietro(RectTransform contenitore)
    {
        if (contenitore == null) return null;
        for (int i = 0; i < contenitore.childCount; i++)
        {
            Transform figlio = contenitore.GetChild(i);
            if (figlio.name == "Bottone-Indietro") return (RectTransform)figlio;
        }
        return null;
    }

    private static float LeggiIndietroX(RectTransform contenitore)
    {
        RectTransform rt = TrovaIndietro(contenitore);
        return rt != null ? rt.anchoredPosition.x : -150f;
    }

    private static void ImpostaIndietroX(RectTransform contenitore, float x)
    {
        RectTransform rt = TrovaIndietro(contenitore);
        if (rt != null) rt.anchoredPosition = new Vector2(x, rt.anchoredPosition.y);
    }

    private Sprite CreaSpriteBianco()
    {
        Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        Color[] px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++) px[i] = Color.white;
        tex.SetPixels(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
                             100f, 0, SpriteMeshType.FullRect, new Vector4(1, 1, 1, 1));
    }

    void Update()
    {
        if (gameManager == null) return;

        if (gameManager.statoAttuale != statoPrecedente)
        {
            GameManager.AppState statoInUscita = statoPrecedente;
            GameManager.AppState statoInEntrata = gameManager.statoAttuale;
            statoPrecedente = gameManager.statoAttuale;
            TerminaRicerca(false);
            AggiornaGruppoAttivo();

            if (statoInEntrata == GameManager.AppState.Osservatore ||
                statoInEntrata == GameManager.AppState.Seguimi)
            {
                InizializzaMenuAperto(statoInEntrata == GameManager.AppState.Osservatore
                    ? contenitoreObserver : contenitoreSeguimi);
            }

            // Uscita dalle modalità esecutive (Osservatore/Seguimi): stop
            // difensivo così riproduzione e colonne vengono sempre fermate,
            // qualunque sia il percorso di navigazione verso il menu.
            if (statoInUscita == GameManager.AppState.Osservatore ||
                statoInUscita == GameManager.AppState.Seguimi)
            {
                FermaRiproduzione();
            }
        }
    }

    private void AggiornaGruppoAttivo()
    {
        if (gameManager == null || receiver == null) return;

        if (gameManager.statoAttuale == GameManager.AppState.Osservatore ||
            gameManager.statoAttuale == GameManager.AppState.Seguimi)
        {
            receiver.InviaComandoListaSongs();
        }
    }

    // ---------------------------------------------------------------
    // RICEZIONE DATI DAL BRIDGE (chiamati da UdpReceiver)
    // ---------------------------------------------------------------
    public void SongListRicevuta(UdpReceiver.SongData[] songs, bool suggest)
    {
        if (gameManager == null) return;

        List<GameObject> targetList;
        RectTransform elenco;

        if (inRicerca && contenitoreRicerca != null)
        {
            // In ricerca i risultati vanno SEMPRE nel contenitore da cui è partita
            // la ricerca (lo stato globale può non corrispondere al gruppo mostrato).
            if (contenitoreRicerca == contenitoreSeguimi)
            {
                targetList = bottoniSeguimi;
                elenco = contenutoSeguimi;
            }
            else
            {
                targetList = bottoniObserver;
                elenco = contenutoObserver;
            }
        }
        else if (gameManager.statoAttuale == GameManager.AppState.Osservatore)
        {
            targetList = bottoniObserver;
            elenco = contenutoObserver;
        }
        else if (gameManager.statoAttuale == GameManager.AppState.Seguimi)
        {
            targetList = bottoniSeguimi;
            elenco = contenutoSeguimi;
        }
        else
        {
            return;
        }

        if (elenco == null) return;

        // I suggerimenti live sostituiscono l'elenco, così mostriamo i risultati
        // filtrati. Per le liste complete (list_songs) mostriamo TUTTE le canzoni:
        // il cap a 12 vale solo per i suggerimenti, altrimenti i brani nuovi in
        // fondo alla lista non comparirebbero mai.
        int maxMostrati = 0;
        if (songs != null)
            maxMostrati = suggest ? Mathf.Min(songs.Length, 12) : songs.Length;

        RenderizzaLista(elenco, targetList, songs, maxMostrati);

        RiallineaBottoni(contenitoreObserver, barraObserver, barraSeguimi);
        RiallineaBottoni(contenitoreSeguimi, barraObserver, barraSeguimi);

        RilasciaSoloIndietro();
        RifrescaRaggio();
    }

    // Renderizza una lista di canzoni nel contenitore indicato, distruggendo prima
    // i vecchi bottoni/messaggi. In ricerca gestisce anche il messaggio di stato.
    private void RenderizzaLista(RectTransform elenco, List<GameObject> targetList,
                                 UdpReceiver.SongData[] songs, int maxMostrati)
    {
        List<string> firma = new List<string>();
        if (songs != null)
        {
            for (int i = 0; i < maxMostrati; i++)
                firma.Add(songs[i].id.ToString());
        }

        // Anti-sfarfallio: se il contenuto non è cambiato rispetto all'ultimo
        // render, non distruggiamo/ricreiamo i bottoni (evita rebuild continui,
        // layout oscillante e perdita di scroll/hover a ogni risposta di rete).
        // In ricerca con firma vuota si ristampa comunque: il messaggio può
        // passare da "Ricerca in corso..." a "Nessun risultato...".
        bool conMessaggioVuota = inRicerca && firma.Count == 0;
        if (!conMessaggioVuota && FirmaUguale(ListaFirma(targetList), firma)) return;

        SvuotaBottoni(targetList);

        int mostrati = 0;
        if (songs != null)
        {
            for (int i = 0; i < maxMostrati; i++)
            {
                CreaBottoneBrano(elenco, songs[i].title, songs[i].artist, songs[i].id, targetList, false);
                mostrati++;
            }
        }
        AggiornaFirma(targetList, firma);

        // In modalità ricerca, se non c'è nessun match mostriamo un messaggio
        // chiaro al posto di una lista vuota. Durante il download online la
        // ricerca può richiedere qualche secondo: mostriamo l'attesa.
        if (inRicerca && mostrati == 0)
        {
            string testo = inAttesaRicerca
                ? "Ricerca in corso..."
                : "Nessun risultato per \"" + queryRicerca + "\"";
            CreaMessaggio(elenco, testo, targetList);
        }

        if (inRicerca)
        {
            ScrollRect sr = elenco.GetComponentInParent<ScrollRect>();
            PortaScrollInCima(sr);
        }

        RiallineaBottoni(contenitoreObserver, barraObserver, barraSeguimi);
        RiallineaBottoni(contenitoreSeguimi, barraObserver, barraSeguimi);

        RilasciaSoloIndietro();
    }

    private static bool FirmaUguale(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private List<string> ListaFirma(List<GameObject> targetList)
    {
        return (targetList == bottoniSeguimi) ? firmaSeguimi : firmaObserver;
    }

    private void AggiornaFirma(List<GameObject> targetList, List<string> firma)
    {
        List<string> salvata = ListaFirma(targetList);
        salvata.Clear();
        salvata.AddRange(firma);
    }

    // Reset hover SOLO dei bottoni "Indietro" quando la lista viene renderizzata:
    // le righe canzoni non devono più spegnersi a ogni risposta di rete.
    private void RilasciaSoloIndietro()
    {
        if (indietroObserver != null) indietroObserver.Rilascia();
        if (indietroSeguimi != null) indietroSeguimi.Rilascia();
    }

    public void SearchResultRicevuta(string status, string filename, string title, string artist, string message)
    {
        if (gameManager == null) return;

        inAttesaRicerca = false;

        if (status == "success" && filename != null)
        {
            // Il file è ora nel DB. In modalità ricerca mostriamo SOLO i risultati
            // aggiornati (il suggerimento include il brano appena scaricato, con il suo id),
            // altrimenti ricarichiamo l'intera lista del DB.
            if (inRicerca && !string.IsNullOrEmpty(queryRicerca))
            {
                if (receiver != null) receiver.InviaComandoSuggerimento(queryRicerca);
            }
            else if (receiver != null)
            {
                receiver.InviaComandoListaSongs();
            }
        }
        else
        {
            UnityEngine.Debug.LogWarning("[RICERCA] " + (string.IsNullOrEmpty(message) ? "Nessun risultato." : message));

            // La ricerca online è terminata senza successo: aggiorna la lista così
            // il messaggio "Ricerca in corso..." diventa "Nessun risultato...".
            if (inRicerca && !string.IsNullOrEmpty(queryRicerca) && receiver != null)
            {
                receiver.InviaComandoSuggerimento(queryRicerca);
            }
        }

        RiallineaBottoni(contenitoreObserver, barraObserver, barraSeguimi);
        RiallineaBottoni(contenitoreSeguimi, barraObserver, barraSeguimi);
    }

    public void PlayResultRicevuta(string status, string message)
    {
        if (status == "error")
        {
            UnityEngine.Debug.LogWarning("[PLAY] " + message);
        }
    }

    // Avvia la ricerca: chiude la tastiera, mostra l'elenco (dimensione piena) e
    // invia prima i risultati immediati (suggerimento locale) poi la ricerca completa
    // (che scarica il file se non presente nel DB).
    private void AvviaRicerca(string testo)
    {
        if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();
        if (receiver == null) return;

        queryRicerca = testo.Trim();
        inRicerca = true;
        inAttesaRicerca = true;

        if (elencoAttivo != null)
        {
            LayoutElement leScorr = elencoAttivo.GetComponent<LayoutElement>();
            if (leScorr != null) leScorr.preferredHeight = altezzaScorrRicerca;
            elencoAttivo.SetActive(true);
            PortaScrollInCima(elencoAttivo.GetComponent<ScrollRect>());
        }

        // Feedback immediato: svuota la lista del contenitore di ricerca e mostra
        // l'attesa mentre il bridge cerca nel DB locale e online. I risultati
        // arrivati sostituiranno il messaggio (SvuotaBottoni lo rimuove).
        List<GameObject> targetRicerca = bottoniObserver;
        RectTransform elencoRicerca = contenutoObserver;
        if (contenitoreRicerca == contenitoreSeguimi)
        {
            targetRicerca = bottoniSeguimi;
            elencoRicerca = contenutoSeguimi;
        }
        SvuotaBottoni(targetRicerca);
        if (elencoRicerca != null) CreaMessaggio(elencoRicerca, "Ricerca in corso...", targetRicerca);

        receiver.InviaComandoSuggerimento(queryRicerca);
        receiver.InviaComandoRicerca(queryRicerca);
        Debug.Log("[UDP] Ricerca avviata: \"" + queryRicerca + "\"");
    }

    // Uscita dalla modalità ricerca: chiude la tastiera, ripristina larghezza/scroll
    // originali e (se richiesto) ricarica la lista completa del DB.
    private void TerminaRicerca(bool ricaricaLista)
    {
        inRicerca = false;
        queryRicerca = "";
        inAttesaRicerca = false;

        if (tastieraAttiva != null) tastieraAttiva.SetActive(false);
        tastieraAttiva = null;

        if (contenitoreRicerca != null)
        {
            contenitoreRicerca.sizeDelta = new Vector2(larghezzaOrigRicerca, contenitoreRicerca.sizeDelta.y);
            contenitoreRicerca.anchoredPosition = posOrigRicerca;
            ImpostaIndietroX(contenitoreRicerca, indietroLocXOrig);
            contenitoreRicerca = null;
        }

        if (elencoAttivo != null)
        {
            LayoutElement leScorr = elencoAttivo.GetComponent<LayoutElement>();
            if (leScorr != null) leScorr.preferredHeight = altezzaScorrRicerca;
            elencoAttivo.SetActive(true);
            PortaScrollInCima(elencoAttivo.GetComponent<ScrollRect>());
            elencoAttivo = null;
        }

        if (ricaricaLista)
        {
            if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();
            if (receiver != null) receiver.InviaComandoListaSongs();
        }

        RilasciaIndietro();
    }

    // Chiamata dai bottoni dei brani quando parte la riproduzione: esce dalla
    // modalità ricerca e ricarica la lista completa (lista iniziale + brano cercato).
    public void CanzoneAvviata()
    {
        if (!inRicerca) return;
        TerminaRicerca(true);
    }

    // Ferma l'esecuzione corrente (riproduzione + colonne) e pulisce il visualizzatore.
    private void FermaRiproduzione()
    {
        if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();
        if (receiver != null) receiver.InviaComandoStop();

        if (visualizerCache == null) visualizerCache = FindFirstObjectByType<PianoVisualizer>();
        if (visualizerCache != null)
        {
            visualizerCache.ResetVisualizer();
            visualizerCache.PulisciNoteAtteseVisive();
        }
    }

    // Debounce dei suggerimenti della barra di ricerca: la richiesta parte solo
    // dopo ~300ms da fermo, così non si ricostruisce la lista a ogni tasto.
    private void AccodaSuggerimento(string val)
    {
        // Durante la ricerca niente liste live: i risultati arrivano solo dopo Cerca.
        if (inRicerca) return;

        queryDebounce = val;
        if (coroutineDebounce != null) StopCoroutine(coroutineDebounce);
        coroutineDebounce = StartCoroutine(InvioSuggerimentoDopoPausa());
    }

    private IEnumerator InvioSuggerimentoDopoPausa()
    {
        yield return new WaitForSeconds(0.3f);
        coroutineDebounce = null;
        if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();
        if (receiver == null) yield break;
        if (string.IsNullOrEmpty(queryDebounce))
            receiver.InviaComandoListaSongs();
        else
            receiver.InviaComandoSuggerimento(queryDebounce);
    }

    // La lista ricostruita o il menù che cambia invalidano la cache del raggio:
    // forza il rinfresco immediato (metà secondo di ritardo = laser che trema).
    private void RifrescaRaggio()
    {
        if (raggioCache == null) raggioCache = FindFirstObjectByType<RaggioPuntatore>();
        if (raggioCache != null) raggioCache.RinfrescaOra();
    }

    private static GameObject TrovaElencoScroll(RectTransform contenitore)
    {
        if (contenitore == null) return null;
        Transform el = contenitore.Find("ElencoBrani");
        return el != null ? el.gameObject : null;
    }

    private RectTransform ContenitoreCorrente()
    {
        if (gameManager == null) return null;
        if (gameManager.statoAttuale == GameManager.AppState.Osservatore) return contenitoreObserver;
        if (gameManager.statoAttuale == GameManager.AppState.Seguimi) return contenitoreSeguimi;
        return null;
    }

    // Chiamata a fine avvio brano: il menù si chiude dentro il cerchio "+"
    // lasciando spazio alle colonne, con mini animazione di chiusura.
    public void ChiudiMenuCorrente()
    {
        RectTransform contenitore = ContenitoreCorrente();
        if (contenitore == null || transizioneMenu) return;

        bool siamoObserver = contenitore == contenitoreObserver;
        if (siamoObserver)
        {
            if (!menuObserverAperto) return;
            menuObserverAperto = false;
        }
        else
        {
            if (!menuSeguimiAperto) return;
            menuSeguimiAperto = false;
        }

        GameObject barra = siamoObserver ? barraObserver : barraSeguimi;
        GameObject elenco = TrovaElencoScroll(contenitore);
        GameObject cerchio = siamoObserver ? cerchioObserver : cerchioSeguimi;
        transizioneMenu = true;
        StartCoroutine(AnimaChiusura(contenitore, barra, elenco, cerchio));
        RifrescaRaggio();
    }

    public void ApriMenu(RectTransform contenitore)
    {
        if (contenitore == null || transizioneMenu) return;

        bool siamoObserver = contenitore == contenitoreObserver;
        if (siamoObserver)
        {
            if (menuObserverAperto) return;
            menuObserverAperto = true;
        }
        else
        {
            if (menuSeguimiAperto) return;
            menuSeguimiAperto = true;
        }

        GameObject barra = siamoObserver ? barraObserver : barraSeguimi;
        GameObject elenco = TrovaElencoScroll(contenitore);
        GameObject cerchio = siamoObserver ? cerchioObserver : cerchioSeguimi;
        transizioneMenu = true;
        StartCoroutine(AnimaApertura(contenitore, barra, elenco, cerchio));
        RifrescaRaggio();
    }

    private void ApriMenuDaCerchio(RectTransform contenitore)
    {
        FermaRiproduzione();
        ApriMenu(contenitore);
    }

    private void InizializzaMenuAperto(RectTransform contenitore)
    {
        if (contenitore == null) return;
        bool siamoObserver = contenitore == contenitoreObserver;
        if (siamoObserver) menuObserverAperto = true;
        else menuSeguimiAperto = true;

        GameObject barra = siamoObserver ? barraObserver : barraSeguimi;
        GameObject elenco = TrovaElencoScroll(contenitore);
        GameObject cerchio = siamoObserver ? cerchioObserver : cerchioSeguimi;

        if (barra != null)
        {
            Ripristina(barra);
            if (!barra.activeSelf) barra.SetActive(true);
        }
        if (elenco != null)
        {
            Ripristina(elenco);
            if (!elenco.activeSelf) elenco.SetActive(true);
        }
        if (cerchio != null) cerchio.SetActive(false);
        RipristinaLayoutContenitore(contenitore, barra, elenco);
        RifrescaRaggio();
    }

    private IEnumerator AnimaChiusura(RectTransform contenitore, GameObject barra, GameObject elenco, GameObject cerchio)
    {
        RectTransform rtBarra = barra != null ? (RectTransform)barra.transform : null;
        RectTransform rtElenco = elenco != null ? (RectTransform)elenco.transform : null;
        RectTransform rtCerchio = cerchio != null ? (RectTransform)cerchio.transform : null;

        Vector3 destinazione = cerchio != null ? cerchio.transform.position : Vector3.zero;
        Vector3 posBarra = rtBarra != null ? rtBarra.position : destinazione;
        Vector3 posElenco = rtElenco != null ? rtElenco.position : destinazione;

        CanvasGroup cgBarra = PreparaCanvasGroup(barra);
        CanvasGroup cgElenco = PreparaCanvasGroup(elenco);

        // Il cerchio sta sopra barra/elenco durante il volo, così non viene
        // occultato dai pannelli che si comprimono sopra di lui.
        if (rtCerchio != null)
        {
            rtCerchio.gameObject.SetActive(true);
            rtCerchio.localScale = Vector3.zero;
            rtCerchio.SetAsLastSibling();
        }

        // Barra/elenco escono dal layout: la scalatura non deve far impennare
        // l'altezza del contenitore. Il fitter resta libero e l'altezza viene
        // animata a mano fino al valore collassato (niente "pop" a SetActive).
        LayoutElement leBarra = IgnoraLayoutTransitorio(barra, true);
        LayoutElement leElenco = IgnoraLayoutTransitorio(elenco, true);
        ContentSizeFitter csf = CsfDelContenitore(contenitore);
        if (csf != null) csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
        float altezzaIniziale = contenitore != null ? contenitore.rect.height : 0f;
        float altezzaFinale = Mathf.Max(0f, altezzaIniziale - AltezzaIgnorata(leBarra) - AltezzaIgnorata(leElenco));

        float durata = 0.25f;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / durata;
            t = Mathf.Min(t, 1f);
            float k = 1f - Mathf.Pow(1f - t, 3f);
            if (rtBarra != null)
            {
                rtBarra.position = Vector3.Lerp(posBarra, destinazione, k);
                rtBarra.localScale = Vector3.one * (1f - k);
            }
            if (cgBarra != null) cgBarra.alpha = 1f - k;
            if (rtElenco != null)
            {
                rtElenco.position = Vector3.Lerp(posElenco, destinazione, k);
                rtElenco.localScale = Vector3.one * (1f - k);
            }
            if (cgElenco != null) cgElenco.alpha = 1f - k;
            if (rtCerchio != null) rtCerchio.localScale = Vector3.one * k;
            if (contenitore != null)
                contenitore.sizeDelta = new Vector2(contenitore.sizeDelta.x, Mathf.Lerp(altezzaIniziale, altezzaFinale, k));
            yield return null;
        }

        if (barra != null)
        {
            Ripristina(barra);
            barra.SetActive(false);
        }
        if (elenco != null)
        {
            Ripristina(elenco);
            elenco.SetActive(false);
        }
        if (rtCerchio != null) rtCerchio.localScale = Vector3.one;
        if (contenitore != null && altezzaFinale >= 0f)
            contenitore.sizeDelta = new Vector2(contenitore.sizeDelta.x, altezzaFinale);
        RipristinaLayoutTransitorio(barra, leBarra);
        RipristinaLayoutTransitorio(elenco, leElenco);
        transizioneMenu = false;
    }

    private IEnumerator AnimaApertura(RectTransform contenitore, GameObject barra, GameObject elenco, GameObject cerchio)
    {
        RectTransform rtBarra = barra != null ? (RectTransform)barra.transform : null;
        RectTransform rtElenco = elenco != null ? (RectTransform)elenco.transform : null;
        RectTransform rtCerchio = cerchio != null ? (RectTransform)cerchio.transform : null;

        CanvasGroup cgBarra = PreparaCanvasGroup(barra);
        CanvasGroup cgElenco = PreparaCanvasGroup(elenco);

        // Barra/elenco restano dentro il layout all'apertura: appena attivati il
        // VerticalLayoutGroup li riposiziona subito al loro posto (in alto), quindi
        // la transizione scala/sfuma lì senza apparire storti sopra il bottone "+".
        // L'altezza del contenitore viene comunque animata a mano e il fitter viene
        // ripristinato solo a fine transizione (si ricalcola allo stesso valore).
        ContentSizeFitter csf = CsfDelContenitore(contenitore);
        if (csf != null) csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
        float altezzaAttuale = contenitore != null ? contenitore.rect.height : 0f;
        float altezzaFinale = altezzaAttuale + AltezzaPreferita(barra) + AltezzaPreferita(elenco);

        if (barra != null)
        {
            IgnoraLayoutTransitorio(barra, false);
            Ripristina(barra);
            barra.SetActive(true);
        }
        if (elenco != null)
        {
            IgnoraLayoutTransitorio(elenco, false);
            Ripristina(elenco);
            elenco.SetActive(true);
        }

        if (rtBarra != null) rtBarra.localScale = Vector3.one * 0.01f;
        if (rtElenco != null) rtElenco.localScale = Vector3.one * 0.01f;
        if (cgBarra != null) cgBarra.alpha = 0f;
        if (cgElenco != null) cgElenco.alpha = 0f;

        float durata = 0.25f;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / durata;
            t = Mathf.Min(t, 1f);
            float k = 1f - Mathf.Pow(1f - t, 3f);
            if (rtBarra != null) rtBarra.localScale = Vector3.one * k;
            if (cgBarra != null) cgBarra.alpha = k;
            if (rtElenco != null) rtElenco.localScale = Vector3.one * k;
            if (cgElenco != null) cgElenco.alpha = k;
            if (rtCerchio != null) rtCerchio.localScale = Vector3.one * (1f - k);
            if (contenitore != null)
                contenitore.sizeDelta = new Vector2(contenitore.sizeDelta.x, Mathf.Lerp(altezzaAttuale, altezzaFinale, k));
            yield return null;
        }

        if (rtBarra != null) rtBarra.localScale = Vector3.one;
        if (rtElenco != null) rtElenco.localScale = Vector3.one;
        if (cgBarra != null) cgBarra.alpha = 1f;
        if (cgElenco != null) cgElenco.alpha = 1f;
        if (contenitore != null) contenitore.sizeDelta = new Vector2(contenitore.sizeDelta.x, altezzaFinale);
        if (rtCerchio != null) rtCerchio.gameObject.SetActive(false);

        IgnoraLayoutTransitorio(barra, false);
        IgnoraLayoutTransitorio(elenco, false);
        if (csf != null)
        {
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutRebuilder.MarkLayoutForRebuild(contenitore);
        }
        transizioneMenu = false;
    }

    private static CanvasGroup PreparaCanvasGroup(GameObject go)
    {
        if (go == null) return null;
        return go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
    }

    private static void Ripristina(GameObject go)
    {
        if (go == null) return;
        RectTransform rt = go.transform as RectTransform;
        if (rt != null) rt.localScale = Vector3.one;
        CanvasGroup cg = go.GetComponent<CanvasGroup>();
        if (cg != null) cg.alpha = 1f;
    }

    // Esce/rientra dal layout il gruppo durante la transizione del menù "+".
    private static LayoutElement IgnoraLayoutTransitorio(GameObject go, bool ignora)
    {
        if (go == null) return null;
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.ignoreLayout = ignora;
        return le;
    }

    private static void RipristinaLayoutTransitorio(GameObject go, LayoutElement le)
    {
        if (go == null) return;
        if (le == null) le = go.GetComponent<LayoutElement>();
        if (le != null) le.ignoreLayout = false;
    }

    private static float AltezzaIgnorata(LayoutElement le)
    {
        return le != null ? Mathf.Max(0f, le.preferredHeight) : 0f;
    }

    // Legge l'altezza preferita senza toccare ignoreLayout (utile in apertura,
    // dove barra/elenco devono restare dentro il layout).
    private static float AltezzaPreferita(GameObject go)
    {
        if (go == null) return 0f;
        LayoutElement le = go.GetComponent<LayoutElement>();
        return le != null ? Mathf.Max(0f, le.preferredHeight) : 0f;
    }

    private static ContentSizeFitter CsfDelContenitore(RectTransform contenitore)
    {
        if (contenitore == null) return null;
        return contenitore.GetComponent<ContentSizeFitter>();
    }

    // Ripristina l'auto-layout del contenitore (dopo la chiusura del menù il
    // fitter era stato liberato per animare l'altezza a mano).
    private void RipristinaLayoutContenitore(RectTransform contenitore, GameObject barra, GameObject elenco)
    {
        if (contenitore == null) return;
        IgnoraLayoutTransitorio(barra, false);
        IgnoraLayoutTransitorio(elenco, false);
        ContentSizeFitter csf = CsfDelContenitore(contenitore);
        if (csf != null)
        {
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutRebuilder.MarkLayoutForRebuild(contenitore);
        }
    }

    // Bottone circolare "+" a destra del bottone Indietro (colori legati alla
    // palette: Tan base, Dusty Rose hover/pressione).
    // Scarto destro del "+" rispetto al bordo di "Indietro", in unita' del gruppo
    // (il gruppo e' scalato x4, quindi 40 unita' locali = 160 di mondo).
    private const float ScartoPlus = 40f;

    private GameObject CreaCerchioPlus(RectTransform contenitore)
    {
        if (contenitore == null) return null;

        RectTransform indietro = TrovaIndietro(contenitore);
        float diametro = indietro != null ? Mathf.Abs(indietro.sizeDelta.y) : 57.5f;
        float xBase = indietro != null ? indietro.anchoredPosition.x : -150f;
        float yBase = indietro != null ? indietro.anchoredPosition.y : 125f;

        GameObject cerchioGO = new GameObject("BottonePlus", typeof(RectTransform));
        cerchioGO.transform.SetParent(contenitore, false);

        RectTransform rt = (RectTransform)cerchioGO.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(xBase + diametro + diametro * 0.5f + ScartoPlus, yBase);
        rt.sizeDelta = new Vector2(diametro, diametro);

        LayoutElement le = cerchioGO.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        Image img = cerchioGO.AddComponent<Image>();
        img.sprite = SpriteCerchio;
        img.type = Image.Type.Simple;
        img.color = GameManager.TanColor;
        GameManager.ApplicaPenombra(cerchioGO, 0.30f, 3f);

        Button btn = cerchioGO.AddComponent<Button>();
        btn.targetGraphic = img;

        IndietroFeedback feedback = cerchioGO.AddComponent<IndietroFeedback>();
        feedback.ImpostaColori(GameManager.TanColor, GameManager.DustyRose);
        feedback.ImpostaColorePremuto(GameManager.DustyRose);
        // Il "+" e' visibile solo da chiuso e fermo, quindi puo' ingrandirsi
        // all'hover senza interferire con l'animazione di apertura/chiusura.
        feedback.fattoreHover = 1.12f;
        feedback.fattorePremuto = 1.06f;
        GlowBottone.Applica(cerchioGO, GlowBottone.FormaGlow.Circolare);

        GameObject labelGO = CrearGOFiglio(cerchioGO.transform, "Testo");
        RectTransform rtLabel = (RectTransform)labelGO.transform;
        rtLabel.anchorMin = Vector2.zero;
        rtLabel.anchorMax = Vector2.one;
        rtLabel.offsetMin = Vector2.zero;
        rtLabel.offsetMax = Vector2.zero;
        TextMeshProUGUI label = labelGO.AddComponent<TextMeshProUGUI>();
        label.text = "+";
        label.font = fontAsset;
        label.fontSize = diametro * 0.4f;
        label.color = Color.black;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        GameManager.ApplicaStileTesto(label);

        btn.onClick.AddListener(() => ApriMenuDaCerchio(contenitore));

        cerchioGO.SetActive(false);
        return cerchioGO;
    }

    private static Sprite SpriteCerchio
    {
        get
        {
            if (spriteCerchio == null) spriteCerchio = CreaSpriteCerchioProc();
            return spriteCerchio;
        }
    }

    private static Sprite CreaSpriteCerchioProc()
    {
        const int DIM = 128;
        Texture2D tex = new Texture2D(DIM, DIM, TextureFormat.RGBA32, false);
        float centro = (DIM - 1) * 0.5f;
        float raggio = centro - 1f;
        Color[] px = new Color[DIM * DIM];
        for (int y = 0; y < DIM; y++)
        {
            for (int x = 0; x < DIM; x++)
            {
                float dx = x - centro;
                float dy = y - centro;
                float distanza = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(raggio - distanza + 1f);
                px[y * DIM + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, DIM, DIM), new Vector2(0.5f, 0.5f), 100f,
                             0, SpriteMeshType.FullRect, Vector4.zero);
    }

    private void SvuotaBottoni(List<GameObject> lista)
    {
        for (int i = 0; i < lista.Count; i++)
        {
            if (lista[i] != null) Destroy(lista[i]);
        }
        lista.Clear();
    }

    private void RiallineaBottoni(RectTransform contenitore, GameObject barra1, GameObject barra2)
    {
        if (contenitore == null) return;

        // Sposta la barra di ricerca del gruppo corrente in cima (sibling 0).
        if (barra1 != null && barra1.transform.parent == contenitore) barra1.transform.SetAsFirstSibling();
        if (barra2 != null && barra2.transform.parent == contenitore) barra2.transform.SetAsFirstSibling();

        // Il bottone "Indietro" viene sempre tenuto in fondo alla lista.
        for (int i = 0; i < contenitore.childCount; i++)
        {
            Transform figlio = contenitore.GetChild(i);
            if (figlio.name == "Bottone-Indietro")
            {
                figlio.SetAsLastSibling();
                break;
            }
        }

        // La tastiera virtuale resta l'ultimo figlio (in fondo, sotto l'elenco)
        // così viene disegnata sopra tutto e non copre mai i bottoni.
        for (int i = 0; i < contenitore.childCount; i++)
        {
            Transform figlio = contenitore.GetChild(i);
            if (figlio.name == "TastieraVirtuale")
            {
                figlio.SetAsLastSibling();
                break;
            }
        }
    }

    // ---------------------------------------------------------------
    // COSTRUZIONE UI RUNTIME
    // ---------------------------------------------------------------
    private RectTransform CreaElencoScorrevole(RectTransform parent)
    {
        // ScrollView (pannello a altezza fissa)
        GameObject scrollGO = CrearGOFiglio(parent, "ElencoBrani");
        RectTransform rtScroll = (RectTransform)scrollGO.transform;
        rtScroll.anchorMin = Vector2.zero;
        rtScroll.anchorMax = new Vector2(1f, 0f);
        rtScroll.pivot = new Vector2(0.5f, 0.5f);
        rtScroll.sizeDelta = new Vector2(0f, 300f);

        LayoutElement leScroll = scrollGO.AddComponent<LayoutElement>();
        leScroll.preferredHeight = 300f;

        ScrollRect scroll = scrollGO.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Elastic;
        scroll.inertia = true;
        scroll.elasticity = 0.12f;
        scroll.scrollSensitivity = 40f;
        scroll.decelerationRate = 0.05f;

        // Viewport (maschera)
        GameObject vpGO = CrearGOFiglio(scrollGO.transform, "Viewport");
        // Azzera canzoni e bottoni "Indietro" all'inizio e alla fine di ogni drag
        // del contenuto (lo ScrollRect perde l'OnPointerUp della canzone premuta).
        vpGO.AddComponent<ScrollDragReset>();
        RectTransform rtVp = (RectTransform)vpGO.transform;
        rtVp.anchorMin = Vector2.zero;
        rtVp.anchorMax = Vector2.one;
        rtVp.offsetMin = Vector2.zero;
        rtVp.offsetMax = Vector2.zero;
        rtVp.pivot = new Vector2(0.5f, 0.5f);

        // Corsia dedicata della scrollbar (unita canvas): separa la "presa" del
        // pinch dai bottoni delle canzoni evitando ogni sovrapposizione.
        const float SB_LANE = 12f;
        const float SB_WIDTH = 6f;
        rtVp.offsetMax = new Vector2(-SB_LANE, 0f);

        RectMask2D mask = vpGO.AddComponent<RectMask2D>();

        Image sfondoVp = vpGO.AddComponent<Image>();
        sfondoVp.sprite = pannelloSpr;
        sfondoVp.type = Image.Type.Sliced;
        sfondoVp.raycastTarget = false;
        sfondoVp.color = GameManager.ConAlfa(GameManager.SteelBlue, 0.55f);

        GameObject sbGO = CrearGOFiglio(scrollGO.transform, "Scrollbar");
        RectTransform rtSb = (RectTransform)sbGO.transform;
        rtSb.anchorMin = new Vector2(1f, 0f);
        rtSb.anchorMax = new Vector2(1f, 1f);
        rtSb.pivot = new Vector2(1f, 0.5f);
        rtSb.anchoredPosition = Vector2.zero;
        rtSb.sizeDelta = new Vector2(SB_LANE, 0f);

        Image bgImg = sbGO.AddComponent<Image>();
        bgImg.sprite = pannelloSpr;
        bgImg.type = Image.Type.Sliced;
        bgImg.color = GameManager.ConAlfa(GameManager.SteelBlue, 0.30f);
        bgImg.raycastPadding = Vector4.zero;

        GameObject handleGO = CrearGOFiglio(sbGO.transform, "Handle");
        RectTransform rtHandle = (RectTransform)handleGO.transform;
        rtHandle.anchorMin = Vector2.zero;
        rtHandle.anchorMax = Vector2.zero;
        rtHandle.pivot = new Vector2(0.5f, 0.5f);
        rtHandle.anchoredPosition = new Vector2(SB_LANE * 0.5f, 0f);
        rtHandle.sizeDelta = new Vector2(SB_WIDTH, 0f);

        Image handleImg = handleGO.AddComponent<Image>();
        handleImg.sprite = pannelloSpr;
        handleImg.type = Image.Type.Sliced;
        handleImg.pixelsPerUnitMultiplier = 4f;
        handleImg.color = GameManager.SteelBlue;
        handleImg.raycastTarget = false;

        ScrollbarVisuale sbVisuale = sbGO.AddComponent<ScrollbarVisuale>();
        sbVisuale.scroll = scroll;
        sbVisuale.handle = rtHandle;
        sbVisuale.handleImg = handleImg;
        sbVisuale.trackImg = bgImg;

        // Content (VerticalLayoutGroup + ContentSizeFitter)
        GameObject contentGO = CrearGOFiglio(vpGO.transform, "Content");
        RectTransform rtContent = (RectTransform)contentGO.transform;
        rtContent.anchorMin = new Vector2(0f, 1f);
        rtContent.anchorMax = new Vector2(1f, 1f);
        rtContent.pivot = new Vector2(0.5f, 1f);
        rtContent.anchoredPosition = Vector2.zero;
        rtContent.sizeDelta = new Vector2(0f, 0f);

        VerticalLayoutGroup vlg = contentGO.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(6, 6, 6, 6);
        vlg.spacing = 8f;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        ContentSizeFitter csf = contentGO.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = rtVp;
        scroll.content = rtContent;

        return rtContent;
    }

    private GameObject CreaBarraRicerca(RectTransform contenitore, bool isObserver)
    {
        GameObject root = new GameObject("BarraRicerca", typeof(RectTransform));
        root.transform.SetParent(contenitore, false);

        RectTransform rtRoot = (RectTransform)root.transform;
        rtRoot.anchorMin = Vector2.zero;
        rtRoot.anchorMax = new Vector2(1f, 0f);
        rtRoot.pivot = new Vector2(0.5f, 0.5f);
        rtRoot.sizeDelta = new Vector2(0f, 42f);

        LayoutElement leRoot = root.AddComponent<LayoutElement>();
        leRoot.preferredHeight = 42f;

        HorizontalLayoutGroup hlg = root.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 5f;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.padding = new RectOffset(0, 0, 0, 0);
        hlg.childAlignment = TextAnchor.MiddleCenter;

        // Campo di testo nascosto: fa da buffer per la tastiera (i tasti ci
        // digitano dentro e il readout della tastiera mostra il testo). Essendo
        // inattivo non partecipa al layout, quindi nella barra resta solo Cerca.
        TMP_InputField input = CreaInputField(root.transform, isObserver);
        input.gameObject.SetActive(false);

        GameObject cercaGO = CrearGOFiglio(root.transform, "Cerca");
        LayoutElement leCerca = cercaGO.AddComponent<LayoutElement>();
        leCerca.preferredWidth = 130f;

        Image cercaImg = cercaGO.AddComponent<Image>();
        cercaImg.sprite = bottoneSpr;
        cercaImg.type = Image.Type.Sliced;
        cercaImg.color = Color.white;
        GameManager.ApplicaPenombra(cercaGO, 0.30f, 3f);

        Button cercaBtn = cercaGO.AddComponent<Button>();
        cercaBtn.targetGraphic = cercaImg;
        // "Cerca" e' l'unico bottone Twilight Indigo: e' l'accento scuro della UI e
        // stacca dagli altri pulsanti Tan. Su questo blu il nero e' 1.99:1, quindi
        // la label e' bianca (10.55:1) e resta bianca anche in hover/premuto.
        ColorBlock coloriCerca = cercaBtn.colors;
        coloriCerca.normalColor = GameManager.Indigo;
        coloriCerca.highlightedColor = GameManager.Indigo;
        coloriCerca.pressedColor = GameManager.Indigo;
        coloriCerca.selectedColor = GameManager.Indigo;
        coloriCerca.colorMultiplier = 1f;
        cercaBtn.colors = coloriCerca;

        GameObject labelGO = CrearGOFiglio(cercaGO.transform, "Label");
        RectTransform rtLabel = (RectTransform)labelGO.transform;
        rtLabel.anchorMin = Vector2.zero;
        rtLabel.anchorMax = Vector2.one;
        rtLabel.offsetMin = Vector2.zero;
        rtLabel.offsetMax = Vector2.zero;
        TextMeshProUGUI cercaLabel = labelGO.AddComponent<TextMeshProUGUI>();
        cercaLabel.text = "Cerca";
        cercaLabel.font = fontAsset;
        cercaLabel.fontSize = 18;
        cercaLabel.color = GameManager.TestoChiaro;
        cercaLabel.alignment = TextAlignmentOptions.Center;
        cercaLabel.raycastTarget = false;
        GameManager.ApplicaStileTesto(cercaLabel);

        // Etichetta a tre stati: bianca su Twilight Indigo, che non cambia colore tra
        // riposo/hover/premuto, quindi i tre stati sono identici come prima della
        // palette: serve solo a non farla diventare nera al passaggio del raggio.
        IndietroFeedback feedbackCerca = cercaGO.AddComponent<IndietroFeedback>();
        feedbackCerca.etichetta = cercaLabel;
        feedbackCerca.cambiaColoreEtichetta = true;
        feedbackCerca.coloreEtichettaBase = GameManager.TestoChiaro;
        feedbackCerca.coloreEtichettaHover = GameManager.TestoChiaro;
        feedbackCerca.coloreEtichettaPremuto = GameManager.TestoChiaro;
        feedbackCerca.ImpostaColori(GameManager.Indigo, GameManager.Indigo);
        feedbackCerca.ImpostaColorePremuto(GameManager.Indigo);
        feedbackCerca.fattoreHover = 1.12f;
        feedbackCerca.fattorePremuto = 1.06f;

        // Tastiera virtuale: in editor+Link TMP non puo' aprire la tastiera di sistema
        // della Quest, quindi digitiamo qui dentro. Diventa un figlio del contenitore
        // (in fondo, sotto l'elenco) e viene mostrata/nascosta dal pulsante Cerca.
        GameObject tastiera = CreaTastieraVirtuale(contenitore, input);
        tastiera.SetActive(false);

        cercaBtn.onClick.AddListener(() =>
        {
            // Appena si vuole cercare, fermiamo l'esecuzione in corso (colonne e
            // riproduzione) così non resta lo svolgimento di Osservatore/Seguimi.
            FermaRiproduzione();
            RilasciaIndietro();

            if (!inRicerca)
            {
                // ENTRA in modalità ricerca: nasconde i bottoni del DB, allarga
                // il gruppo e apre la tastiera.
                inRicerca = true;
                queryRicerca = "";
                contenitoreRicerca = contenitore;
                tastieraAttiva = tastiera;

                larghezzaOrigRicerca = contenitore.sizeDelta.x;
                posOrigRicerca = contenitore.anchoredPosition;
                indietroLocXOrig = LeggiIndietroX(contenitore);
                Transform el = contenitore.Find("ElencoBrani");
                elencoAttivo = el != null ? el.gameObject : null;
                if (elencoAttivo != null)
                {
                    LayoutElement leScorr = elencoAttivo.GetComponent<LayoutElement>();
                    if (leScorr != null) altezzaScorrRicerca = leScorr.preferredHeight;
                    elencoAttivo.SetActive(false);
                }

                // Tastiera piu' larga del gruppo base. Per non invadere il bottone
                // "Indietro" (in alto a sinistra), il pannello si allarga in modo
                // ASIMMETRICO: il bordo sinistro resta fermo e cresce solo a destra.
                // Indietro e' ancorato al centro del gruppo, quindi lo contro-sposto
                // per mantenerlo dov'e' (stessa posizione a schermo).
                float nuovaLarghezza = Mathf.Max(larghezzaOrigRicerca, LARGHEZZA_RICERCA);
                float deltaLocal = nuovaLarghezza - larghezzaOrigRicerca;
                float shiftCanvas = deltaLocal * 2f;
                contenitore.sizeDelta = new Vector2(nuovaLarghezza, contenitore.sizeDelta.y);
                contenitore.anchoredPosition = new Vector2(posOrigRicerca.x + shiftCanvas, posOrigRicerca.y);
                ImpostaIndietroX(contenitore, indietroLocXOrig - shiftCanvas / 4f);
                tastiera.SetActive(true);
                tastiera.transform.SetAsLastSibling();
                input.ActivateInputField();
                Debug.Log("[UDP] Ricerca aperta: elenco nascosto, tastiera mostrata.");
            }
            else
            {
                // GIA' in ricerca: con la tastiera chiusa la riapre per modificare il
                // testo (mostrando di nuovo i risultati sotto); con la tastiera aperta
                // fa quello che faceva il tasto OK: esegue la ricerca, oppure esce
                // dalla ricerca se il testo e' vuoto.
                if (!tastiera.activeSelf)
                {
                    tastiera.SetActive(true);
                    tastiera.transform.SetAsLastSibling();
                    if (elencoAttivo != null)
                    {
                        LayoutElement leScorr = elencoAttivo.GetComponent<LayoutElement>();
                        if (leScorr != null) leScorr.preferredHeight = 90f;
                        elencoAttivo.SetActive(true);
                        PortaScrollInCima(elencoAttivo.GetComponent<ScrollRect>());
                    }
                    input.ActivateInputField();
                    Debug.Log("[UDP] Ricerca: tastiera riaperta per modificare il testo (risultati mantenuti).");
                }
                else
                {
                    tastiera.SetActive(false);
                    if (string.IsNullOrWhiteSpace(input.text))
                    {
                        TerminaRicerca(true);
                    }
                    else
                    {
                        AvviaRicerca(input.text);
                    }
                    Debug.Log("[UDP] Ricerca: ricerca eseguita (tastiera chiusa).");
                }
            }
        });

        return root;
    }

    private TMP_InputField CreaInputField(Transform parent, bool isObserver)
    {
        GameObject campoGO = CrearGOFiglio(parent, "Input");
        LayoutElement leCampo = campoGO.AddComponent<LayoutElement>();
        leCampo.flexibleWidth = 1f;

        Image sfondo = campoGO.AddComponent<Image>();
        sfondo.sprite = campoSpr;
        sfondo.type = Image.Type.Sliced;
        sfondo.color = GameManager.SfondoScuro(0.95f);

        TMP_InputField input = campoGO.AddComponent<TMP_InputField>();
        input.targetGraphic = sfondo;

        GameObject ta = CrearGOFiglio(campoGO.transform, "TextArea");
        RectMask2D mask = ta.AddComponent<RectMask2D>();
        RectTransform rtTa = (RectTransform)ta.transform;
        rtTa.anchorMin = Vector2.zero;
        rtTa.anchorMax = Vector2.one;
        rtTa.offsetMin = new Vector2(6f, 4f);
        rtTa.offsetMax = new Vector2(-6f, -4f);

        // Placeholder
        GameObject phGO = CrearGOFiglio(ta.transform, "Placeholder");
        TextMeshProUGUI ph = phGO.AddComponent<TextMeshProUGUI>();
        RectTransform rtPh = (RectTransform)ph.transform;
        rtPh.anchorMin = Vector2.zero;
        rtPh.anchorMax = Vector2.one;
        rtPh.offsetMin = Vector2.zero;
        rtPh.offsetMax = Vector2.zero;
        ph.text = "";
        ph.font = fontAsset;
        ph.fontSize = 18;
        // Sul fondo scuro del campo il grigio di prima non si leggeva: bianco
        // leggermente attenuato, cosi' il testo digitato resta il primo piano.
        ph.color = GameManager.ConAlfa(GameManager.TestoChiaro, 0.60f);
        ph.raycastTarget = false;

        // Text
        GameObject txtGO = CrearGOFiglio(ta.transform, "Text");
        TextMeshProUGUI txt = txtGO.AddComponent<TextMeshProUGUI>();
        RectTransform rtTxt = (RectTransform)txt.transform;
        rtTxt.anchorMin = Vector2.zero;
        rtTxt.anchorMax = Vector2.one;
        rtTxt.offsetMin = Vector2.zero;
        rtTxt.offsetMax = Vector2.zero;
        txt.font = fontAsset;
        txt.fontSize = 18;
        txt.color = Color.white;
        txt.raycastTarget = true;

        input.textComponent = txt;
        input.placeholder = ph;
        input.textViewport = rtTa;
        input.fontAsset = fontAsset;
        input.pointSize = 18;

        input.onValueChanged.AddListener(AccodaSuggerimento);

        return input;
    }

    private GameObject CreaTastieraVirtuale(RectTransform contenitore, TMP_InputField input)
    {
        GameObject tastiera = new GameObject("TastieraVirtuale", typeof(RectTransform));
        tastiera.transform.SetParent(contenitore, false);

        RectTransform rt = (RectTransform)tastiera.transform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;

        // Partecipa al layout del contenitore: si posiziona in fondo, sotto l'elenco.
        LayoutElement le = tastiera.AddComponent<LayoutElement>();
        le.preferredHeight = 306f;

        Image sfondo = tastiera.AddComponent<Image>();
        sfondo.sprite = pannelloSpr;
        sfondo.type = Image.Type.Sliced;
        sfondo.color = GameManager.ConAlfa(GameManager.SteelBlue, 0.35f);

        VerticalLayoutGroup vlg = tastiera.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(4, 4, 4, 4);
        vlg.spacing = 10f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.UpperCenter;

        // Schermo readout: mostra live il testo digitato sulla tastiera.
        GameObject readGO = CrearGOFiglio(tastiera.transform, "Readout");
        LayoutElement leRead = readGO.AddComponent<LayoutElement>();
        leRead.preferredHeight = 38f;

        // Maschera: il testo non puo' mai uscire dai bordi del readout.
        readGO.AddComponent<RectMask2D>();

        Image readImg = readGO.AddComponent<Image>();
        readImg.sprite = pannelloSpr;
        readImg.type = Image.Type.Sliced;
        readImg.color = GameManager.ConAlfa(GameManager.SteelBlue, 0.35f);

        GameObject readLabelGO = CrearGOFiglio(readGO.transform, "Testo");
        RectTransform rtReadLabel = (RectTransform)readLabelGO.transform;
        rtReadLabel.anchorMin = Vector2.zero;
        rtReadLabel.anchorMax = Vector2.one;
        rtReadLabel.offsetMin = new Vector2(8f, 2f);
        rtReadLabel.offsetMax = new Vector2(-8f, -2f);
        TextMeshProUGUI readLabel = readLabelGO.AddComponent<TextMeshProUGUI>();
        readLabel.font = fontAsset;
        readLabel.fontSize = 20;
        // Il readout e' l'unico pezzo di testo sul pannello Steel Blue: bianco.
        readLabel.color = GameManager.TestoChiaro;
        readLabel.alignment = TextAlignmentOptions.Left;
        readLabel.enableWordWrapping = false;
        readLabel.overflowMode = TextOverflowModes.Ellipsis;
        readLabel.text = "Scrivi il brano";
        readLabel.raycastTarget = false;

        input.onValueChanged.AddListener((v) =>
        {
            readLabel.text = string.IsNullOrEmpty(v) ? "Scrivi il brano" : "> " + v;
        });

        CreaRigaTastiera(tastiera.transform, input, 42f, "A", "B", "C", "D", "E", "F");
        CreaRigaTastiera(tastiera.transform, input, 42f, "G", "H", "I", "J", "K", "L");
        CreaRigaTastiera(tastiera.transform, input, 42f, "M", "N", "O", "P", "Q", "R");
        CreaRigaTastiera(tastiera.transform, input, 42f, "S", "T", "U", "V", "W", "X");
        CreaRigaTastiera(tastiera.transform, input, 42f, "Y", "Z", "SP", "DEL", "AC", "BACK");

        return tastiera;
    }

    private void CreaRigaTastiera(Transform parent, TMP_InputField input, float altezza, params string[] tasti)
    {
        GameObject riga = CrearGOFiglio(parent, "RigaTastiera");

        HorizontalLayoutGroup hlg = riga.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6f;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = false;
        hlg.padding = new RectOffset(0, 0, 0, 0);
        hlg.childAlignment = TextAnchor.MiddleCenter;

        LayoutElement leRiga = riga.AddComponent<LayoutElement>();
        leRiga.preferredHeight = altezza;

        // Ogni riga ha lo stesso numero di tasti (6): tutti con la stessa
        // flessibilita', così ogni casella ha la stessa dimensione e riempie la riga.
        foreach (string t in tasti)
        {
            string tasto = t;

            GameObject te = CrearGOFiglio(riga.transform, "Tasto_" + tasto);

            LayoutElement le = te.AddComponent<LayoutElement>();
            le.preferredWidth = 0f;
            le.flexibleWidth = 1f;
            le.preferredHeight = altezza;

            Image img = te.AddComponent<Image>();
            img.sprite = bottoneSpr;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 8f;
img.color = GameManager.TanColor;
            GameManager.ApplicaPenombra(te, 0.30f, 3f);

            Button btn = te.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;

            // Feedback visivo: ingrandisce e cambia colore al passaggio/pressione.
            te.AddComponent<KeyFeedback>();
            GlowBottone.Applica(te);

            GameObject labGO = CrearGOFiglio(te.transform, "Label");
            RectTransform rtLab = (RectTransform)labGO.transform;
            rtLab.anchorMin = Vector2.zero;
            rtLab.anchorMax = Vector2.one;
            rtLab.offsetMin = Vector2.zero;
            rtLab.offsetMax = Vector2.zero;
            TextMeshProUGUI lab = labGO.AddComponent<TextMeshProUGUI>();
            lab.text = tasto;
            lab.font = fontAsset;
            lab.fontSize = 20f;
            lab.fontSizeMin = 12f;
            lab.fontSizeMax = 20f;
            lab.enableAutoSizing = true;
            lab.color = Color.black;
            lab.alignment = TextAlignmentOptions.Center;
            lab.enableWordWrapping = false;
            lab.overflowMode = TextOverflowModes.Overflow;
            GameManager.ApplicaStileTesto(lab);

            btn.onClick.AddListener(() => GestisciTastoTastiera(input, tasto, te));
        }
    }

    private void GestisciTastoTastiera(TMP_InputField input, string tasto, GameObject tastoGO)
    {
        if (input == null) return;

        // Qualsiasi tasto della tastiera virtuale pulisce il bottone "Indietro".
        RilasciaIndietro();

        switch (tasto)
        {
            case "BACK":
                {
                    Transform t = tastoGO.transform;
                    if (t.parent != null && t.parent.parent != null)
                        t.parent.parent.gameObject.SetActive(false);
                    TerminaRicerca(true);
                }
                break;
            case "DEL":
                if (input.text.Length > 0)
                    input.text = input.text.Substring(0, input.text.Length - 1);
                break;
            case "AC":
                input.text = "";
                break;
            case "SP":
                input.text += " ";
                break;
            default:
                input.text += tasto;
                break;
        }

        if (input.textComponent != null) input.MoveTextEnd(false);
        input.ActivateInputField();
    }

    private void CreaMessaggio(RectTransform elenco, string testo, List<GameObject> listaTarget)
    {
        GameObject msgGO = CrearGOFiglio(elenco, "Messaggio");
        RectTransform rt = (RectTransform)msgGO.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(0f, 36f);

        LayoutElement le = msgGO.AddComponent<LayoutElement>();
        le.preferredHeight = 36f;

        TextMeshProUGUI lab = msgGO.AddComponent<TextMeshProUGUI>();
        lab.text = testo;
        lab.font = fontAsset;
        lab.fontSize = 18;
        // Messaggi di stato ("nessun risultato", "nessuna song"): bianchi come il resto
        // delle scritte su sfondo scuro.
        lab.color = GameManager.TestoChiaro;
        lab.alignment = TextAlignmentOptions.Center;
        lab.raycastTarget = false;

        listaTarget.Add(msgGO);
    }

    private void CreaBottoneBrano(RectTransform elenco, string titolo, string autore, int id,
                                  List<GameObject> listaTarget, bool nuovoRisultato)
    {
        GameObject bottoneGO = CrearGOFiglio(elenco, "BottoneRisultato");

        RectTransform rt = (RectTransform)bottoneGO.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(0f, 40f);

        LayoutElement le = bottoneGO.AddComponent<LayoutElement>();
        le.preferredHeight = 40f;

        Image img = bottoneGO.AddComponent<Image>();
        img.sprite = bottoneSpr;
        img.type = Image.Type.Sliced;
        img.color = GameManager.TanColor;
        GameManager.ApplicaPenombra(bottoneGO, 0.30f, 3f);

        Button btn = bottoneGO.AddComponent<Button>();
        btn.targetGraphic = img;
        ColorBlock colori = btn.colors;
        colori.highlightedColor = GameManager.DustyRose;
        colori.pressedColor = GameManager.DustyRose;
        btn.colors = colori;

        // Hover manuale (niente ColorTint): evita che lo scroll illumini tutte le canzoni.
        IndietroFeedback feedbackBrano = bottoneGO.AddComponent<IndietroFeedback>();

        GameObject labelGO = CrearGOFiglio(bottoneGO.transform, "Testo");
        RectTransform rtLabel = (RectTransform)labelGO.transform;
        rtLabel.anchorMin = Vector2.zero;
        rtLabel.anchorMax = Vector2.one;
        rtLabel.offsetMin = new Vector2(8f, 2f);
        rtLabel.offsetMax = new Vector2(-8f, -2f);
        TextMeshProUGUI label = labelGO.AddComponent<TextMeshProUGUI>();
        label.font = fontAsset;
        label.fontSize = 14;
        label.color = Color.black;
        label.alignment = TextAlignmentOptions.Left;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
        GameManager.ApplicaStileTesto(label);

        feedbackBrano.etichetta = label;
        feedbackBrano.cambiaColoreEtichetta = true;
        // Label nera su Tan e anche su Dusty Rose: il nero e' l'unico colore che
        // regge su entrambi i fondi (11.21:1 e 6.10:1).
        feedbackBrano.coloreEtichettaBase = Color.black;
        feedbackBrano.coloreEtichettaHover = Color.black;
        feedbackBrano.coloreEtichettaPremuto = Color.black;
        feedbackBrano.ImpostaColorePremuto(GameManager.DustyRose);

        BranoDinamicoUI dinamico = bottoneGO.AddComponent<BranoDinamicoUI>();
        dinamico.ImpostaBrano(titolo, autore, id, nuovoRisultato);

        listaTarget.Add(bottoneGO);
    }

    private GameObject CrearGOFiglio(Transform parent, string nome)
    {
        GameObject go = new GameObject(nome, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }
}