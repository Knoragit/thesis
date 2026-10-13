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

    // Distanza teorica in metri tra il tasto più a sinistra e quello più a destra (1.1985m su 88 tasti)
    private const float LARGHEZZA_TEORICA_TASTIERA = 1.1985f;

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

        if (guideSphere != null) guideSphere.SetActive(true);

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

        // Proietta la sfera guida davanti all'utente per permettergli di mirare agli sticker
        Vector3 targetPos = mainCameraTransform.position + mainCameraTransform.forward * 0.6f + Vector3.down * 0.2f;
        guideSphere.transform.position = Vector3.Lerp(guideSphere.transform.position, targetPos, Time.deltaTime * 8f);

        // Orienta la sfera guida in base allo sguardo dell'utente (annullando l'inclinazione verticale)
        Vector3 forwardSguardo = mainCameraTransform.forward;
        forwardSguardo.y = 0;
        if (forwardSguardo != Vector3.zero) guideSphere.transform.rotation = Quaternion.LookRotation(forwardSguardo);
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

        // --- 3. POSIZIONAMENTO E ROTAZIONE SQUADRATA ---
        pianoVisualizer.transform.position = posizioneArretrata;
        pianoVisualizer.transform.rotation = Quaternion.LookRotation(forwardDir, upDir);

        // --- 4. CALCOLO DELLA SCALA DINAMICA ---
        float larghezzaRealeRilevata = Vector3.Distance(p1, p2);
        float fattoreScala = larghezzaRealeRilevata / LARGHEZZA_TEORICA_TASTIERA;

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

        GameManager gm = FindFirstObjectByType<GameManager>();
        if (gm != null)
        {
            StartCoroutine(AttivaMenuDopoCalibrazione());
        }
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

    private void AggiornaTestoIstruzioni()
    {
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
                instructionalText.text = "<color=#FF5555><b>PUNTO 1</b></color>\n\nMuovi la testa per posizionare la pallina verde sullo sticker <color=#FF5555><b>ROSSO</b></color> (Estremo Sinistro) e fai pinch con la mano destra.";
                break;
            case 1:
                instructionalText.text = "<color=#5555FF><b>PUNTO 2</b></color>\n\nMuovi la testa per posizionare la pallina verde sullo sticker <color=#5555FF><b>BLU</b></color> (Estremo Destro) e fai pinch con la mano destra.";
                break;
            case 2:
                // Verde e giallo scuriti: sul pannello bianco #55FF55 (1,33:1) e
                // #FFFF55 (1,07:1) non si vedevano piu'. Rosso e blu reggono
                // cosi' come sono (3,14:1 e 5,09:1) e non li ho toccati.
                instructionalText.text = "<color=#2A802A><b>PUNTO 3</b></color>\n\nMuovi la testa per posizionare la pallina verde sullo sticker <color=#2A802A><b>VERDE</b></color> (Do Centrale) e fai pinch con la mano destra.";
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
        marker.transform.localScale = new Vector3(0.015f, 0.015f, 0.015f);

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