using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using TMPro;

public class ManualAnchor : MonoBehaviour
{
    [Header("Riferimenti Generali")]
    public PianoVisualizer pianoVisualizer;
    public GameObject guideSphere; // Sfera che segue lo sguardo/mano per prendere la mira
    public TextMeshProUGUI instructionalText; // TextMeshPro per guidare l'utente

    [Header("Posizionamento Automatico Canvas")]
    public Transform canvasTransform; // Trascina qui il tuo Canvas dall'Inspector
    public Vector3 offsetCanvasDalPiano = new Vector3(0.60f, 0.25f, 0.20f);
    // X = 0.60 (Centrato lungo la tastiera 88 tasti)
    // Y = 0.25 (Altezza sopra i tasti)
    // Z = 0.20 (Profondità: dietro le colonne 3D)

    [Header("Materiali Sfere di Feedback (Opzionale)")]
    public Material materialePunto1; // Es. Rosso (Estremo Sinistro)
    public Material materialePunto2; // Es. Blu (Estremo Destro)
    public Material materialePunto3; // Es. Verde (Do Centrale)

    // Diametro in metri della sfera guida e delle sferette lasciate sui punti presi.
    private const float DIAMETRO_SFERE = 0.015f;
    // Il colore della sfera guida segue lo sticker da puntare: rosso, poi blu, poi verde.
    private static readonly Color ColoreSferaPunto1 = new Color(1f, 0.2f, 0.2f);
    private static readonly Color ColoreSferaPunto2 = new Color(0.25f, 0.35f, 1f);
    private static readonly Color ColoreSferaPunto3 = new Color(0.14f, 0.86f, 0.28f);

    private int currentStep = 0; // 0 = Attesa P1, 1 = Attesa P2, 2 = Attesa P3, 3 = Calibrato
    private Vector3 p1, p2, p3;

    private GameObject markerP1, markerP2, markerP3;
    private Transform mainCameraTransform;

    // --- SALVATAGGIO STATO ORIGINALE CANVAS ---
    private Transform originalCanvasParent;
    private Vector3 originalCanvasLocalPos;
    private Quaternion originalCanvasLocalRot;
    private Vector3 originalCanvasLocalScale; // <-- AGGIUNTO: Salviamo la scala corretta!

    // --- PROTEZIONE CONTRO I CLICK MULTIPLI ---
    private float lastPinchTime = 0f;
    private bool diagnosticStampato = false;

    // Gli sticker rosso e blu stanno accanto agli spigoli ESTERNI del primo (La0) e
    // dell'ultimo tasto (Do8): tra loro c'e' tutta la tastiera, 52 tasti bianchi
    // da 23,5 mm = 1,222 m. Prima si usava 1,1985 m (51 tasti: dal centro del La0
    // al centro del Do8), che con gli sticker sui bordi stringeva tutto del 2% e
    // spostava le note di mezzo tasto: l'errore cresceva andando verso destra,
    // fino a quasi 2,5 cm sul Do8.
    private const float LARGHEZZA_TASTO_BIANCO = 0.0235f;
    private const int TASTI_BIANCHI = 52;
    private const float LARGHEZZA_TEORICA_TASTIERA = TASTI_BIANCHI * LARGHEZZA_TASTO_BIANCO;

    void Start()
    {
        // Salva lo stato originale del Canvas per poterlo ripristinare in caso di re-calibrazione
        if (canvasTransform != null)
        {
            originalCanvasParent = canvasTransform.parent;
            originalCanvasLocalPos = canvasTransform.localPosition;
            originalCanvasLocalRot = canvasTransform.localRotation;
            originalCanvasLocalScale = canvasTransform.localScale; // <-- Salva la scala VR (es. 0.001)
        }

        RisolviCamera();

        if (guideSphere != null)
        {
            guideSphere.SetActive(true);
            // La sfera e' una primitiva di diametro 1 senza genitore: la scala e' il diametro.
            guideSphere.transform.localScale = Vector3.one * DIAMETRO_SFERE;
        }

        if (guideSphere != null && guideSphere.GetComponent<Renderer>() != null)
        {
            Debug.Log($"[CALIBRAZIONE] GuideSphere colore materiale: {guideSphere.GetComponent<Renderer>().sharedMaterial.color}, shader: {guideSphere.GetComponent<Renderer>().sharedMaterial.shader.name}");
        }

        CreaPannelloCalibrazione();

        AggiornaTestoIstruzioni();
    }

