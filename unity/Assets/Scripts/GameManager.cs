using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.EventSystems;

public class GameManager : MonoBehaviour
{
    public enum AppState { Onboarding, Menu, Tutorial, Osservatore, Seguimi, FaiTu }

    [Header("Stato dell'Applicazione")]
    public AppState statoAttuale = AppState.Onboarding;

    [Header("Riferimenti UI Gruppi")]
    public GameObject calibrationGroup;
    public GameObject menuGroup;
    public GameObject tutorialGroup;
    public GameObject observerGroup;
    public GameObject seguimiGroup;
    public GameObject faiTuGroup;

    [Header("UI Specifica Modalità Fai Tu")]
    public GameObject faiTuBannerMessaggio;
    public GameObject faiTuReportMessaggio;

    [Header("Riferimenti UI Testi")]
    public TextMeshProUGUI tutorialText;

    private UdpReceiver udpReceiver;
    private float tempoUltimoRilascio = -1f;
    private int ultimaNotaMidi = -1;
    private float ultimaVelocita = 0f;
    public int sfidaAttuale = 0;

    // Anti-rimbalzo per il Tutorial: tiene traccia delle note realmente premute.
    // Una pressa forte può generare due note_on ravvicinati (sensore/martelletti):
    // il secondo viene ignorato e il conteggio resta affidabile (contatore derivato
    // dal set, non da incrementi -> auto-riparante anche con sovrapposizioni).
    private readonly HashSet<int> tastiTutorialPremuti = new HashSet<int>();
    // Prima nota della sfida staccato corrente (guardia anti auto-trigger).
    private int primaNotaStaccato = -1;
    // Contatore delle note della "Scala dinamica crescente" (SFIDA 2): servono
    // esattamente 4 note in successione e sempre piu' forti per concludere.
    private int noteScalaCrescente = 0;
    private float tempoUltimaNotaScala = -1f;
    // True mentre il bridge sta riproducendo l'esempio dimostrativo della sfida.
    private bool esempioInCorso = false;
    // True quando il bridge ha confermato ("example_done") la fine dell'esempio:
    // distingue "riprodotto" da "comando perso, devo ritentare".
    private bool esempioCompletato = false;
    // "clear_scene" ricevuto dal bridge mentre l'esempio era ancora in riproduzione:
    // lo rimandiamo alla fine, altrimenti la colonna dell'esempio viene distrutta
    // nello stesso frame in cui nasce (e non si vede nulla).
    private bool clearSceneInAttesa = false;
    // Coroutine dell'esempio/sfida corrente del Tutorial: fermata se si abbandona
    // o si riapre il tutorial (evita coroutine orfane che bloccano la rilevazione).
    private Coroutine tutorialCoroutine = null;

    // Gestione Note per la modalità "Seguimi"
    private List<int> expectedNotes = new List<int>();
    private HashSet<int> userPressedNotes = new HashSet<int>();

    private bool inTransizione = false;
    private Coroutine faiTuBannerCoroutine;
    private Coroutine reportFeedbackCoroutine;
    // Pannello di spiegazione mostrato all'ingresso di Tutorial/Osservatore/Seguimi.
    // Nato a runtime (il banner FaiTu vive solo nella scena) e con lo stesso
    // aspetto: Steel Blue, sprite arrotondata, ombra e testo bianco.
    private GameObject bannerModalita;
    private TextMeshProUGUI bannerModalitaTesto;
    private Coroutine bannerModalitaCoroutine;
    // Visualizzatore PDF del report: creato a runtime sotto il Canvas, con le
    // pagine salvate in PNG dal bridge nella cartella sessione (app e bridge
    // girano sulla stessa macchina via Link). Vive anche lui fuori dai gruppi,
    // perche' i gruppi Osservatore/Seguimi sono in scala 4.
    private GameObject viewerReport;
    private RawImage paginaReportImmagine;
    private List<Texture2D> pagineReport = new List<Texture2D>();
    private int paginaReportCorrente = 0;
    // Bottoni del viewer catturati al momento della creazione: servono per nasconderli
    // quando non servono (Indietro sulla prima pagina, Avanti sull'ultima).
    private Button bottoneIndietroReport;
    private Button bottoneAvantiReport;

    // "Genera Report" vive ora nel menu (spostato a runtime da GruppoFaiTu) e resta
    // spento finche' il bridge non confirma che c'e' materiale (note Fai Tu o
    // esecuzioni Seguimi registrate).
    private Button bottoneGeneraReport;
    private bool reportDisponibile = false;

    // Sprite e materiale condivisi da TUTTI i bottoni (forma e aspetto uniformi).
    // Vengono ricavati dai bottoni statici della scena (menu/FaiTu) che usano la
    // sprite UI arrotondata built-in e il materiale di testo con ombra.
    private static Sprite sprArrotondato;
    private static Material matOmbra;

    // --- PALETTE (Steel Blue / Tan / Dusty Rose) ---
    // Unico insieme di colori per l'intera UI: pulsanti, pannelli, testi.
    public static readonly Color SteelBlue = new Color(0.4275f, 0.5961f, 0.7294f, 1f);  // #6d98ba
    public static readonly Color TanColor = new Color(0.8275f, 0.7255f, 0.6235f, 1f);   // #d3b99f
    public static readonly Color DustyRose = new Color(0.7569f, 0.4667f, 0.4039f, 1f);  // #c17767

    // Neutro scuro: e' la tonalita' notte dello stesso blu Steel Blue, non un colore
    // nuovo. Serve per i testi sugli sfondi chiari Tan (nome dell'autore) e per il
    // fondo del campo di ricerca.
    public static readonly Color Inchiostro = new Color(0.1490f, 0.2039f, 0.3216f, 1f); // #263452

    // Verde "brano scaricato" sulle righe Tan: serve SCURO (4.74:1), il verde chiaro
    // #65b891 di prima era 1.27:1, cioe' illeggibile. Resta separato da
    // SuccessoTutorial perche' i due verdi hanno usi (e fondi) diversi.
    public static readonly Color Successo = new Color(0.1020f, 0.3294f, 0.2078f, 1f);   // #1a5435

    // Twilight Indigo: bottone "Cerca", l'unico elemento scuro della UI a pieno.
    // Il nero su questo blu e' 1.99:1 (illeggibile), quindi la label e' bianca
    // (10.55:1).
    public static readonly Color Indigo = new Color(0.2510f, 0.2157f, 0.4314f, 1f);     // #40376e

    // Shamrock e Darl Amaranth: colori delle transizioni del tutorial (COMPLETATO,
    // ECCELLENTE, errori). ATTENZIONE: il pannello del tutorial e' Steel Blue e i due
    // colori hanno la stessa luminanza del pannello, quindi il verde Shamrock li e'
    // 1.04:1, invisibile, mentre il rosso arriva a 3.23:1. Sono scelti cosi' per
    // richiesta esplicita: il testo bianco che segue ("Ottimo lavoro! Rileggi
    // bene...") resta leggibile e porta comunque il messaggio. Per tornare al
    // verde leggibile basta sostituire SuccessoTutorial con TestoChiaro.
    public static readonly Color SuccessoTutorial = new Color(0.3020f, 0.6314f, 0.4039f, 1f); // #4da167 Shamrock
    public static readonly Color ErroreTutorial = new Color(0.4902f, 0.1137f, 0.2471f, 1f);   // #7d1d3f Darl Amaranth

    // Testo sopra i pannelli Steel Blue: su un tono medio il massimo contrasto
    // possibile e' il bianco (3.07:1). Tan sul pannello sarebbe 1.64:1 (invisibile),
    // Dusty Rose 1.12:1. Va bene perche' le scritte della UI sono molto grandi.
    public static readonly Color TestoChiaro = new Color(1f, 1f, 1f, 1f);

    // Alone dei pulsanti Tan: sul nero della camera il blu Steel Blue si stacca
    // nettamente (6.85:1).
    public static readonly Color GlowColore = SteelBlue;

    // Fondo scuro dei campi di testo.
    public static Color SfondoScuro(float alfa) => new Color(Inchiostro.r, Inchiostro.g, Inchiostro.b, alfa);

    // Stesso colore RGB con un alfa diverso: serve per i pannelli semitrasparenti
    // (elenco, tastiera, traccia della scrollbar).
    public static Color ConAlfa(Color c, float alfa) => new Color(c.r, c.g, c.b, alfa);

    // Per i <color=#...> inline di TextMeshPro: la stringa non puo' interpolare
    // direttamente un Color, quindi si passa dall'esadecimale.
    //
    // La conversione e' scritta a mano invece di delegarla a ColorUtility perche'
    // i Color hanno float con 4 decimali, che raddoppiati per 255 non cadono mai
    // su un intero esatto: 0.4039f * 255 = 102.9945 e troncando diventerebbe 0x66
    // invece di 0x67, così Shamrock uscirebbe #4da166 invece di #4da167. Qui si
    // arrotonda al byte piu' vicino, che e' sempre l'esadecimale scritto nella
    // definizione del colore.
    public static string Hex(Color c) => $"{CanaleHex(c.r)}{CanaleHex(c.g)}{CanaleHex(c.b)}";

    private static string CanaleHex(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255).ToString("X2");

    // --- FONT ---
    // Un'unica famiglia: Raleway (Resources/Fonts/Raleway SDF, atlas dinamica).
    private static TMP_FontAsset fontApp;
    private static bool fontCercato;

    public static TMP_FontAsset FontApp
    {
        get
        {
            if (!fontCercato)
            {
                fontCercato = true;
                fontApp = TrovaFontConPriorita("raleway");
            }
            return fontApp;
        }
    }

    private static TMP_FontAsset TrovaFontConPriorita(params string[] nomiCercati)
    {
        TMP_FontAsset[] fontAssets = Resources.LoadAll<TMP_FontAsset>("Fonts");
        foreach (string cercato in nomiCercati)
        {
            foreach (TMP_FontAsset fa in fontAssets)
                if (fa != null && fa.name.ToLower().Contains(cercato)) return fa;
        }
        if (fontAssets.Length > 0 && fontAssets[0] != null) return fontAssets[0];

        Font[] fonts = Resources.LoadAll<Font>("Fonts");
        if (fonts.Length > 0 && fonts[0] != null)
        {
            TMP_FontAsset dinamico = TMP_FontAsset.CreateFontAsset(fonts[0]);
            if (dinamico != null) return dinamico;
        }

        return TMP_Settings.defaultFontAsset;
    }