    private void RisolviCamera()
    {
        if (mainCameraTransform != null) return;

        // 1) Telecamera del visore Meta (OVRCameraRig/CenterEyeAnchor)
        OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.centerEyeAnchor != null)
        {
            mainCameraTransform = cameraRig.centerEyeAnchor;
            Debug.Log($"[CALIBRAZIONE] Telecamera trovata: CenterEyeAnchor (OVRCameraRig)");
            return;
        }

        // 2) Fallback: telecamera taggata MainCamera
        if (Camera.main != null)
        {
            mainCameraTransform = Camera.main.transform;
            Debug.Log($"[CALIBRAZIONE] Telecamera trovata: {Camera.main.name} (Camera.main)");
            return;
        }

        // 3) Fallback: qualunque telecamera abilitata nella scena
        foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (c == null || !c.enabled) continue;
            mainCameraTransform = c.transform;
            Debug.Log($"[CALIBRAZIONE] Telecamera trovata: {c.name} (fallback)");
            return;
        }

        Debug.LogWarning("[CALIBRAZIONE] Nessuna telecamera trovata per la sfera guida! Verifica OVRCameraRig/Camera.main.");
    }

    void Update()
    {
        // Se non abbiamo ancora una telecamera, riproviamo ogni frame
        // (il rig del visore potrebbe attivarsi dopo il nostro Start).
        if (mainCameraTransform == null)
        {
            RisolviCamera();
            if (mainCameraTransform == null) return;
        }

        if (!diagnosticStampato && mainCameraTransform != null)
        {
            diagnosticStampato = true;
            Renderer r = guideSphere != null ? guideSphere.GetComponent<Renderer>() : null;
            Debug.Log($"[CALIBRAZIONE] DIAG camera pos={mainCameraTransform.position} fwd={mainCameraTransform.forward} | sfera active={guideSphere != null && guideSphere.activeInHierarchy} worldPos={(guideSphere != null ? guideSphere.transform.position.ToString("F3") : "n/a")} scale={(guideSphere != null ? guideSphere.transform.lossyScale.ToString("F3") : "n/a")} rendererEnabled={(r != null ? r.enabled.ToString() : "n/a")} bounds={(r != null ? r.bounds.ToString("F3") : "n/a")}");
        }

        // Se abbiamo completato la calibrazione, non facciamo più nulla
        if (currentStep >= 3 || guideSphere == null) return;

        // La sfera guida sta tra pollice e indice della mano destra: si appoggiano
        // le dita sullo sticker e il punto preso e' proprio li'. Prima seguiva lo
        // sguardo a 60 cm dalla testa: il punto finiva sulla linea occhi-sticker ma
        // piu' vicino all'utente, e la tastiera virtuale usciva piu' stretta del vero.
        if (PuntoTraLeDita(out Vector3 dita))
        {
            guideSphere.transform.position = dita;
        }
        else
        {
            // Riserva, se il tracciamento delle dita non c'e': la vecchia mira con lo sguardo.
            Vector3 targetPos = mainCameraTransform.position + mainCameraTransform.forward * 0.6f + Vector3.down * 0.2f;
            guideSphere.transform.position = Vector3.Lerp(guideSphere.transform.position, targetPos, Time.deltaTime * 8f);
        }

        // Orienta la sfera guida in base allo sguardo dell'utente (annullando l'inclinazione verticale)
        Vector3 forwardSguardo = mainCameraTransform.forward;
        forwardSguardo.y = 0;
        if (forwardSguardo != Vector3.zero) guideSphere.transform.rotation = Quaternion.LookRotation(forwardSguardo);
    }

    // Distanza (m) della pallina guida davanti alla punta delle dita.
    private const float DISTANZA_PALLINA_DALLE_DITA = 0.03f;

    // Scheletri delle mani (cache rinfrescata ogni mezzo secondo).
    private OVRSkeleton[] scheletri = new OVRSkeleton[0];
    private float prossimoRinfrescoScheletri = 0f;
    private bool avvisoDitaStampato = false;

    // Punto medio tra la punta del pollice e quella dell'indice della mano destra.
    private bool PuntoTraLeDita(out Vector3 punto)
    {
        punto = Vector3.zero;
        if (Time.time >= prossimoRinfrescoScheletri)
        {
            prossimoRinfrescoScheletri = Time.time + 0.5f;
            scheletri = FindObjectsByType<OVRSkeleton>(FindObjectsSortMode.None);
        }

        foreach (OVRSkeleton s in scheletri)
        {
            if (s == null || !s.isActiveAndEnabled || !s.IsInitialized || !s.IsDataValid) continue;
            OVRSkeleton.SkeletonType tipo = s.GetSkeletonType();
            bool openXR = tipo == OVRSkeleton.SkeletonType.XRHandRight;
            if (!openXR && tipo != OVRSkeleton.SkeletonType.HandRight) continue;

            // I due tipi di scheletro numerano le ossa in modo diverso.
            OVRSkeleton.BoneId idPollice = openXR ? OVRSkeleton.BoneId.XRHand_ThumbTip : OVRSkeleton.BoneId.Hand_ThumbTip;
            OVRSkeleton.BoneId idIndice = openXR ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            OVRSkeleton.BoneId idPolso = openXR ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot;
            Transform pollice = null, indice = null, polso = null;
            if (s.Bones == null) continue;
            foreach (OVRBone osso in s.Bones)
            {
                if (osso == null || osso.Transform == null) continue;
                if (osso.Id == idPollice) pollice = osso.Transform;
                else if (osso.Id == idIndice) indice = osso.Transform;
                else if (osso.Id == idPolso) polso = osso.Transform;
            }
            if (pollice == null || indice == null) continue;

            punto = (pollice.position + indice.position) * 0.5f;
            // La pallina sta un po' davanti alle dita, nella direzione in cui
            // puntano (dal polso verso le punte), come la punta di una penna:
            // e' lei che va sullo sticker e le dita restano comode, piu' indietro.
            if (polso != null)
            {
                Vector3 direzione = punto - polso.position;
                if (direzione.sqrMagnitude > 0.0001f)
                    punto += direzione.normalized * DISTANZA_PALLINA_DALLE_DITA;
            }
            return true;
        }

        if (!avvisoDitaStampato)
        {
            avvisoDitaStampato = true;
            Debug.LogWarning("[CALIBRAZIONE] Dita della mano destra non tracciate: la sfera guida segue lo sguardo.");
        }
        return false;
    }

    public void OnPinchDetected()
    {
        if (currentStep >= 3 || guideSphere == null || pianoVisualizer == null) return;

        // PROTEZIONE 1: Cooldown temporale (aumentato a 1.5 secondi)
        if (Time.time - lastPinchTime < 1.5f) return;

        // PROTEZIONE 2: Distanza spaziale (DEVI spostare la sfera di almeno 15 cm dal punto precedente)
        if (currentStep == 1 && Vector3.Distance(guideSphere.transform.position, p1) < 0.15f)
        {
            Debug.LogWarning("Devi spostarti verso destra per il Punto 2!");
            return;
        }
        if (currentStep == 2 && Vector3.Distance(guideSphere.transform.position, p2) < 0.15f)
        {
            Debug.LogWarning("Devi spostarti verso il Do Centrale per il Punto 3!");
            return;
        }

        lastPinchTime = Time.time;

        switch (currentStep)
        {
            case 0:
                p1 = guideSphere.transform.position;
                markerP1 = CreaMarkerTemporaneo(p1, materialePunto1);
                currentStep = 1;
                AggiornaTestoIstruzioni();
                Debug.Log("[CALIBRAZIONE] Punto 1 registrato!");
                break;

            case 1:
                p2 = guideSphere.transform.position;
                markerP2 = CreaMarkerTemporaneo(p2, materialePunto2);
                currentStep = 2;
                AggiornaTestoIstruzioni();
                Debug.Log("[CALIBRAZIONE] Punto 2 registrato!");
                break;

            case 2:
                p3 = guideSphere.transform.position;
                markerP3 = CreaMarkerTemporaneo(p3, materialePunto3);
                currentStep = 3;
                AggiornaTestoIstruzioni();
                Debug.Log("[CALIBRAZIONE] Punto 3 registrato! Avvio calibrazione...");

                EseguiCalibrazione3Punti();
                break;
        }
    }

    private void EseguiCalibrazione3Punti()
    {
        // --- 1. PROIEZIONE ORIZZONTALE PERFETTA ---
        Vector3 rightDir = (p2 - p1);
        rightDir.y = 0f;
        rightDir.Normalize();

        Vector3 upDir = Vector3.up;
        Vector3 forwardDir = Vector3.Cross(rightDir, upDir).normalized;

        // --- 2. SPOSTAMENTO IN PROFONDITÀ ---
        float profonditaPunto3 = Vector3.Dot(p3 - p1, forwardDir);
        Vector3 posizioneArretrata = p1 + (forwardDir * profonditaPunto3);
        // Anche l'altezza e' quella del punto 3 (Do centrale), non del punto 1:
        // colonne e tasto verde partono all'altezza dello sticker verde.
        posizioneArretrata.y = p3.y;

        // --- 3. CALCOLO DELLA SCALA DINAMICA ---
        // Larghezza misurata in orizzontale, lungo la tastiera: un dislivello tra i
        // due sticker non deve allargarla.
        float larghezzaRealeRilevata = Vector3.Dot(p2 - p1, rightDir);
        float fattoreScala = larghezzaRealeRilevata / LARGHEZZA_TEORICA_TASTIERA;

        // --- 4. POSIZIONAMENTO E ROTAZIONE SQUADRATA ---
        // Lo sticker rosso e' sul bordo esterno del La0, ma l'origine del
        // visualizzatore (x = 0) e' il CENTRO del La0: mezzo tasto piu' a destra.
        pianoVisualizer.transform.position = posizioneArretrata
            + rightDir * (LARGHEZZA_TASTO_BIANCO * 0.5f * fattoreScala);
        pianoVisualizer.transform.rotation = Quaternion.LookRotation(forwardDir, upDir);
        pianoVisualizer.transform.localScale = new Vector3(fattoreScala, fattoreScala, fattoreScala);

        // --- 5. AUTOMATISMO CANVAS (CORRETTO) ---
        if (canvasTransform != null)
        {
            // Fa diventare il Canvas figlio del pianoforte
            canvasTransform.SetParent(pianoVisualizer.transform, false);

            // Posizione e rotazione locale fissa
            canvasTransform.localPosition = offsetCanvasDalPiano;
            canvasTransform.localRotation = Quaternion.identity;

            // FIX SCALA: Mantiene la scala VR originale moltiplicata per la compensazione di scala
            canvasTransform.localScale = originalCanvasLocalScale / fattoreScala;
        }

        // --- 6. PERSISTENZA TRAMITE SPATIAL ANCHOR ---
        var anchorManager = FindFirstObjectByType<ARAnchorManager>();
        if (anchorManager != null && pianoVisualizer.gameObject.GetComponent<ARAnchor>() == null)
        {
            pianoVisualizer.gameObject.AddComponent<ARAnchor>();
        }

        // --- 7. PULIZIA VISIVA ---
        PulisciMarkers();
        if (guideSphere != null) guideSphere.SetActive(false);

        if (instructionalText != null)
        {
            instructionalText.transform.parent.gameObject.SetActive(false);
        }

        Debug.Log($"[OK] Calibrazione Livellata 3 Punti completata! Scala: {fattoreScala:F2}x");

        // Prima del menu, la regolazione fine a mano (destra/sinistra).
        AvviaRegolazioneFine();
    }

    // ---------------------------------------------------------------
    // REGOLAZIONE FINE (dopo le 3 sfere)
    // ---------------------------------------------------------------
    // Le 3 sfere lasciano spesso i tasti virtuali un po' fuori posto. Qui si vedono
    // tutti i Do in verde (colonne e tasti) e si sistemano a mano in due passi:
    //   1) POSIZIONE: sposta tutti i tasti a destra/sinistra finche' il Do1 cade
    //      sul suo tasto;
    //   2) LARGHEZZA: allarga o stringe la tastiera attorno al Do1 finche' anche
    //      il Do8 cade sul suo tasto (il Do1 non si muove).
    // Serve la larghezza perche' un errore di scala (sticker qualche mm fuori
    // dagli spigoli dei tasti) cresce lungo la tastiera e lo spostamento da solo
    // non lo corregge. "Conferma" porta al menu.
    private const float REGOLAZIONE_MAX_MM = 30f;
    private const float PASSO_BOTTONI_MM = 0.5f;

    // Letto da RaggioPuntatore: in Onboarding il laser serve solo qui.
    public static bool RegolazioneInCorso { get; private set; }

    private GameObject pannelloRegolazione;
    private SliderTempo sliderPosizione;
    private SliderTempo sliderLarghezza;
    private TextMeshProUGUI testoPosizione;
    private TextMeshProUGUI testoLarghezza;
    // Posizione: mm di spostamento. Larghezza: mm di cui si sposta il Do8.
    private float posizioneMm = 0f;
    private float larghezzaMm = 0f;

    private static readonly System.Globalization.CultureInfo Italiano = System.Globalization.CultureInfo.GetCultureInfo("it-IT");

    private void AvviaRegolazioneFine()
    {
        ChiudiRegolazioneFine();
        RegolazioneInCorso = true;
        posizioneMm = 0f;
        larghezzaMm = 0f;
        pianoVisualizer.ImpostaRegolazioneFine(0f, 1f);
        pianoVisualizer.MostraTuttiIDo();
        CreaPannelloRegolazione();
        ApplicaRegolazione();

        RaggioPuntatore raggio = FindFirstObjectByType<RaggioPuntatore>();
        if (raggio != null) raggio.RinfrescaOra();
        Debug.Log("[CALIBRAZIONE] Regolazione fine: sposta i tasti virtuali e conferma.");
    }

    private void ImpostaPosizione(float mm)
    {
        posizioneMm = Mathf.Clamp(mm, -REGOLAZIONE_MAX_MM, REGOLAZIONE_MAX_MM);
        ApplicaRegolazione();
    }

    private void ImpostaLarghezza(float mm)
    {
        larghezzaMm = Mathf.Clamp(mm, -REGOLAZIONE_MAX_MM, REGOLAZIONE_MAX_MM);
        ApplicaRegolazione();
    }

    // Millimetri reali -> unita' locali del visualizzatore (che e' scalato).
    private void ApplicaRegolazione()
    {
        float scala = Mathf.Max(0.0001f, pianoVisualizer.transform.lossyScale.x);
        float spostamento = posizioneMm / 1000f / scala;
        float fattore = 1f + (larghezzaMm / 1000f / scala) / PianoVisualizer.DISTANZA_DO1_DO8;
        pianoVisualizer.ImpostaRegolazioneFine(spostamento, fattore);

        if (sliderPosizione != null) sliderPosizione.SetValueWithoutNotify(posizioneMm);
        if (sliderLarghezza != null) sliderLarghezza.SetValueWithoutNotify(larghezzaMm);
        if (testoPosizione != null)
            testoPosizione.text = "<b>1) Posizione</b>: allinea il Do1   " + FormattaMm(posizioneMm);
        if (testoLarghezza != null)
            testoLarghezza.text = "<b>2) Larghezza</b>: allinea il Do8   " + FormattaMm(larghezzaMm);
    }

    private static string FormattaMm(float mm)
    {
        return (mm >= 0f ? "+" : "") + mm.ToString("0.0", Italiano) + " mm";
    }

    private void ConfermaRegolazione()
    {
        Debug.Log($"[CALIBRAZIONE] Regolazione confermata: posizione {posizioneMm:F1} mm, larghezza {larghezzaMm:F1} mm al Do8");
        ChiudiRegolazioneFine();
        StartCoroutine(AttivaMenuDopoCalibrazione());
    }

    // Toglie pannello e note verdi; la regolazione scelta resta applicata.
    private void ChiudiRegolazioneFine()
    {
        RegolazioneInCorso = false;
        if (pannelloRegolazione != null) Destroy(pannelloRegolazione);
        pannelloRegolazione = null;
        sliderPosizione = null;
        sliderLarghezza = null;
        testoPosizione = null;
        testoLarghezza = null;
        if (pianoVisualizer != null) pianoVisualizer.PulisciNoteAtteseVisive();
    }

    private void CreaPannelloRegolazione()
    {
        if (canvasTransform == null) return;

        GameObject p = new GameObject("PannelloRegolazioneFine", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(canvasTransform, false);
        RectTransform rt = (RectTransform)p.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(900f, 560f);
        ApplicaStilePannello(p.GetComponent<Image>());
        pannelloRegolazione = p;

        TextMeshProUGUI titolo = CreaTestoRegolazione(rt, "Titolo", new Vector2(0f, 220f), new Vector2(840f, 90f), 26f);
        titolo.text = "<b>REGOLAZIONE FINE</b>\nPrima allinea il Do1, poi il Do8 sui tasti veri";
        titolo.color = GameManager.SteelBlue;

        testoPosizione = CreaTestoRegolazione(rt, "TestoPosizione", new Vector2(0f, 140f), new Vector2(840f, 50f), 26f);
        testoPosizione.color = Color.black;
        sliderPosizione = CreaRigaSlider(rt, 80f, ImpostaPosizione, () => posizioneMm);

        testoLarghezza = CreaTestoRegolazione(rt, "TestoLarghezza", new Vector2(0f, 0f), new Vector2(840f, 50f), 26f);
        testoLarghezza.color = Color.black;
        sliderLarghezza = CreaRigaSlider(rt, -60f, ImpostaLarghezza, () => larghezzaMm);

        CreaBottoneRegolazione(rt, "Conferma", new Vector2(0f, -200f), new Vector2(300f, 80f), ConfermaRegolazione);
    }

    // Una riga: bottone "<", slider di precisione, bottone ">" (passi da 0,5 mm).
    private SliderTempo CreaRigaSlider(RectTransform parent, float y, System.Action<float> imposta, System.Func<float> valore)
    {
        GameObject area = new GameObject("AreaSlider", typeof(RectTransform));
        area.transform.SetParent(parent, false);
        RectTransform art = (RectTransform)area.transform;
        art.anchorMin = new Vector2(0.5f, 0.5f);
        art.anchorMax = new Vector2(0.5f, 0.5f);
        art.pivot = new Vector2(0.5f, 0.5f);
        art.anchoredPosition = new Vector2(0f, y);
        art.sizeDelta = new Vector2(560f, 80f);

        // La funzione della barra del tempo stende lo slider su tutta la larghezza
        // del genitore, meno i margini.
        SliderTempo slider = BarraTempoOsservatore.CreaSlider(art, 10f, 80f);
        slider.minValue = -REGOLAZIONE_MAX_MM;
        slider.maxValue = REGOLAZIONE_MAX_MM;
        slider.SetValueWithoutNotify(0f);
        // I valori sono attorno allo zero: niente riempimento da sinistra.
        if (slider.fillRect != null)
            slider.fillRect.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0f);
        slider.onValueChanged.AddListener(v => imposta(v));

        string passo = PASSO_BOTTONI_MM.ToString("0.0", Italiano);
        CreaBottoneRegolazione(parent, "< " + passo, new Vector2(-365f, y), new Vector2(130f, 70f),
            () => imposta(valore() - PASSO_BOTTONI_MM));
        CreaBottoneRegolazione(parent, passo + " >", new Vector2(365f, y), new Vector2(130f, 70f),
            () => imposta(valore() + PASSO_BOTTONI_MM));
        return slider;
    }

    private TextMeshProUGUI CreaTestoRegolazione(RectTransform parent, string nome, Vector2 posizione, Vector2 dimensione, float fontSize)
    {
        GameObject go = new GameObject(nome, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = posizione;
        rt.sizeDelta = dimensione;
        TextMeshProUGUI testo = go.AddComponent<TextMeshProUGUI>();
        if (GameManager.FontApp != null) testo.font = GameManager.FontApp;
        testo.fontSize = fontSize;
        testo.alignment = TextAlignmentOptions.Center;
        testo.raycastTarget = false;
        return testo;
    }

    // Bottone Tan con hover e pressione Dusty Rose, come gli altri bottoni dell'app.
    private void CreaBottoneRegolazione(RectTransform parent, string etichetta, Vector2 posizione, Vector2 dimensione, UnityEngine.Events.UnityAction azione)
    {
        GameObject go = new GameObject("Bottone " + etichetta, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = posizione;
        rt.sizeDelta = dimensione;

        Image img = go.GetComponent<Image>();
        img.color = GameManager.TanColor;
        if (GameManager.SpriteArrotondato != null)
        {
            img.sprite = GameManager.SpriteArrotondato;
            img.type = Image.Type.Sliced;
        }
        GameManager.ApplicaPenombra(go);

        Button btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        ColorBlock colori = btn.colors;
        colori.normalColor = Color.white;
        colori.highlightedColor = GameManager.DustyRose;
        colori.selectedColor = Color.white;
        colori.pressedColor = GameManager.DustyRose;
        btn.colors = colori;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        btn.onClick.AddListener(azione);

        TextMeshProUGUI testo = CreaTestoRegolazione(rt, "Testo", Vector2.zero, dimensione, 28f);
        testo.text = "<b>" + etichetta + "</b>";
        testo.color = Color.black;
    }

    private GameObject messaggioAncoraggio;
    private GameObject pannelloCalibrazione;

    private IEnumerator AttivaMenuDopoCalibrazione()
    {
        // Messaggio runtime disegnato al centro del Canvas (dove poi compare il
        // menu), senza toccare il pannello di calibrazione che a fine calibrazione
        // viene spento (instructionalText.transform.parent.SetActive(false)).
        MostraMessaggioAncoraggio();

        yield return new WaitForSeconds(3.0f);

        // Protezione: se nel frattempo e' partita una nuova calibrazione
        // (Bottone Riancora), currentStep non e' piu' 3 e non apriamo il menu.
        if (currentStep < 3)
        {
            NascondiMessaggioAncoraggio();
            yield break;
        }

        GameManager gm = FindFirstObjectByType<GameManager>();
        if (gm != null) gm.AttivaMenu();

        NascondiMessaggioAncoraggio();
    }

    private void MostraMessaggioAncoraggio()
    {
        NascondiMessaggioAncoraggio();
        if (canvasTransform == null) return;

        GameObject go = new GameObject("MessaggioAncoraggio", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvasTransform, false);

        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(600f, 400f);

        // Pannello sulla root e testo su un figlio: in UGUI un Graphic figlio si
        // disegna DOPO quello del padre, quindi un pannello figlio coprirebbe il
        // testo. Cosi' l'ordine di disegno e' deterministico.
        ApplicaStilePannello(go.GetComponent<Image>());

        GameObject goTesto = new GameObject("TestoAncoraggio", typeof(RectTransform));
        goTesto.transform.SetParent(go.transform, false);

        RectTransform rtTesto = (RectTransform)goTesto.transform;
        rtTesto.anchorMin = Vector2.zero;
        rtTesto.anchorMax = Vector2.one;
        rtTesto.offsetMin = Vector2.zero;
        rtTesto.offsetMax = Vector2.zero;

        TextMeshProUGUI testo = goTesto.AddComponent<TextMeshProUGUI>();
        testo.font = GameManager.FontApp != null
            ? GameManager.FontApp
            : (instructionalText != null ? instructionalText.font : testo.font);
        testo.fontSize = 30f;
        testo.alignment = TextAlignmentOptions.Center;
        testo.raycastTarget = false;
        testo.color = GameManager.SteelBlue;
        testo.text = "<b>ANCORAGGIO RIUSCITO!</b>\n\nIl menu si aprira' tra 3 secondi...";

        messaggioAncoraggio = go;
    }

    private void NascondiMessaggioAncoraggio()
    {
        if (messaggioAncoraggio != null)
        {
            Destroy(messaggioAncoraggio);
            messaggioAncoraggio = null;
        }
    }

    // Stile dei pannelli, uguale a quelli del tutorial: sfondo bianco, bordi
    // arrotondati e ombra. La sprite arrotondata la chiediamo a GameManager
    // (SpriteArrotondato), che e' gia' il modo in cui i bottoni del tutorial la
    // prendono a runtime; l'ombra e' GameManager.ApplicaPenombra. Se la sprite
    // non arrivasse, l'Image resta un rettangolo bianco semplice.
    private static void ApplicaStilePannello(Image img)
    {
        img.color = Color.white;
        img.raycastTarget = false;

        Sprite s = GameManager.SpriteArrotondato;
        if (s != null)
        {
            img.sprite = s;
            img.type = Image.Type.Sliced;
        }

        GameManager.ApplicaPenombra(img.gameObject);
    }

    // Pannello bianco dietro al testo di calibrazione. Non puo' vivere nella
    // scena (che non si tocca), quindi nasce a runtime come sibling del testo: e'
    // il primo figlio di GruppoCalibrazione, che non ha un Graphic proprio, e
    // per questo l'ordine dei figli basta a renderlo dietro a TestoIstruzioni.
    private void CreaPannelloCalibrazione()
    {
        if (instructionalText == null) return;
        if (pannelloCalibrazione != null) return;

        Transform contenitore = instructionalText.transform.parent;
        if (contenitore == null) return;

        GameObject p = new GameObject("PannelloCalibrazione", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(contenitore, false);

        RectTransform prt = (RectTransform)p.transform;
        prt.anchorMin = new Vector2(0.5f, 0.5f);
        prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.anchoredPosition = Vector2.zero;

        // rectTransform.rect e' la dimensione effettiva del testo (600x400 dalla
        // scena). rect non e' esposto su TextMeshProUGUI, quindi si passa dal
        // RectTransform.
        prt.sizeDelta = instructionalText.rectTransform.rect.size;

        ApplicaStilePannello(p.GetComponent<Image>());

        p.transform.SetAsFirstSibling();

        pannelloCalibrazione = p;
    }

    public void ResettaStatoCalibrazione()
    {
        currentStep = 0;
        lastPinchTime = Time.time;
        PulisciMarkers();

        // Nuova calibrazione: via la regolazione fine in corso e lo spostamento
        // a mano della calibrazione precedente.
        ChiudiRegolazioneFine();
        if (pianoVisualizer != null) pianoVisualizer.ImpostaRegolazioneFine(0f, 1f);

        // --- RIPRISTINO CANVAS ALLO STATO INIZIALE ---
        if (canvasTransform != null && originalCanvasParent != null)
        {
            canvasTransform.SetParent(originalCanvasParent, false);
            canvasTransform.localPosition = originalCanvasLocalPos;
            canvasTransform.localRotation = originalCanvasLocalRot;
            canvasTransform.localScale = originalCanvasLocalScale; // <-- FIX: Ripristina la scala
        }

        if (guideSphere != null) guideSphere.SetActive(true);

        AggiornaTestoIstruzioni();

        if (instructionalText != null)
        {
            instructionalText.transform.parent.gameObject.SetActive(true);
        }

        NascondiMessaggioAncoraggio();

        this.gameObject.SetActive(true);

        Debug.Log("[DEBUG] Calibratore resettato allo stato iniziale.");
    }

    // Colora la sfera guida come lo sticker del punto da prendere.
    private void AggiornaColoreSferaGuida()
    {
        if (guideSphere == null || currentStep >= 3) return;
        Renderer r = guideSphere.GetComponent<Renderer>();
        if (r == null) return;

        Color colore = currentStep == 0 ? ColoreSferaPunto1
                     : currentStep == 1 ? ColoreSferaPunto2
                     : ColoreSferaPunto3;
        // r.material crea una copia: il materiale GreenLight condiviso resta intatto.
        Material m = r.material;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colore); // URP
        m.color = colore;
    }

    private void AggiornaTestoIstruzioni()
    {
        // Il testo cambia a ogni punto preso: insieme cambia il colore della sfera.
        AggiornaColoreSferaGuida();

        if (instructionalText == null) return;

        // Il corpo non taggato ("Muovi la testa per...") prende il colore base,
        // che ora e' nero per contrastare sul pannello bianco. I tag <color=...>
        // gia' presenti nelle stringhe restano invariati e lo sovrascrivono.
        instructionalText.color = Color.black;

        // Nella scena il testo e' TopLeft: sul pannello bianco si legge meglio
        // centrato, e in verticale sfrutta il vuoto sopra e sotto il blocco.
        instructionalText.alignment = TextAlignmentOptions.Center;

        switch (currentStep)
        {
            case 0:
                instructionalText.text = "<color=#FF5555><b>PUNTO 1</b></color>\n\nPorta la pallina che sta davanti alle dita della mano destra sullo sticker <color=#FF5555><b>ROSSO</b></color> (Estremo Sinistro) e fai pinch.";
                break;
            case 1:
                instructionalText.text = "<color=#5555FF><b>PUNTO 2</b></color>\n\nPorta la pallina che sta davanti alle dita della mano destra sullo sticker <color=#5555FF><b>BLU</b></color> (Estremo Destro) e fai pinch.";
                break;
            case 2:
                // Verde e giallo scuriti: sul pannello bianco #55FF55 (1,33:1) e
                // #FFFF55 (1,07:1) non si vedevano piu'. Rosso e blu reggono
                // cosi' come sono (3,14:1 e 5,09:1) e non li ho toccati.
                instructionalText.text = "<color=#2A802A><b>PUNTO 3</b></color>\n\nPorta la pallina che sta davanti alle dita della mano destra sullo sticker <color=#2A802A><b>VERDE</b></color> (Do Centrale) e fai pinch.";
                break;
            case 3:
                instructionalText.text = "<color=#80802A><b>CALIBRAZIONE COMPLETATA!</b></color>";
                break;
        }
    }

    private GameObject CreaMarkerTemporaneo(Vector3 posizione, Material mat)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.position = posizione;
        marker.transform.localScale = Vector3.one * DIAMETRO_SFERE;

        Destroy(marker.GetComponent<SphereCollider>());

        if (mat != null)
        {
            marker.GetComponent<Renderer>().material = mat;
        }
        else
        {
            marker.GetComponent<Renderer>().material.color = Color.yellow;
        }
        return marker;
    }

    private void PulisciMarkers()
    {
        if (markerP1 != null) Destroy(markerP1);
        if (markerP2 != null) Destroy(markerP2);
        if (markerP3 != null) Destroy(markerP3);
    }
}