    // I titoli sono riconosciuti per nome. In scena alcuni hanno uno spazio finale
    // ("Testo Titolo "): il confronto deve normalizzarlo, altrimenti restano esclusi.
    private static bool ETitolo(TextMeshProUGUI t)
    {
        return t != null && t.name != null && t.name.Trim() == "Testo Titolo";
    }

    public static Sprite SpriteArrotondato
    {
        get
        {
            if (sprArrotondato == null) sprArrotondato = TrovaSpriteArrotondata();
            return sprArrotondato;
        }
    }

    public static Material MaterialeTesto
    {
        get
        {
            if (matOmbra == null)
            {
                matOmbra = CreaMaterialeOmbra(FontApp);
                if (matOmbra == null) matOmbra = TrovaMaterialeOmbra();
            }
            return matOmbra;
        }
    }

    // Materiale con ombra (underlay) costruito sul font attivo: clona il materiale
    // del font e attiva l'underlay.
    private static Material CreaMaterialeOmbra(TMP_FontAsset font)
    {
        if (font == null || font.material == null) return null;

        Material m = new Material(font.material);
        m.name = font.name + " Drop Shadow";
        m.EnableKeyword("UNDERLAY_ON");
        m.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.5f));
        m.SetFloat("_UnderlayOffsetX", 0.4f);
        m.SetFloat("_UnderlayOffsetY", -0.4f);
        m.SetFloat("_UnderlayDilate", 0.1f);
        m.SetFloat("_UnderlaySoftness", 0.15f);
        return m;
    }

    private static Sprite TrovaSpriteArrotondata()
    {
        Image[] immagini = FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // 1) La sprite di un bottone "Torna al Menù"/"Indietro" statico: e' il
        //    riferimento visivo che l'utente confronta con quello del tutorial.
        string[] nomiTorna = { "TornaAlMen\u00F9", "Bottone-Indietro", "GeneraReport" };
        foreach (Image img in immagini)
        {
            if (img.sprite == null) continue;
            foreach (string nome in nomiTorna)
            {
                if (img.gameObject.name == nome) return img.sprite;
            }
        }

        // 2) Lo sfondo di un Button qualsiasi.
        Button[] bottoni = FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (Button b in bottoni)
        {
            Image im = b.targetGraphic as Image;
            if (im != null && im.sprite != null && im.sprite.name == "UISprite") return im.sprite;
        }
        foreach (Button b in bottoni)
        {
            Image im = b.targetGraphic as Image;
            if (im != null && im.sprite != null && im.sprite.border != Vector4.zero) return im.sprite;
        }

        // 3) Qualunque Image con una sprite arrotondata.
        foreach (Image img in immagini)
        {
            if (img.sprite != null && img.sprite.name == "UISprite") return img.sprite;
        }
        foreach (Image img in immagini)
        {
            if (img.sprite != null && img.sprite.border != Vector4.zero) return img.sprite;
        }
        return null;
    }

    private static Material TrovaMaterialeOmbra()
    {
        TextMeshProUGUI[] testi = FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (TextMeshProUGUI t in testi)
        {
            Material m = t.fontSharedMaterial;
            if (m != null && m.name.Contains("Drop Shadow")) return m;
        }
        return null;
    }

    // Applica a una label lo stile uniforme: Raleway in grassetto + ombra. Essendo
    // un unico font, anche i titoli restano in grassetto (la gerarchia la fa' la
    // dimensione, non il peso) e l'ombra e' quella del font attivo.
    public static void ApplicaStileTesto(TextMeshProUGUI label)
    {
        if (label == null) return;

        if (FontApp != null) label.font = FontApp;
        label.fontStyle = FontStyles.Bold;
        if (MaterialeTesto != null) label.fontSharedMaterial = MaterialeTesto;
    }

    // Ombra morbida sotto al bottone (il rettangolo del bottone non cambia, quindi
    // non serve aggiungere un'immagine a parte). Segue anche la scala dell'hover,
    // essendo un componente dello stesso GameObject.
    public static void ApplicaPenombra(GameObject go, float alfa = 0.30f, float distanza = 3f)
    {
        if (go == null) return;
        Shadow ombra = go.GetComponent<Shadow>();
        if (ombra == null) ombra = go.AddComponent<Shadow>();
        ombra.effectColor = new Color(0f, 0f, 0f, alfa);
        ombra.effectDistance = new Vector4(0f, -distanza, 0f, 0f);
        ombra.useGraphicAlpha = true;
    }

    private void Awake()
    {
        ConfiguraStatoIniziale();
    }

    void Start()
    {
        Application.targetFrameRate = 72;
        udpReceiver = FindFirstObjectByType<UdpReceiver>();
        // Se il bridge si dichiara pronto mentre siamo gia' nel menu (es. il primo
        // "get_report_status" all'avvio era caduto pre-prontezza), rivalidiamo il
        // bottone "Genera Report" senza dover uscire e rientrare nel menu.
        if (udpReceiver != null) udpReceiver.BridgeProntoRicevuto += OnBridgePronto;
        CreaBottoneHomeTutorial();
        SpostaGeneraReportInMenu();
        IngrandisciBottoniUI();
        SistemaTestiUI();

        GameObject raggioGO = new GameObject("RaggioPuntatore");
        raggioGO.transform.SetParent(this.transform, false);
        raggioGO.AddComponent<RaggioPuntatore>();

        StartCoroutine(UniformaBottoniTornaDopoFrame());
    }

    private void OnDestroy()
    {
        if (udpReceiver != null) udpReceiver.BridgeProntoRicevuto -= OnBridgePronto;
    }

    private void OnBridgePronto()
    {
        if (statoAttuale != AppState.Menu || udpReceiver == null) return;
        ApplicaAspettoGeneraReport();
        udpReceiver.InviaComandoRichiediStatoReport();
    }

    private void SistemaTestiUI()
    {
        if (tutorialText != null)
        {
            RectTransform rtTutorial = tutorialText.transform as RectTransform;
            if (rtTutorial != null)
            {
                rtTutorial.anchorMin = new Vector2(0.5f, 0.5f);
                rtTutorial.anchorMax = new Vector2(0.5f, 0.5f);
                rtTutorial.pivot = new Vector2(0.5f, 0.5f);
                rtTutorial.anchoredPosition = Vector2.zero;
                rtTutorial.sizeDelta = new Vector2(440f, 360f);
            }
            tutorialText.alignment = TextAlignmentOptions.Center;
            tutorialText.enableWordWrapping = true;
        }

        if (faiTuReportMessaggio != null)
        {
            TextMeshProUGUI reportText = faiTuReportMessaggio.GetComponent<TextMeshProUGUI>();
            if (reportText != null)
                reportText.text = "Report Generato!\n\nLo trovi nella cartella Sessioni";
        }

        // Il banner FaiTu della scena ha il testo a y=65 (400x200): lo riequilibro
        // verso il centro (y=20) a runtime, come quelli creati in codice, senza mai
        // modificare la scena. E' il figlio TextMeshPro del banner.
        if (faiTuBannerMessaggio != null)
        {
            TextMeshProUGUI bannerText = faiTuBannerMessaggio.GetComponentInChildren<TextMeshProUGUI>(true);
            if (bannerText != null)
            {
                RectTransform rtBanner = bannerText.transform as RectTransform;
                if (rtBanner != null)
                    rtBanner.anchoredPosition = new Vector2(rtBanner.anchoredPosition.x, 20f);
            }
        }
    }

    // Rende il bottone "Torna al Menù" identico in tutte le modalità: copia lo
    // stile di quello di FaiTu (gruppo x1, riferimento) su Tutorial/Osservatore/
    // Seguimi. Aspetta un frame perché SongListManager posiziona e aggancia i suoi
    // "Bottone-Indietro" nel proprio Start().
    private IEnumerator UniformaBottoniTornaDopoFrame()
    {
        yield return null;
        UniformaBottoniTorna();
        ApplicaPalette();
        ApplicaFontGlobale();
    }

    private void UniformaBottoniTorna()
    {
        if (faiTuGroup == null) return;
        Transform rif = faiTuGroup.transform.Find("TornaAlMen\u00F9");
        if (rif == null) return;

        if (tutorialGroup != null)
        {
            Transform t = tutorialGroup.transform.Find("TornaAlMenu");
            CopiaStileBottoneTorna(t, rif);

            // Nel tutorial il testo va su due righe esplicite ("Torna al" / "Menù").
            TextMeshProUGUI txtTutorial = t != null
                ? t.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (txtTutorial != null) txtTutorial.text = "Torna al\nMen\u00F9";
        }

        CopiaStileBottoneTorna(TrovaFiglio(observerGroup, "Bottone-Indietro"), rif);
        CopiaStileBottoneTorna(TrovaFiglio(seguimiGroup, "Bottone-Indietro"), rif);
    }

    private static Transform TrovaFiglio(GameObject gruppo, string nome)
    {
        if (gruppo == null) return null;
        for (int i = 0; i < gruppo.transform.childCount; i++)
        {
            Transform f = gruppo.transform.GetChild(i);
            if (f.name == nome) return f;
        }
        return null;
    }

    // Copia forma ed etichetta del bottone di riferimento sul target, compensando
    // la diversa scala dei gruppi (FaiTu x1, Osservatore/Seguimi x4). NON copia i
    // colori: la palette viene applicata a parte da ApplicaPalette().
    private static void CopiaStileBottoneTorna(Transform target, Transform riferimento)
    {
        if (target == null || riferimento == null) return;

        float scalaRif = riferimento.lossyScale.x;
        float comp = (scalaRif != 0f) ? target.lossyScale.x / scalaRif : 1f;
        if (comp <= 0f) comp = 1f;

        Image imgRif = riferimento.GetComponent<Image>();
        Image imgDst = target.GetComponent<Image>();
        if (imgRif != null && imgDst != null)
        {
            imgDst.sprite = imgRif.sprite;
            imgDst.type = imgRif.type;
            imgDst.material = imgRif.material;
            imgDst.pixelsPerUnitMultiplier = imgRif.pixelsPerUnitMultiplier * comp;
        }

        TextMeshProUGUI txtRif = riferimento.GetComponentInChildren<TextMeshProUGUI>(true);
        TextMeshProUGUI txtDst = target.GetComponentInChildren<TextMeshProUGUI>(true);
        if (txtRif != null && txtDst != null)
        {
            txtDst.font = txtRif.font;
            txtDst.fontSharedMaterial = txtRif.fontSharedMaterial;
            txtDst.fontStyle = txtRif.fontStyle;
            txtDst.alignment = txtRif.alignment;
            txtDst.text = txtRif.text;
            txtDst.fontSize = txtRif.fontSize / comp;
        }
    }

    // Applica la palette ai bottoni statici, ai pannelli e ai titoli.
    private void ApplicaPalette()
    {
        // Bottoni del menu, Riancora, "Torna al Menù" e Genera Report:
        // Tan base, Dusty Rose (hover/pressione), testo nero.
        StileBottone(TrovaFiglio(menuGroup, "Bottone Tutorial"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(menuGroup, "Bottone Osservatore"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(menuGroup, "Bottone Seguimi"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(menuGroup, "Bottone Fai Tu"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(menuGroup, "BottoneRiancora"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(menuGroup, "GeneraReport"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(faiTuGroup, "TornaAlMen\u00F9"), TanColor, DustyRose, DustyRose, Color.black);
        StileBottone(TrovaFiglio(tutorialGroup, "TornaAlMenu"), TanColor, DustyRose, DustyRose, Color.black);

        // "Indietro" di Osservatore/Seguimi: gestiti a mano da IndietroFeedback.
        StileIndietro(TrovaFiglio(observerGroup, "Bottone-Indietro"));
        StileIndietro(TrovaFiglio(seguimiGroup, "Bottone-Indietro"));

        // Pannelli "Sfondo" di Tutorial e del banner FaiTu: Steel Blue, con i testi
        // resi chiari (su un pannello Steel Blue il bianco e' l'unico leggibile).
        ColoraPannelli(tutorialGroup);
        ColoraPannelli(faiTuGroup);
        ColoraTestiChiariPannelli(tutorialGroup);
        ColoraTestiChiariPannelli(faiTuGroup);

        // "Report Generato / Lo trovi nella cartella Sessioni" e' l'unico testo di
        // FaiTu che NON sta su un pannello (ConfermaReport non ha sfondo): resta
        // quindi sul nero della camera, dove il blu Steel Blue e' ben leggibile
        // (6.85:1). Va reimposto DOPO ColoraTestiChiariPannelli, che lo renderebbe
        // bianco insieme a tutto il resto del gruppo.
        TextMeshProUGUI reportText = faiTuReportMessaggio != null
            ? faiTuReportMessaggio.GetComponent<TextMeshProUGUI>() : null;
        if (reportText != null) reportText.color = SteelBlue;

        // Titoli dei gruppi: bianchi (stanno su fondi diversi, nero o pannelli
        // semitrasparenti: il bianco e' l'unico che regge su entrambi).
        ColoraTitoli(menuGroup);
        ColoraTitoli(observerGroup);
        ColoraTitoli(seguimiGroup);

        ApplicaFontSizeMenu();
    }

    // Corpus dei bottoni di menu/FaiTu: un gradino piu' piccolo, perche' le
    // etichette sono brevi e a font piu' grande occupavano troppo la faccia del
    // pulsante. I valori stanno qui e non in scena cosi' restano un unico punto
    // da modificare per tutta la UI (le label di scena restano il default).
    private const float FS_MENU = 42f;          // i 4 bottoni del menu
    private const float FS_RIPRESA = 60f;       // Riancora Pianoforte + Torna al Menu
    // I "Bottone-Indietro" di Osservatore/Seguimi vivono in gruppi scalati x4:
    // per apparire uguali al "Torna al Menu" (x1) il valore locale deve essere
    // FS_RIPRESA / 4.
    private const float FS_INDIETRO = FS_RIPRESA / 4f;

    private void ApplicaFontSizeMenu()
    {
        ImpostaFontSizeFigli(menuGroup, "Bottone Tutorial", FS_MENU);
        ImpostaFontSizeFigli(menuGroup, "Bottone Osservatore", FS_MENU);
        ImpostaFontSizeFigli(menuGroup, "Bottone Seguimi", FS_MENU);
        ImpostaFontSizeFigli(menuGroup, "Bottone Fai Tu", FS_MENU);
        ImpostaFontSizeFigli(menuGroup, "BottoneRiancora", FS_RIPRESA);

        // Riferimento di tutti i "Torna al Menu": CopiaStileBottoneTorna() deriva
        // da questo anche Tutorial e i due "Bottone-Indietro".
        ImpostaFontSizeFigli(faiTuGroup, "TornaAlMen\u00F9", FS_RIPRESA);

        // "Genera Report" e' nel menu: stessa dimensione degli altri bottoni.
        ImpostaFontSizeFigli(menuGroup, "GeneraReport", FS_MENU);

        // CopiaStileBottoneTorna() gira PRIMA di questo metodo e copierebbe quindi
        // il valore di scena (72): va reimposto anche il bottone del tutorial.
        ImpostaFontSizeFigli(tutorialGroup, "TornaAlMenu", FS_RIPRESA);

        ImpostaFontSizeFigli(observerGroup, "Bottone-Indietro", FS_INDIETRO);
        ImpostaFontSizeFigli(seguimiGroup, "Bottone-Indietro", FS_INDIETRO);
    }

    private static void ImpostaFontSizeFigli(GameObject gruppo, string nome, float size)
    {
        Transform bottone = TrovaFiglio(gruppo, nome);
        if (bottone == null) return;
        TextMeshProUGUI txt = bottone.GetComponentInChildren<TextMeshProUGUI>(true);
        if (txt != null) txt.fontSize = size;
    }

    private static void StileBottone(Transform target, Color baseC, Color hoverC, Color pressedC, Color testo)
    {
        if (target == null) return;

        Image img = target.GetComponent<Image>();
        if (img != null) img.color = baseC;

        Button btn = target.GetComponent<Button>();
        if (btn != null)
        {
            ColorBlock c = btn.colors;
            c.normalColor = baseC;
            c.highlightedColor = hoverC;
            c.pressedColor = pressedC;
            c.selectedColor = hoverC;
            c.disabledColor = baseC;
            c.colorMultiplier = 1f;
            btn.colors = c;
            // Colore e scala sono gestiti a mano da IndietroFeedback: evitiamo che
            // la ColorTint lasci l'highlight "appeso" e che colori il bottone per
            // un frame quando la transizione verra' disattivata.
            btn.transition = Selectable.Transition.None;
        }

        // Hover manuale (scala + colore) come su Indietro/Cerca.
        IndietroFeedback fb = target.GetComponent<IndietroFeedback>();
        if (fb == null) fb = target.gameObject.AddComponent<IndietroFeedback>();
        fb.ImpostaColori(baseC, hoverC);
        fb.ImpostaColorePremuto(pressedC);
        fb.fattoreHover = 1.12f;
        fb.fattorePremuto = 1.06f;

        TextMeshProUGUI label = target.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.color = testo;

        ApplicaPenombra(target.gameObject);
        GlowBottone.Applica(target.gameObject);
    }

    private static void StileIndietro(Transform target)
    {
        if (target == null) return;

        Image img = target.GetComponent<Image>();
        if (img != null) img.color = TanColor;

        Button btn = target.GetComponent<Button>();
        if (btn != null)
        {
            ColorBlock c = btn.colors;
            c.highlightedColor = DustyRose;
            c.pressedColor = DustyRose;
            btn.colors = c;
        }

        IndietroFeedback fb = target.GetComponent<IndietroFeedback>();
        if (fb != null)
        {
            fb.ImpostaColori(TanColor, DustyRose);
            fb.fattoreHover = 1.12f;
            fb.fattorePremuto = 1.06f;
        }

        TextMeshProUGUI label = target.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.color = Color.black;

        ApplicaPenombra(target.gameObject);
        GlowBottone.Applica(target.gameObject);
    }

    // Colore "spento" del bottone Genera Report senza materiale: la stessa Tan
    // schiarita verso il bianco, cosi' resta nella palette ma non sembra attivo.
    private static readonly Color SpentoReport = new Color(0.8941f, 0.8353f, 0.7725f, 1f); // #e5d5c6

    // Chiamato da UdpReceiver quando il bridge risponde a "get_report_status":
    // true = c'e' materiale (note Fai Tu o esecuzioni Seguimi) -> bottone attivo.
    public void StatoReportRicevuto(bool hasMaterial)
    {
        reportDisponibile = hasMaterial;
        ApplicaAspettoGeneraReport();
    }

    // Aspetto del bottone "Genera Report" nel menu: Tan pieno ed interattivo con
    // materiale, Tan schiarita e non cliccabile senza. IndietroFeedback disabilitato
    // per evitare hover/grow, glow nascosto e etichetta attenuata.
    private void ApplicaAspettoGeneraReport()
    {
        if (bottoneGeneraReport == null) return;

        Image img = bottoneGeneraReport.GetComponent<Image>();
        TextMeshProUGUI label = bottoneGeneraReport.GetComponentInChildren<TextMeshProUGUI>(true);
        IndietroFeedback fb = bottoneGeneraReport.GetComponent<IndietroFeedback>();

        if (reportDisponibile)
        {
            bottoneGeneraReport.interactable = true;
            if (fb != null) fb.enabled = true;
            if (img != null) img.color = TanColor;
            if (label != null) label.color = Color.black;
            SetGlowGeneraReport(true);
        }
        else
        {
            bottoneGeneraReport.interactable = false;
            // Prima disattivo il feedback: il suo OnDisable riporta l'Image al colore
            // di riposo (Tan) e va chiamato PRIMA di applicare il colore spento.
            if (fb != null) fb.enabled = false;
            if (img != null) img.color = SpentoReport;
            if (label != null) label.color = new Color(0f, 0f, 0f, 0.55f);
            SetGlowGeneraReport(false);
        }
    }

    // Il glow del bottone e' un figlio "Glow" con CanvasGroup pilotato ogni frame
    // dal componente GlowBottone: per nasconderlo davvero va disattivato il
    // componente (ferma l'Update che lo riporta ad alphaRiposo) e azzerata
    // l'alpha del CanvasGroup subito dopo. Riattivandolo, OnEnable ripristina il
    // respiro di riposo come sugli altri bottoni del menu.
    private void SetGlowGeneraReport(bool visibile)
    {
        if (bottoneGeneraReport == null) return;

        GlowBottone glow = bottoneGeneraReport.GetComponent<GlowBottone>();
        CanvasGroup cg = null;
        foreach (Transform figlio in bottoneGeneraReport.transform)
        {
            if (figlio.name == "Glow") { cg = figlio.GetComponent<CanvasGroup>(); break; }
        }
        if (glow == null) return;

        if (visibile)
        {
            glow.enabled = true;
        }
        else
        {
            glow.enabled = false;
            // Dopo OnDisable l'alpha resta sul valore di riposo: la azzeriamo per
            // nascondere subito il glow (il componente disattivato non la tocca piu').
            if (cg != null) cg.alpha = 0f;
        }
    }

    // I pannelli "Sfondo" (Tutorial e banner FaiTu) sono Steel Blue: e' l'unico
    // blu della palette e si stacca sia sul nero sia dietro ai bottoni Tan.
    private static void ColoraPannelli(GameObject gruppo)
    {
        if (gruppo == null) return;
        foreach (Transform t in gruppo.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "Sfondo") continue;
            Image img = t.GetComponent<Image>();
            if (img != null) img.color = SteelBlue;
        }
    }

    // I testi dei pannelli Tutorial/FaiTu (ora Steel Blue, un tono medio) vengono
    // resi BIANCHI perche' e' l'unico colore con contrasto sufficiente (3.07:1).
    // Esclude le label dei bottoni, gia' nere da StileBottone.
    private static void ColoraTestiChiariPannelli(GameObject gruppo)
    {
        if (gruppo == null) return;
        foreach (TextMeshProUGUI t in gruppo.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (t.GetComponentInParent<Button>() != null) continue;
            t.color = TestoChiaro;
        }
    }

    // Porta i titoli "Testo Titolo" in bianco: stanno su fondi diversi (nero della
    // camera o pannelli semitrasparenti) e il bianco e' l'unico colore leggibile
    // su entrambi. Il metodo ETitolo normalizza il nome, quindi include anche i
    // titoli con spazio finale.
    private static void ColoraTitoli(GameObject gruppo)
    {
        if (gruppo == null) return;
        foreach (TextMeshProUGUI t in gruppo.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (ETitolo(t)) t.color = TestoChiaro;
        }
    }

    // Imposta il font di TUTTE le scritte (anche inattive): Raleway. Mantiene
    // l'ombra sulle label che l'avevano.
    private void ApplicaFontGlobale()
    {
        TMP_FontAsset fa = FontApp;
        if (fa == null) return;

        foreach (TextMeshProUGUI t in FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool avevaOmbra = t.fontSharedMaterial != null && t.fontSharedMaterial.name.Contains("Drop Shadow");
            bool eTitolo = ETitolo(t);
            t.font = fa;
            // Il font e' unico: stile e ombra si riapplicano solo dove servivano
            // gia', per non aggiungere ombre a testi che non le avevano.
            if (avevaOmbra || eTitolo) ApplicaStileTesto(t);
        }

        foreach (TextMeshPro t in FindObjectsByType<TextMeshPro>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            t.font = fa;
        }
    }

    // Il bottone "Genera Report" sta in scena dentro GruppoFaiTu (x -500, y 250,
    // 400x200): lo spostiamo a runtime nel menu e compattiamo la colonna a 5 bottoni
    // (stesso passo 190 e stessa dimensione degli altri). Il Button e l'onClick della
    // scena restano validi dopo il re-parenting. Prima di IngrandisciBottoniUI(),
    // cosi' la scala 1.15 del menu lo tratta come tutti gli altri.
    private void SpostaGeneraReportInMenu()
    {
        if (menuGroup == null || faiTuGroup == null) return;

        ImpostaPosizioneMenu("Bottone Tutorial", 380f);
        ImpostaPosizioneMenu("Bottone Osservatore", 190f);
        ImpostaPosizioneMenu("Bottone Seguimi", 0f);
        ImpostaPosizioneMenu("Bottone Fai Tu", -190f);

        Transform genera = TrovaFiglio(faiTuGroup, "GeneraReport");
        if (genera == null) return;

        genera.SetParent(menuGroup.transform, false);

        RectTransform rt = genera as RectTransform;
        if (rt != null)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(400f, 100f);
            rt.anchoredPosition = new Vector2(0f, -380f);
        }

        bottoneGeneraReport = genera.GetComponent<Button>();
        // In attesa della conferma del bridge il bottone parte spento: il default
        // e' "niente materiale" piuttosto che un bottone attivo per errore.
        reportDisponibile = false;
        ApplicaAspettoGeneraReport();
    }

    private void ImpostaPosizioneMenu(string nome, float y)
    {
        Transform b = TrovaFiglio(menuGroup, nome);
        if (b == null) return;
        RectTransform rt = b as RectTransform;
        if (rt == null) return;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, y);
    }

    private void IngrandisciBottoniUI()
    {
        if (menuGroup != null)
        {
            foreach (Transform figlio in menuGroup.transform)
                figlio.localScale = Vector3.one * 1.15f;
        }

        if (faiTuGroup != null)
        {
            foreach (Transform figlio in faiTuGroup.transform)
            {
                if (figlio.name == "TornaAlMen\u00F9")
                {
                    RectTransform rt = (RectTransform)figlio;
                    rt.sizeDelta = new Vector2(460f, 230f);
                    ApplicaStileTesto(figlio.GetComponentInChildren<TextMeshProUGUI>(true));
                }
            }
        }
    }

    private void ConfiguraStatoIniziale()
    {
        statoAttuale = AppState.Onboarding;

        if (calibrationGroup != null) calibrationGroup.SetActive(true);
        if (menuGroup != null) menuGroup.SetActive(false);
        if (tutorialGroup != null) tutorialGroup.SetActive(false);
        if (observerGroup != null) observerGroup.SetActive(false);
        if (seguimiGroup != null) seguimiGroup.SetActive(false);
        if (faiTuGroup != null) faiTuGroup.SetActive(false);
        if (faiTuReportMessaggio != null) faiTuReportMessaggio.SetActive(false);
    }

    public void CambiaStato(AppState nuovoStato)
    {
        if (statoAttuale == nuovoStato) return;

        StartCoroutine(DeselezionaFineFrame());

        SongListManager slm = FindFirstObjectByType<SongListManager>();
        if (slm != null) slm.RilasciaIndietro();

        // Reset tracciamento note seguimi
        expectedNotes.Clear();
        userPressedNotes.Clear();

        // Gestione uscite dagli stati precedenti
        if ((statoAttuale == AppState.Osservatore || statoAttuale == AppState.Seguimi) && udpReceiver != null)
        {
            udpReceiver.InviaComandoStop();
        }

        if (statoAttuale == AppState.FaiTu && udpReceiver != null)
        {
            udpReceiver.InviaComandoStopFaiTu();
        }

        // Se si abbandona il tutorial, ferma la riproduzione dell'esempio e ogni
        // coroutine pendente: mai coroutine orfane che bloccano la sfida.
        if (statoAttuale == AppState.Tutorial && nuovoStato != AppState.Tutorial)
        {
            if (tutorialCoroutine != null)
            {
                StopCoroutine(tutorialCoroutine);
                tutorialCoroutine = null;
            }
            if (esempioInCorso)
            {
                esempioInCorso = false;
                if (udpReceiver != null) udpReceiver.InviaComandoStopEsempio();
            }
            esempioCompletato = false;
            ApplicaClearSceneInAttesa();
        }

        if (faiTuBannerCoroutine != null) StopCoroutine(faiTuBannerCoroutine);
        if (reportFeedbackCoroutine != null) StopCoroutine(reportFeedbackCoroutine);
        NascondiBannerModalita();
        NascondiViewerReport();

        if (faiTuReportMessaggio != null) faiTuReportMessaggio.SetActive(false);

        PianoVisualizer visualizer = FindFirstObjectByType<PianoVisualizer>();
        if (visualizer != null)
        {
            visualizer.ResetVisualizer();
            visualizer.PulisciNoteAtteseVisive();
        }

        statoAttuale = nuovoStato;

        // Visibilità pannelli. Tutorial/Osservatore/Seguimi passano da
        // MostraBannerModalitaEAttiva: il gruppo resta spento per i 5s del pannello
        // di spiegazione e si accende dopo. Menu e FaiTu si attivano subito come
        // prima (il banner di FaiTu e' gia' gestito sotto).
        if (calibrationGroup != null) calibrationGroup.SetActive(statoAttuale == AppState.Onboarding);
        if (menuGroup != null) menuGroup.SetActive(statoAttuale == AppState.Menu);

        // All'ingresso del menu chiediamo al bridge se c'e' materiale per il report:
        // finche' non risponde, il bottone "Genera Report" resta spento.
        if (statoAttuale == AppState.Menu && udpReceiver != null)
        {
            ApplicaAspettoGeneraReport();
            udpReceiver.InviaComandoRichiediStatoReport();
        }

        bool introduzioneConBanner =
            statoAttuale == AppState.Tutorial ||
            statoAttuale == AppState.Osservatore ||
            statoAttuale == AppState.Seguimi;

        GameObject gruppoDaAttivare = null;
        if (statoAttuale == AppState.Tutorial) gruppoDaAttivare = tutorialGroup;
        else if (statoAttuale == AppState.Osservatore) gruppoDaAttivare = observerGroup;
        else if (statoAttuale == AppState.Seguimi) gruppoDaAttivare = seguimiGroup;

        // Ogni gruppo diverso da quello corrente va spento, SEMPRE. Questa pulizia
        // era implicita in tre if separati (tutorial/observer/seguimi): quando ho
        // introdotto i pannelli e' rimasta solo l'accensione post-banner, e uscendo
        // da una modalita' verso il menu il gruppo restava acceso sopra il menu.
        // Il && !introduzioneConBanner tiene spento anche il gruppo corrente per i
        // suoi 5s di pannello.
        if (tutorialGroup != null) tutorialGroup.SetActive(statoAttuale == AppState.Tutorial && !introduzioneConBanner);
        if (observerGroup != null) observerGroup.SetActive(statoAttuale == AppState.Osservatore && !introduzioneConBanner);
        if (seguimiGroup != null) seguimiGroup.SetActive(statoAttuale == AppState.Seguimi && !introduzioneConBanner);

        if (introduzioneConBanner)
        {
            bannerModalitaCoroutine = StartCoroutine(MostraBannerModalitaEAttiva(statoAttuale, gruppoDaAttivare));
        }

        if (faiTuGroup != null)
        {
            faiTuGroup.SetActive(statoAttuale == AppState.FaiTu);

            if (statoAttuale == AppState.FaiTu)
            {
                // Prima "start_fai_tu" (con attesa del bridge se serve), poi il banner.
                faiTuBannerCoroutine = StartCoroutine(AvviaFaiTuDopoBridgePronto());
            }
        }
    }

    // All'avvio (o riavvio) il bridge Python impiega qualche secondo a partire:
    // scansiona i brani, inizializza il MIDI e solo dopo lega la porta UDP che riceve
    // i comandi. Se mandiamo "start_fai_tu" prima che il bridge sia in ascolto il
    // pacchetto cade e la registrazione Fai Tu parte spenta: le note vengono
    // comunque inoltrate a Unity (colonne visibili) ma NON registrate. Per questo il
    // comando parte SOLO quando il bridge conferma di essere pronto (stesso approccio
    // dell'attesa del tutorial prima di "play_example"), con un tetto di 30s.
    private IEnumerator AvviaFaiTuDopoBridgePronto()
    {
        if (udpReceiver == null) yield break;

        if (!udpReceiver.BridgePronto)
        {
            TextMeshProUGUI bannerTesto = faiTuBannerMessaggio != null
                ? faiTuBannerMessaggio.GetComponentInChildren<TextMeshProUGUI>(true)
                : null;
            string testoOriginale = bannerTesto != null ? bannerTesto.text : null;
            if (faiTuBannerMessaggio != null) faiTuBannerMessaggio.SetActive(true);
            if (bannerTesto != null) bannerTesto.text = "Connessione alla tastiera in corso...";

            float scadenza = Time.time + 30f;
            while (!udpReceiver.BridgePronto && Time.time < scadenza)
            {
                yield return null;
            }

            // Il banner standard (MostraBannerFaiTuTemporaneo) mostra il testo di
            // scena: glielo ripristiniamo se l'attesa l'aveva sostituito.
            if (bannerTesto != null && testoOriginale != null) bannerTesto.text = testoOriginale;
            if (faiTuBannerMessaggio != null) faiTuBannerMessaggio.SetActive(false);
        }

        // "start_fai_tu" e' idempotente (azzera e riavvia la registrazione): inviarlo
        // una sola volta a bridge pronto basta.
        if (udpReceiver != null) udpReceiver.InviaComandoStartFaiTu();

        yield return StartCoroutine(MostraBannerFaiTuTemporaneo());
    }

    // --- PANNELLO DI SPIEGAZIONE ALL'INGRESSO DI UNA MODALITA' ---
    // Un solo pannello, creato a runtime e riusato da tutte le modalita': duplicarlo
    // per ogni stato creerebbe N oggetti con lo stesso aspetto da mantenere.
    // Vive sotto il Canvas e NON dentro il gruppo della modalita', perche' i gruppi
    // Osservatore e Seguimi hanno localScale 4 (quello FaiTu e' a 1): messo li' il
    // pannello uscirebbe quattro volte piu' grande.
    private void CreaBannerModalita()
    {
        if (bannerModalita != null) return;

        // Il Canvas: preferisco il campo esplicito, altrimenti risalgo da un gruppo
        // noto. Senza canvas non c'e' dove appenderlo e non viene creato nulla.
        Transform contenitore = null;
        foreach (GameObject gruppo in new[] { menuGroup, faiTuGroup, tutorialGroup })
        {
            if (gruppo != null && gruppo.transform.parent != null) { contenitore = gruppo.transform.parent; break; }
        }
        if (contenitore == null) return;

        // Copia i valori reali del banner FaiTu della scena (GruppoFaiTu/FaiTuBanner):
        // sfondo con scala 5,4,2 e testo 400x200 a y=20. Stessi numeri = stesso aspetto.
        GameObject p = new GameObject("BannerModalita", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(contenitore, false);

        RectTransform prt = (RectTransform)p.transform;
        prt.anchorMin = new Vector2(0.5f, 0.5f);
        prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.anchoredPosition = Vector2.zero;

        GameObject sfondo = new GameObject("Sfondo", typeof(RectTransform), typeof(Image));
        sfondo.transform.SetParent(p.transform, false);
        RectTransform srt = (RectTransform)sfondo.transform;
        srt.anchorMin = Vector2.zero;
        srt.anchorMax = Vector2.one;
        srt.offsetMin = Vector2.zero;
        srt.offsetMax = Vector2.zero;
        srt.localScale = new Vector3(5f, 4f, 2f);

        Image img = sfondo.GetComponent<Image>();
        img.color = SteelBlue;
        img.raycastTarget = false;
        if (SpriteArrotondato != null)
        {
            img.sprite = SpriteArrotondato;
            img.type = Image.Type.Sliced;
        }
        ApplicaPenombra(img.gameObject);

        GameObject goTesto = new GameObject("IstruzioneModalita", typeof(RectTransform));
        goTesto.transform.SetParent(p.transform, false);
        RectTransform trt = (RectTransform)goTesto.transform;
        trt.anchorMin = new Vector2(0.5f, 0.5f);
        trt.anchorMax = new Vector2(0.5f, 0.5f);
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = new Vector2(0f, 20f);
        trt.sizeDelta = new Vector2(400f, 200f);

        TextMeshProUGUI testo = goTesto.AddComponent<TextMeshProUGUI>();
        // Font: quello dell'app, con fallback su quello del banner FaiTu della scena.
        if (FontApp != null) testo.font = FontApp;
        else if (faiTuBannerMessaggio != null)
        {
            TextMeshProUGUI riferimento = faiTuBannerMessaggio.GetComponentInChildren<TextMeshProUGUI>(true);
            if (riferimento != null) testo.font = riferimento.font;
        }
        // 40 = fontSize reale del banner FaiTu nella scena (verificato in
        // SampleScene.unity, non stimato): stesse dimensioni del pannello che
        // l'utente ha gia' visto in Fai Tu.
        testo.fontSize = 40f;
        testo.fontStyle = FontStyles.Bold;
        testo.alignment = TextAlignmentOptions.Center;
        testo.color = TestoChiaro;
        testo.raycastTarget = false;

        bannerModalitaTesto = testo;
        bannerModalita = p;
        // Inizialmente spento: CompareSoloIlBannerModalita lo accende per 5s.
        p.SetActive(false);

        // Ultimo figlio del contenitore: i pannelli dei gruppi devono restare sotto.
        p.transform.SetAsLastSibling();
    }

    // --- VISUALIZZATORE DEL REPORT PDF ---
    // Un pannello creato a runtime sotto il Canvas, come BannerModalita: vive fuori
    // dai gruppi, che sono in scale diverse (FaiTu x1, Osservatore/Seguimi x4).
    // Mostra una pagina PNG del report alla volta con Avanti/Indietro, e Chiudi
    // sotto i due pulsanti, come richiesto.
    private void CreaViewerReport()
    {
        if (viewerReport != null) return;

        Transform contenitore = null;
        foreach (GameObject gruppo in new[] { menuGroup, faiTuGroup, tutorialGroup })
        {
            if (gruppo != null && gruppo.transform.parent != null) { contenitore = gruppo.transform.parent; break; }
        }
        if (contenitore == null) return;

        // Pannello esterno: sfondo scuro che copre tutto il canvas dietro le pagine.
        GameObject p = new GameObject("ViewerReportPDF", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(contenitore, false);

        RectTransform prt = (RectTransform)p.transform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;

        Image schermo = p.GetComponent<Image>();
        schermo.color = new Color(0f, 0f, 0f, 0.85f);
        // True: il fondale scuro copre il canvas e blocca i click sugli elementi
        // dietro (i bottoni di FaiTu restano attivi ma non raggiungibili, pattern
        // modale). I bottoni del viewer sono figli e ricevono i loro click.
        schermo.raycastTarget = true;

        // Sfondo del pannello: Steel Blue a tutta area, come gli altri della UI.
        GameObject sfondo = new GameObject("Sfondo", typeof(RectTransform), typeof(Image));
        sfondo.transform.SetParent(p.transform, false);
        RectTransform srt = (RectTransform)sfondo.transform;
        srt.anchorMin = new Vector2(0.5f, 0.5f);
        srt.anchorMax = new Vector2(0.5f, 0.5f);
        srt.pivot = new Vector2(0.5f, 0.5f);
        srt.anchoredPosition = Vector2.zero;
        srt.sizeDelta = new Vector2(700f, 980f);

        Image img = sfondo.GetComponent<Image>();
        img.color = SteelBlue;
        img.raycastTarget = false;
        if (SpriteArrotondato != null)
        {
            img.sprite = SpriteArrotondato;
            img.type = Image.Type.Sliced;
        }
        ApplicaPenombra(img.gameObject);

        // Immagine della pagina corrente del report. Le pagine PNG del bridge sono
        // sempre 8.5x11 (1020x1320 px a 120dpi): imposto il rect con lo stesso
        // rapporto (600 x ~777) e niente AspectRatioFitter, perche' con FitInParent
        // su parent a tutto canvas la pagina si estendeva su tutto lo schermo e
        // copriva i pulsanti. La pagina resta a meta'-alta, con i pulsanti sotto.
        GameObject goImmagine = new GameObject("PaginaReport", typeof(RectTransform), typeof(RawImage));
        goImmagine.transform.SetParent(p.transform, false);
        RectTransform igt = (RectTransform)goImmagine.transform;
        igt.anchorMin = new Vector2(0.5f, 0.5f);
        igt.anchorMax = new Vector2(0.5f, 0.5f);
        igt.pivot = new Vector2(0.5f, 0.5f);
        igt.anchoredPosition = new Vector2(0f, 85f);
        igt.sizeDelta = new Vector2(600f, 777f);

        paginaReportImmagine = goImmagine.GetComponent<RawImage>();
        paginaReportImmagine.color = Color.white;
        paginaReportImmagine.raycastTarget = false;
        paginaReportImmagine.texture = null;

        // Avanti e Indietro affiancati subito sotto la pagina, Chiudi sotto.
        bottoneIndietroReport = CreaBottoneViewer(p.transform, "Indietro", new Vector2(-155f, -355f), new Vector2(280f, 90f), 48f, IndietroPaginaReport);
        bottoneAvantiReport = CreaBottoneViewer(p.transform, "Avanti", new Vector2(155f, -355f), new Vector2(280f, 90f), 48f, AvantiPaginaReport);
        CreaBottoneViewer(p.transform, "Chiudi", new Vector2(0f, -440f), new Vector2(600f, 80f), 48f, ChiudiViewerReport);

        viewerReport = p;
        // Inizialmente spento: ReportPronto lo accende quando le pagine sono pronte.
        p.SetActive(false);

        // Ultimo figlio del contenitore: sta sopra a tutto.
        p.transform.SetAsLastSibling();
    }

    // Bottone del viewer creato a runtime, con lo stesso aspetto degli altri
    // bottoni della UI (Tan, penombra, glow, stile FaiTu).
    private Button CreaBottoneViewer(Transform parent, string testo, Vector2 posizione, Vector2 dimensione, float fontSize, UnityAction onClick)
    {
        GameObject bottoneGO = new GameObject("Bottone " + testo, typeof(RectTransform));
        bottoneGO.transform.SetParent(parent, false);

        RectTransform rt = (RectTransform)bottoneGO.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = posizione;
        rt.sizeDelta = dimensione;

        Image sfondo = bottoneGO.AddComponent<Image>();
        sfondo.sprite = SpriteArrotondato != null ? SpriteArrotondato : CreaSpriteBianco();
        sfondo.type = Image.Type.Sliced;

        Button bottone = bottoneGO.AddComponent<Button>();
        bottone.targetGraphic = sfondo;
        bottone.onClick.AddListener(onClick);

        GameObject goLabel = new GameObject("Testo", typeof(RectTransform));
        goLabel.transform.SetParent(bottoneGO.transform, false);

        RectTransform lrt = (RectTransform)goLabel.transform;
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        TextMeshProUGUI label = goLabel.AddComponent<TextMeshProUGUI>();
        label.text = testo;
        label.font = FontApp != null
            ? FontApp
            : (TMP_Settings.defaultFontAsset != null
                ? TMP_Settings.defaultFontAsset
                : Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF"));
        label.fontSize = fontSize;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.black;
        label.raycastTarget = true;

        StileBottone(bottoneGO.transform, TanColor, DustyRose, DustyRose, Color.black);
        return bottone;
    }

    // Testo di spiegazione per modalita'. FaiTu non compare: ha gia' il suo banner.
    private static string TestoSpiegazioneModalita(AppState stato)
    {
        switch (stato)
        {
            case AppState.Tutorial:
                return "Esegui le diverse sfide per comprendere i concetti legati all'espressivita'.";
            case AppState.Osservatore:
                return "Scegli o Cerca la canzone che preferisci per vederne l'esecuzione.";
            case AppState.Seguimi:
                return "Scegli o Cerca la canzone che preferisci da eseguire seguendo le colonne verdi!";
            default:
                return null;
        }
    }

    // Mostra il pannello per 5 secondi e poi attiva il gruppo della modalita', cosi'
    // l'utente legge prima la spiegazione e poi vede il resto (menu o pannelli
    // tutorial). Il gruppo resta disattivato per tutta la durata.
    private IEnumerator MostraBannerModalitaEAttiva(AppState stato, GameObject gruppo)
    {
        CreaBannerModalita();

        string testo = TestoSpiegazioneModalita(stato);
        if (bannerModalita != null && testo != null)
        {
            if (bannerModalitaTesto != null) bannerModalitaTesto.text = testo;
            bannerModalita.SetActive(true);
            yield return new WaitForSeconds(5.0f);
            bannerModalita.SetActive(false);
        }

        // Controllo difensivo: se nel frattempo l'utente e' tornato al menu o ha
        // cambiato modalita', non riaccendere il gruppo DOPO che CambiaStato l'ha
        // spento. Nota: non serve guardare bannerModalita.activeSelf, perche' la
        // riga sopra lo spegne di proposito e renderebbe la condizione sempre vera.
        // Il caso dell'uscita durante i 5s e' gia' coperto: NascondiBannerModalita
        // chiama StopCoroutine e uccide questa coroutine prima che arrivi qui.
        if (statoAttuale != stato) yield break;

        if (gruppo != null) gruppo.SetActive(true);
    }

    // Il pannello sparisce e resta spento: chiamata a ogni cambio di stato, cosi'
    // leaving una modalita' durante i 5s non lascia il pannello a schermo.
    private void NascondiBannerModalita()
    {
        if (bannerModalitaCoroutine != null)
        {
            StopCoroutine(bannerModalitaCoroutine);
            bannerModalitaCoroutine = null;
        }
        if (bannerModalita != null) bannerModalita.SetActive(false);
    }

    // Usa lo stesso pannello Steel Blue arrotondato con ombra degli intro per i
    // messaggi del report ("Generazione report in corso...", "Lo trovi sulla
    // cartella Sessioni..."), come richiesto: stessa posizione, grandezza e colori
    // di scritte e pannello. Resta acceso finche' qualcuno lo nasconde.
    private void MostraBannerReport(string testo)
    {
        CreaBannerModalita();
        if (bannerModalita == null) return;
        if (bannerModalitaTesto != null) bannerModalitaTesto.text = testo;
        bannerModalita.SetActive(true);
        bannerModalita.transform.SetAsLastSibling();
    }

    private IEnumerator MostraBannerFaiTuTemporaneo()
    {
        if (faiTuBannerMessaggio != null)
        {
            RectTransform bannerRt = faiTuBannerMessaggio.transform is RectTransform
                ? (RectTransform)faiTuBannerMessaggio.transform
                : null;
            if (bannerRt != null)
            {
                bannerRt.anchorMin = new Vector2(0.5f, 0.5f);
                bannerRt.anchorMax = new Vector2(0.5f, 0.5f);
                bannerRt.pivot = new Vector2(0.5f, 0.5f);
                bannerRt.anchoredPosition = Vector2.zero;
            }
            faiTuBannerMessaggio.SetActive(true);
        }
        yield return new WaitForSeconds(5.0f);
        if (faiTuBannerMessaggio != null) faiTuBannerMessaggio.SetActive(false);
    }

    private IEnumerator DeselezionaFineFrame()
    {
        yield return null;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    // --- NAVIGAZIONE GLOBALE ---
    public void AttivaMenu() => CambiaStato(AppState.Menu);
    public void RitornaAlMenu() => CambiaStato(AppState.Menu);

    public void AttivaTutorial()
    {
        sfidaAttuale = 1;
        tastiTutorialPremuti.Clear();
        primaNotaStaccato = -1;
        noteScalaCrescente = 0;
        tempoUltimaNotaScala = -1f;
        tempoUltimoRilascio = -1f;
        ultimaNotaMidi = -1;
        inTransizione = false;
        esempioInCorso = false;
        esempioCompletato = false;
        clearSceneInAttesa = false;

        CambiaStato(AppState.Tutorial);
        if (tutorialCoroutine != null) StopCoroutine(tutorialCoroutine);
        tutorialCoroutine = StartCoroutine(AvviaSfidaConEsempio(1, "SFIDA 1:\nPremi un tasto molto delicatamente (Suona 'Piano')"));
    }

    public bool EsempioInCorso => esempioInCorso;

    // Chiamato dal bridge quando termina la riproduzione dell'esempio.
    public void EsempioCompletato()
    {
        esempioCompletato = true;
        esempioInCorso = false;
        // NOTA: qui NON si applica il "clear_scene" differito. Il bridge manda
        // "clear_scene" ed "example_done' nello stesso frame: pulire subito
        // distruggerebbe la colonna dell'esempio nel frame in cui e' nata.
        // Il reset viene applicato dalla coroutine, dopo un breve periodo di
        // cassa (vedi AvviaSfidaConEsempio).
    }

    // "clear_scene" dal bridge: durante l'esempio lo rimandiamo, altrimenti la
    // colonna dell'esempio verrebbe distrutta nel frame in cui nasce (succede se i
    // messaggi arrivano tutti insieme, es. dopo un hitch alla prima apertura).
    public void NotificaClearScene()
    {
        if (esempioInCorso)
        {
            clearSceneInAttesa = true;
            Debug.Log("[TUTORIAL] clear_scene differito: l'esempio deve restare visibile.");
            return;
        }
        PulisciScena();
    }

    private void ApplicaClearSceneInAttesa()
    {
        if (!clearSceneInAttesa) return;
        clearSceneInAttesa = false;
        PulisciScena();
    }

    private void PulisciScena()
    {
        PianoVisualizer vis = FindFirstObjectByType<PianoVisualizer>();
        if (vis != null)
        {
            vis.ResetVisualizer();
            vis.PulisciNoteAtteseVisive();
        }
    }

    private static string TitoloSfida(int sfida)
    {
        switch (sfida)
        {
            case 2: return "Scala dinamica crescente";
            case 3: return "Staccato";
            case 4: return "Legato";
            default: return "Suona piano";
        }
    }

    // Ogni sfida mostra prima l'ESEMPIO (colonna + suono dal pianoforte, registrato
    // dal bridge) e solo dopo invita l'utente a provare, come da descrizione.
    private IEnumerator AvviaSfidaConEsempio(int sfida, string testoDescrizione)
    {
        inTransizione = true;
        sfidaAttuale = sfida;
        esempioInCorso = false;
        esempioCompletato = false;

        try
        {
            // Il gruppo Tutorial resta spento per i 5s del pannello di spiegazione:
            // aspettiamo che finisca prima di scrivere "SFIDA n" e di lanciare
            // l'esempio, altrimenti l'utente legge entrambi insieme.
            if (bannerModalitaCoroutine != null) yield return bannerModalitaCoroutine;

            if (tutorialText != null)
            {
                tutorialText.text = "SFIDA " + sfida + "\n" + TitoloSfida(sfida)
                                    + "\n\nCome l'esempio che visualizzi";
            }

            yield return new WaitForSeconds(1.2f);

            // 1) Aspettiamo che la tastiera (bridge) sia davvero in ascolto. All'avvio
            //    il bridge scansiona i brani e inizializza il MIDI: se spediamo
            //    "play_example" prima, il pacchetto viene scartato e l'esempio non
            //    parte MAI (e' quello che rendeva la sfida 1 muta al primo avvio).
            if (udpReceiver != null && !udpReceiver.BridgePronto)
            {
                float scadenzaPonte = Time.time + 30f;
                while (!udpReceiver.BridgePronto && Time.time < scadenzaPonte)
                {
                    if (tutorialText != null)
                    {
                        tutorialText.text = "SFIDA " + sfida + "\n" + TitoloSfida(sfida)
                                            + "\n\nConnessione alla tastiera in corso...";
                    }
                    yield return null;
                }
                if (!udpReceiver.BridgePronto)
                {
                    Debug.Log("[TUTORIAL] Ponte MIDI non collegato dopo 30s: salto l'esempio.");
                    if (tutorialText != null) tutorialText.text = $"<color=#{Hex(ErroreTutorial)}>La tastiera non risponde: prova tu</color>";
                    yield return new WaitForSeconds(1.5f);
                    esempioInCorso = false;
                    if (clearSceneInAttesa)
                    {
                        yield return new WaitForSeconds(0.6f);
                    }
                    ApplicaClearSceneInAttesa();
                    PreparaSfidaPerLUtente();
                    inTransizione = false;
                    if (tutorialText != null) tutorialText.text = testoDescrizione;
                    yield break;
                }
            }

            if (udpReceiver != null)
            {
                // Foglio pulito nel bridge: nessun esempio precedente rimasto attivo.
                udpReceiver.InviaComandoStopEsempio();

                // "play_example" e' un singolo datagram UDP: se il bridge non e'
                // in ascolto (1a volta, avvio a freddo, riavvio) il comando si perde
                // e senza retry l'esempio non si vede MAI. Riproviamo fino a 3 volte,
                // fermando l'eventuale esempio partito prima di riprovare (altrimenti
                // si sovrappongono due riproduzioni).
                const int TENTATIVI_MAX = 3;
                // 5s: l'esempio piu' lungo (sfida 2, scala crescente) dura 3.0s, quindi
                // una soglia piu' breve farebbe scattare il ritento mentre l'esempio sta
                // ancora suonando e lo riavvierebbe da capo.
                const float ATTESA_PER_TENTATIVO = 5f;

                for (int tentativo = 1; tentativo <= TENTATIVI_MAX && !esempioCompletato; tentativo++)
                {
                    udpReceiver.InviaComandoEsempio(sfida);
                    esempioInCorso = true;
                    Debug.Log("[TUTORIAL] Esempio sfida " + sfida + ": play_example inviato (tentativo "
                              + tentativo + "/" + TENTATIVI_MAX + ").");

                    if (tutorialText != null)
                        tutorialText.text = tentativo == 1 ? "Riproduco l'esempio..." : "Riproduco l'esempio (riprovo...)";

                    float scadenza = Time.time + ATTESA_PER_TENTATIVO;
                    while (!esempioCompletato && esempioInCorso && Time.time < scadenza)
                        yield return null;

                    if (esempioCompletato) break;

                    // Nessuna conferma entro il tempo: il comando e' andato perso o il
                    // bridge non ha supporto esempi.
                    esempioInCorso = false;
                    if (tentativo < TENTATIVI_MAX)
                    {
                        Debug.Log("[TUTORIAL] Bridge non conferma entro " + ATTESA_PER_TENTATIVO + "s: ritento.");
                        udpReceiver.InviaComandoStopEsempio();
                        yield return new WaitForSeconds(0.4f);
                    }
                }

                if (esempioCompletato)
                {
                    Debug.Log("[TUTORIAL] Bridge ha confermato la fine dell'esempio (sfida " + sfida + ").");
                }
                else
                {
                    Debug.Log("[TUTORIAL] Bridge non conferma dopo " + TENTATIVI_MAX
                              + " tentativi: passo alla prova senza esempio.");
                    if (tutorialText != null) tutorialText.text = $"<color=#{Hex(ErroreTutorial)}>L'esempio non e' disponibile: prova tu</color>";
                    yield return new WaitForSeconds(1.2f);
                }
            }
            else
            {
                Debug.Log("[TUTORIAL] Bridge assente: nessun esempio, passo alla prova.");
            }

            esempioInCorso = false;
            // Se il bridge aveva mandato "clear_scene" DURANTE l'esempio, la pulizia
            // era stata differita: diamo prima una breve cassa (0.6s) cosi' la colonna
            // appena nata si vede, poi puliamo davvero la scena.
            if (clearSceneInAttesa)
            {
                yield return new WaitForSeconds(0.6f);
            }
            ApplicaClearSceneInAttesa();

            // Reset completo dello stato: la prova dell'utente parte da zero e i tasti
            // dell'esempio non devono influenzare la rilevazione.
            PreparaSfidaPerLUtente();

            inTransizione = false;
            if (tutorialText != null) tutorialText.text = "Ora tocca a te";
            yield return new WaitForSeconds(1.5f);

            if (tutorialText != null) tutorialText.text = testoDescrizione;
        }
        finally
        {
            // inTransizione deve SEMPRE tornare false: nessun tutorial bloccabile.
            inTransizione = false;
        }
    }

    private void PreparaSfidaPerLUtente()
    {
        tastiTutorialPremuti.Clear();
        primaNotaStaccato = -1;
        noteScalaCrescente = 0;
        tempoUltimaNotaScala = -1f;
        tempoUltimoRilascio = -1f;
        ultimaNotaMidi = -1;
        ultimaVelocita = 0f;
    }

    public void AttivaOsservatore() => CambiaStato(AppState.Osservatore);
    public void AttivaSeguimi() => CambiaStato(AppState.Seguimi);
    public void AttivaFaiTu() => CambiaStato(AppState.FaiTu);

    public void GeneraReportPDF()
    {
        // Difesa in piu' (il bottone e' gia' disattivato senza materiale).
        if (!reportDisponibile) return;

        // Durante la generazione e la visione del report il menu resta nascosto:
        // si vedono solo il banner, il viewer e lo sfondo nero della camera.
        if (menuGroup != null) menuGroup.SetActive(false);

        NascondiViewerReport();
        if (udpReceiver != null) udpReceiver.InviaComandoGeneraReport();

        if (reportFeedbackCoroutine != null) StopCoroutine(reportFeedbackCoroutine);
        reportFeedbackCoroutine = StartCoroutine(MostraInCorsoPoiTimeout());
    }

    // Riporta in vista il menu alla fine del flusso report. Qui NON va usata
    // AttivaMenu(): lo stato e' gia' Menu e CambiaStato esce subito (early-return
    // su stato identico), lasciando il menu spento come l'avevamo nascosto.
    private void RivelaMenuDopoReport()
    {
        if (menuGroup != null) menuGroup.SetActive(true);
    }

    // Mentre Python genera il PDF (e i PNG delle pagine) resta fermo un messaggio
    // di attesa. Se il report non arriva (errore del bridge), dopo il timeout si
    // comunica e si torna al Menu invece di restare in attesa per sempre.
    private IEnumerator MostraInCorsoPoiTimeout()
    {
        MostraBannerReport("Generazione report in corso...");

        yield return new WaitForSeconds(120f);

        MostraBannerReport("Report non disponibile. Riprova.");
        yield return new WaitForSeconds(3.5f);

        NascondiBannerModalita();
        RivelaMenuDopoReport();
    }

    // Chiamato da UdpReceiver quando il bridge conferma ("report_ready") che il
    // PDF e le pagine PNG sono pronti nella cartella sessione.
    public void ReportPronto(string cartella, int pagine)
    {
        if (reportFeedbackCoroutine != null)
        {
            StopCoroutine(reportFeedbackCoroutine);
            reportFeedbackCoroutine = null;
        }
        NascondiBannerModalita();

        // Il report parte dal menu: se nel frattempo si e' cambiata modalita',
        // il viewer non va mostrato e si torna al menu visibile.
        if (statoAttuale != AppState.Menu)
        {
            RivelaMenuDopoReport();
            return;
        }

        CaricaPagineReport(cartella, pagine);
        if (pagineReport.Count == 0)
        {
            MostraMessaggioReportNonDisponibile();
            return;
        }

        CreaViewerReport();
        if (viewerReport == null) return;
        paginaReportCorrente = 0;
        AggiornaPaginaReport();
        viewerReport.SetActive(true);
        viewerReport.transform.SetAsLastSibling();
    }

    private void MostraMessaggioReportNonDisponibile()
    {
        if (reportFeedbackCoroutine != null) StopCoroutine(reportFeedbackCoroutine);
        reportFeedbackCoroutine = StartCoroutine(ReportNonDisponibilePoiMenu());
    }

    private IEnumerator ReportNonDisponibilePoiMenu()
    {
        MostraBannerReport("Report non trovato.\nCerca Report_Completo_Nora.pdf nella cartella Sessione...");
        yield return new WaitForSeconds(3.5f);
        NascondiBannerModalita();
        RivelaMenuDopoReport();
    }

    private void CaricaPagineReport(string cartella, int pagine)
    {
        ScaricaPagineReport();
        if (string.IsNullOrEmpty(cartella) || pagine <= 0) return;

        for (int i = 1; i <= pagine; i++)
        {
            string percorso = Path.Combine(cartella, $"pagina_{i}.png");
            if (!File.Exists(percorso)) continue;
            byte[] bytes = File.ReadAllBytes(percorso);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(bytes))
            {
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                pagineReport.Add(tex);
            }
            else
            {
                Destroy(tex);
            }
        }
    }

    private void ScaricaPagineReport()
    {
        foreach (Texture2D tex in pagineReport)
        {
            if (tex != null) Destroy(tex);
        }
        pagineReport.Clear();
    }

    private void AvantiPaginaReport()
    {
        if (pagineReport.Count == 0) return;
        if (paginaReportCorrente < pagineReport.Count - 1)
        {
            paginaReportCorrente++;
            AggiornaPaginaReport();
        }
    }

    private void IndietroPaginaReport()
    {
        if (paginaReportCorrente > 0)
        {
            paginaReportCorrente--;
            AggiornaPaginaReport();
        }
    }

    private void AggiornaPaginaReport()
    {
        if (paginaReportImmagine == null || pagineReport.Count == 0) return;
        paginaReportImmagine.texture = pagineReport[paginaReportCorrente];

        // Simmetrici agli estremi: Indietro sparisce sulla prima pagina, Avanti
        // sull'ultima (chiesto e confermato per il PDF finale).
        if (bottoneIndietroReport != null)
            bottoneIndietroReport.gameObject.SetActive(paginaReportCorrente > 0);
        if (bottoneAvantiReport != null)
            bottoneAvantiReport.gameObject.SetActive(paginaReportCorrente < pagineReport.Count - 1);
    }

    private void ChiudiViewerReport()
    {
        NascondiViewerReport();

        if (reportFeedbackCoroutine != null) StopCoroutine(reportFeedbackCoroutine);
        reportFeedbackCoroutine = StartCoroutine(MostraMessaggioReportTornaMenu());
    }

    // Su Chiudi: prima il pannello di conferma su sfondo nero, poi il menu ricompare.
    private IEnumerator MostraMessaggioReportTornaMenu()
    {
        MostraBannerReport("Lo trovi sulla cartella Sessioni insieme alla registrazione della tua esecuzione.");
        yield return new WaitForSeconds(3.5f);
        NascondiBannerModalita();
        RivelaMenuDopoReport();
    }

    private void NascondiViewerReport()
    {
        if (viewerReport != null) viewerReport.SetActive(false);
        ScaricaPagineReport();
    }

    private void CreaBottoneHomeTutorial()
    {
        if (tutorialGroup == null) return;

        GameObject bottoneGO = new GameObject("TornaAlMenu", typeof(RectTransform));
        bottoneGO.transform.SetParent(tutorialGroup.transform, false);

        RectTransform rt = (RectTransform)bottoneGO.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(-500f, 500f);
        rt.sizeDelta = new Vector2(460f, 230f);

        Image sfondo = bottoneGO.AddComponent<Image>();
        sfondo.sprite = SpriteArrotondato != null ? SpriteArrotondato : CreaSpriteBianco();
        sfondo.type = Image.Type.Sliced;

        Button bottone = bottoneGO.AddComponent<Button>();
        bottone.targetGraphic = sfondo;
        bottone.onClick.AddListener(AttivaMenu);

        GameObject labelGO = new GameObject("Testo", typeof(RectTransform));
        labelGO.transform.SetParent(bottoneGO.transform, false);

        RectTransform rtLabel = (RectTransform)labelGO.transform;
        rtLabel.anchorMin = Vector2.zero;
        rtLabel.anchorMax = Vector2.one;
        rtLabel.offsetMin = Vector2.zero;
        rtLabel.offsetMax = Vector2.zero;

        TextMeshProUGUI label = labelGO.AddComponent<TextMeshProUGUI>();
        label.text = "Torna al\nMen\u00F9";
        label.font = FontApp != null
            ? FontApp
            : (TMP_Settings.defaultFontAsset != null
                ? TMP_Settings.defaultFontAsset
                : Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF"));
        label.fontSize = 72;
        label.color = new Color(0.19607843f, 0.19607843f, 0.19607843f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = true;
        ApplicaStileTesto(label);
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

    public void AvviaRiancoraggio()
    {
        Debug.Log("[DEBUG] Avvio procedura di Riancoraggio...");

        PianoVisualizer visualizer = FindFirstObjectByType<PianoVisualizer>();
        if (visualizer != null)
        {
            ARAnchor oldAnchor = visualizer.GetComponent<ARAnchor>();
            if (oldAnchor != null) Destroy(oldAnchor);
        }

        CambiaStato(AppState.Onboarding);

        ManualAnchor calibrator = FindFirstObjectByType<ManualAnchor>();
        if (calibrator != null) calibrator.ResettaStatoCalibrazione();
    }

    // --- RICEZIONE NOTE ATTESE DA PYTHON (MODALITÀ SEGUIMI) ---
    public void ImpostaNoteAttese(int[] notes)
    {
        if (statoAttuale != AppState.Seguimi) return;

        expectedNotes.Clear();
        if (notes != null && notes.Length > 0)
        {
            expectedNotes.AddRange(notes);
        }

        PianoVisualizer visualizer = FindFirstObjectByType<PianoVisualizer>();
        if (visualizer != null)
        {
            visualizer.MostraNoteAttese(notes);
        }

        ControllaNoteSeguimi();
    }

    private void ControllaNoteSeguimi()
    {
        if (expectedNotes.Count == 0) return;

        bool allMatched = true;
        foreach (int expected in expectedNotes)
        {
            if (!userPressedNotes.Contains(expected))
            {
                allMatched = false;
                break;
            }
        }

        if (allMatched)
        {
            expectedNotes.Clear();
            if (udpReceiver != null)
            {
                udpReceiver.InviaComandoSeguimiAvanti();
            }
        }
    }

    public void ValutaNota(int nota, float velocity, string action)
    {
        if (statoAttuale == AppState.Seguimi)
        {
            if (action == "press")
            {
                userPressedNotes.Add(nota);
                ControllaNoteSeguimi();
            }
            else if (action == "release")
            {
                userPressedNotes.Remove(nota);
            }
            return;
        }

        if (statoAttuale != AppState.Tutorial || inTransizione) return;

        if (action == "press")
        {
            // Rimbalzo/doppio note_on della stessa nota già premuta: ignorato.
            // Con le pressioni forti i sensori possono reinviare la nota: senza
            // questa guardia lo Staccato veniva bloccato silenziosamente.
            if (!tastiTutorialPremuti.Add(nota))
            {
                Debug.Log("[TUTORIAL] Doppia pressione ignorata (nota " + nota + ")");
                return;
            }

            float kdt = (tempoUltimoRilascio > 0) ? (Time.time - tempoUltimoRilascio) : 0f;
            bool ceSovrapposizioneKOT = (tastiTutorialPremuti.Count > 1);

            switch (sfidaAttuale)
            {
                case 1:
                    if (velocity < 0.236f)
                    {
                        StartCoroutine(TransizioneSfidaCoroutine(2, "SFIDA 2:\nEsegui una Scala dinamica crescente di 4 note (note verso destra sempre pi\u00F9 forti)"));
                    }
                    break;

                case 2:
                    // Scala dinamica crescente: 4 note in successione (qualsiasi
                    // nota, senza vincolo di direzione) e sempre piu' forti, con
                    // tolleranza ~10%: un lieve calo non azzera la scala. Note
                    // troppo ravvicinate (<0.04s) sono ignorate come spurie; una
                    // pausa oltre i 3s fa ripartire il conteggio da 1.
                    if (ultimaNotaMidi == -1)
                    {
                        noteScalaCrescente = 1;
                        tempoUltimaNotaScala = Time.time;
                    }
                    else
                    {
                        float dtScala = Time.time - tempoUltimaNotaScala;
                        if (dtScala < 0.04f)
                        {
                            break;
                        }
                        if (dtScala > 3.0f)
                        {
                            noteScalaCrescente = 1;
                        }
                        else if (velocity >= ultimaVelocita * 0.90f)
                        {
                            noteScalaCrescente++;
                            if (noteScalaCrescente >= 4)
                            {
                                StartCoroutine(TransizioneSfidaCoroutine(3, "SFIDA 3:\nEsegui lo 'Staccato' (Suona le due note in rapida successione con un tocco brevissimo e staccato su ciascuna, come se i tasti scottassero)"));
                            }
                        }
                        else
                        {
                            noteScalaCrescente = 1;
                        }
                        tempoUltimaNotaScala = Time.time;
                    }
                    ultimaNotaMidi = nota;
                    ultimaVelocita = velocity;
                    break;

                case 3:
                    // Registra la prima nota dello staccato (solo alla prima pollice
                    // della sfida): la seconda deve essere diversa per avanzare.
                    if (tastiTutorialPremuti.Count == 1 && primaNotaStaccato == -1)
                        primaNotaStaccato = nota;

                    if (!ceSovrapposizioneKOT && tempoUltimoRilascio > 0 && kdt > 0.04f && kdt < 0.5f && nota != primaNotaStaccato)
                    {
                        StartCoroutine(TransizioneSfidaCoroutine(4, "SFIDA 4:\nEsegui il 'Legato' (suona la nota successiva prima di rilasciare la precedente)"));
                    }
                    else if (tempoUltimoRilascio > 0 && ceSovrapposizioneKOT)
                    {
                        StartCoroutine(TransizioneErroreCoroutine("Errore: Note sovrapposte!", "SFIDA 3:\nEsegui lo 'Staccato' (Suona le due note in rapida successione con un tocco brevissimo e staccato su ciascuna, come se i tasti scottassero)"));
                    }
                    break;

                case 4:
                    if (ceSovrapposizioneKOT)
                    {
                        StartCoroutine(FineTutorialCoroutine());
                    }
                    else if (tempoUltimoRilascio > 0)
                    {
                        StartCoroutine(TransizioneErroreCoroutine("Errore: Note staccate!", "SFIDA 4:\nEsegui il 'Legato' (suona la nota successiva prima di rilasciare la precedente)"));
                    }
                    break;
            }
        }

        if (action == "release")
        {
            tastiTutorialPremuti.Remove(nota);
            if (tastiTutorialPremuti.Count == 0) tempoUltimoRilascio = Time.time;
        }
    }

    private IEnumerator TransizioneSfidaCoroutine(int prossimaSfida, string testoNuovaSfida)
    {
        inTransizione = true;
        // Sopra il pannello Steel Blue il verde/rosso di prima nonavevano contrasto
        // (1.29:1 e 1.12:1): restano bianchi in grassetto e a distinguerli basta
        // la parola scritta.
        if (tutorialText != null) tutorialText.text = $"<color=#{Hex(SuccessoTutorial)}><b>COMPLETATO!</b></color>\n\nOttimo lavoro! Preparati per la prossima sfida...";

        yield return new WaitForSeconds(2.5f);

        if (tutorialCoroutine != null) StopCoroutine(tutorialCoroutine);
        tutorialCoroutine = StartCoroutine(AvviaSfidaConEsempio(prossimaSfida, testoNuovaSfida));
        yield return tutorialCoroutine;
        tutorialCoroutine = null;
    }

    private IEnumerator TransizioneErroreCoroutine(string messaggioErrore, string testoSfidaDaRipristinare)
    {
        inTransizione = true;
        if (tutorialText != null) tutorialText.text = $"<color=#{Hex(ErroreTutorial)}><b>{messaggioErrore}</b></color>\n\nRileggi bene le istruzioni e riprova.";

        yield return new WaitForSeconds(2.0f);

        // Reset secco di TUTTO lo stato di tentativo, non solo del primo tasto dello
        // staccato. Motivo: durante questi 2s inTransizione blocca anche i "release"
        // (valuta nota, riga 1165: la guardia e' prima sia di press sia di release),
        // quindi le note che erano premute all'errore non ricevono mai il rilascio e
        // resterebbero in tastiTutorialPremuti. ceSovrapposizioneKOT resterebbe true
        // e lo Staccato continuerebbe a segnalare "Note sovrapposte!" anche dopo un
        // tentativo corretto. PreparaSfidaPerLUtente fa la Clear() e azzera anche
        // tempoUltimoRilascio, senza cui il Legato non ripartirebbe.
        // Va dopo l'attesa e non prima: se l'utente tiene ancora un tasto premuto, il
        // suo rilascio arriverebbe dopo il reset e azzererebbe il conteggio.
        PreparaSfidaPerLUtente();

        if (tutorialText != null) tutorialText.text = testoSfidaDaRipristinare;
        inTransizione = false;
    }

    private IEnumerator FineTutorialCoroutine()
    {
        inTransizione = true;
        sfidaAttuale = 0;
        tastiTutorialPremuti.Clear();
        primaNotaStaccato = -1;
        noteScalaCrescente = 0;
        tempoUltimaNotaScala = -1f;
        if (tutorialText != null) tutorialText.text = $"<color=#{Hex(SuccessoTutorial)}><b>ECCELLENTE, TUTORIAL COMPLETATO!</b></color>\n \n Ora verrai reindirizzato al men\u00F9...";

        yield return new WaitForSeconds(3.5f);
        AttivaMenu();
        inTransizione = false;
    }
}