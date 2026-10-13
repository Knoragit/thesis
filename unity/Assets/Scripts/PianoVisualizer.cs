using UnityEngine;
using System.Collections.Generic;
using TMPro;

public class PianoVisualizer : MonoBehaviour
{
    private class NoteState
    {
        public GameObject rootObject;     // Contenitore neutro (Scala 1:1)
        public GameObject columnObject;   // Sagoma colonna (si ridimensiona)
        public Material columnMaterial;   // Materiale sagoma (sfumatura colore)
        public ParticleSystem particles;  // Colonna di particelle (rombi)
        public GameObject textObject;     // Etichetta testo (proporzionata)
        public float currentHeight;
        public bool isPressed;
        public bool isLeftHand;
        public float releaseTimer;
        public float pressTimer;
    }

    private Dictionary<int, NoteState> activeNotes = new Dictionary<int, NoteState>();
    private const float LARGHEZZA_TASTO_BIANCO = 0.0235f;
    private const int NOTA_INIZIALE_MIDI = 21; // La0 (A0)
    private const int NOTA_MAX_MIDI = 108;     // Do8 (C8), cima del pianoforte a 88 tasti
    private const int LIMITE_COLONNE_MASSIME = 64;

    private readonly int[] whiteKeyOffsets = { 0, 0, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6 };

    private Dictionary<int, GameObject> expectedVisualObjects = new Dictionary<int, GameObject>();
    // Tutte le guide visive create (anche quelle "orfane" quando lo stesso MIDI
    // compare due volte in un accordo): PulisciNoteAtteseVisive distrugge l'intera
    // lista, cosi' nessuna colonna verde puo' restare dopo la pulizia.
    private readonly List<GameObject> expectedVisualList = new List<GameObject>();

    [Header("Riferimenti Tracciamento Mani Meta")]
    public Transform leftHandTransform;
    public Transform rightHandTransform;

    [Header("Filo Articolazione Morbido")]
    private LineRenderer leftLineRenderer;
    private LineRenderer rightLineRenderer;
    private LineRenderer leftHaloRenderer;
    private LineRenderer rightHaloRenderer;
    public int risoluzioneCurva = 12;
    public float frecciaCurvaturaGravita = 0.05f;

    [Header("Impostazioni Fili Di Luce")]
    public float filoIntensita = 2.0f;
    public float filoWidthCore = 0.004f;
    public float filoWidthHalo = 0.02f;
    public float filoHaloIntensita = 0.5f;

    [Header("Parametri di Decadimento Temporale")]
    public float durataDecadimentoRilascio = 1.0f;
    public float durataMaxNotaPremuta = 12.0f;

    [Header("Mesh Colonna (guide note attese)")]
    public Mesh columnMesh;

    // Le guide delle note attese (modalita' Seguimi) usano due verdi distinti:
    // i tasti bianchi restano sul verde acido di sempre, i tasti neri su un verde
    // piu' scuro della stessa tinta. L'alpha dei neri e' piu' alto perche' a 0.15
    // un verde scurito sparisce praticamente sullo sfondo quasi nero.
    [Header("Guide Note Attese (modalita' Seguimi)")]
    public Color verdeGuidaTastoBianco = new Color(0.0f, 1.0f, 0.35f, 0.15f);
    public Color verdeGuidaTastoNero = new Color(0.0f, 0.55f, 0.19f, 0.22f);
    public Color verdeEtichettaTastoBianco = Color.green;
    public Color verdeEtichettaTastoNero = new Color(0.0f, 0.55f, 0.0f, 1f);

    [Header("Sagoma Colonna")]
    public float traslucenzaColonna = 0.5f;

    [Header("Particelle Colonna (rombi)")]
    public Mesh particleMesh;
    public Material particleMaterial;
    public float particleRate = 80f;
    public int maxParticlesColonna = 300;
    public float particleSize = 0.004f;
    public float particleLifetime = 1.2f;
    public float burstIntensita = 28f;
    public float particellaLuminosita = 1.6f;
    public float riempimentoSpacing = 0.008f;

    [Header("Impostazioni Nomi Note")]
    public bool usaNotazioneItaliana = true; // true = Do, Re, Mi | false = C, D, E

    private List<int> noteFiltrateId = new List<int>();
    private List<Vector3> puntiControllo = new List<Vector3>();
    private List<Vector3> splinePunti = new List<Vector3>();

    void Start()
    {
        OVRCameraRig cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null)
        {
            leftHandTransform = cameraRig.leftHandAnchor;
            rightHandTransform = cameraRig.rightHandAnchor;
        }
        InizializzaLineRenderers();
    }

    private void InizializzaLineRenderers()
    {
        leftLineRenderer = CreaLineRenderer("Linea_Morbida_Sinistra");
        rightLineRenderer = CreaLineRenderer("Linea_Morbida_Destra");
        leftHaloRenderer = CreaLineRenderer("Alone_Morbido_Sinistra");
        rightHaloRenderer = CreaLineRenderer("Alone_Morbido_Destra");

        ConfiguraLineRenderer(leftLineRenderer, Color.cyan, filoWidthCore, 1f);
        ConfiguraLineRenderer(rightLineRenderer, Color.yellow, filoWidthCore, 1f);
        ConfiguraLineRenderer(leftHaloRenderer, Color.cyan, filoWidthHalo, filoHaloIntensita);
        ConfiguraLineRenderer(rightHaloRenderer, Color.yellow, filoWidthHalo, filoHaloIntensita);
    }

    private LineRenderer CreaLineRenderer(string nome)
    {
        return new GameObject(nome).AddComponent<LineRenderer>();
    }

    private void ConfiguraLineRenderer(LineRenderer lr, Color c, float larghezza, float fattoreIntensita)
    {
        lr.material = CreaMaterialeFiloLuce(c, fattoreIntensita);
        lr.useWorldSpace = true;
        lr.numCapVertices = 6;
        lr.numCornerVertices = 4;
        ApplicaProfiloLuce(lr, larghezza);
    }

    private Material CreaMaterialeFiloLuce(Color c, float fattoreIntensita)
    {
        Shader s = Shader.Find("Universal Render Pipeline/Unlit");
        Material m = s != null ? new Material(s) : new Material(Shader.Find("Sprites/Default"));
        if (s != null)
        {
            Color baseLuce = new Color(c.r * filoIntensita * fattoreIntensita, c.g * filoIntensita * fattoreIntensita, c.b * filoIntensita * fattoreIntensita, 1f);
            m.SetColor("_BaseColor", baseLuce);
            m.SetColor("_Color", baseLuce);
            m.SetFloat("_Surface", 1);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_BLENDMODE_ADD");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        return m;
    }

    private void ApplicaProfiloLuce(LineRenderer lr, float larghezzaBase)
    {
        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0.35f),
            new Keyframe(0.5f, 1f),
            new Keyframe(1f, 0.35f)
        );
        lr.widthMultiplier = larghezzaBase;
    }

    public void ResetVisualizer()
    {
        foreach (var kvp in activeNotes)
        {
            if (kvp.Value.rootObject != null)
            {
                Destroy(kvp.Value.rootObject);
            }
        }
        activeNotes.Clear();
        if (leftLineRenderer != null) leftLineRenderer.positionCount = 0;
        if (rightLineRenderer != null) rightLineRenderer.positionCount = 0;
        if (leftHaloRenderer != null) leftHaloRenderer.positionCount = 0;
        if (rightHaloRenderer != null) rightHaloRenderer.positionCount = 0;
        PulisciNoteAtteseVisive();
        Debug.Log("[VISUALIZER] Schermo pulito e colonne azzerate (incluse note attese).");
    }

    public void MostraNoteAttese(int[] notes)
    {
        PulisciNoteAtteseVisive();

        if (notes == null) return;

        foreach (int note in notes)
        {
            GameObject radiceGuida = new GameObject($"Guida_Nota_{note}");
            radiceGuida.transform.SetParent(this.transform, false);
            radiceGuida.transform.localPosition = new Vector3(CalcolaX(note), 0f, 0f);

            GameObject colonnaGuida = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (columnMesh != null) colonnaGuida.GetComponent<MeshFilter>().mesh = columnMesh;
            colonnaGuida.transform.SetParent(radiceGuida.transform, false);
            Destroy(colonnaGuida.GetComponent<BoxCollider>());

            Renderer r = colonnaGuida.GetComponent<Renderer>();

            Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            bool tastoNero = IsTastoNero(note);
            mat.color = tastoNero ? verdeGuidaTastoNero : verdeGuidaTastoBianco;
            r.material = mat;

            float altezzaGuida = 0.15f;
            float spessore = tastoNero ? 0.009f : 0.016f;

            colonnaGuida.transform.localScale = new Vector3(spessore, altezzaGuida, spessore);
            colonnaGuida.transform.localPosition = new Vector3(0f, altezzaGuida * 0.5f, 0f);

            CreaEtichettaTesto(radiceGuida, ConvertiMidiInNomeNota(note), tastoNero ? verdeEtichettaTastoNero : verdeEtichettaTastoBianco);

            expectedVisualObjects[note] = radiceGuida;
            expectedVisualList.Add(radiceGuida);
        }
    }

    public void PulisciNoteAtteseVisive()
    {
        foreach (var go in expectedVisualList)
        {
            if (go != null) Destroy(go);
        }
        expectedVisualList.Clear();
        expectedVisualObjects.Clear();
    }

    void Update()
    {
        List<int> toRemove = new List<int>();
        foreach (var kvp in activeNotes)
        {
            NoteState state = kvp.Value;

            if (state.isPressed)
            {
                state.pressTimer += Time.deltaTime;
                if (state.pressTimer >= durataMaxNotaPremuta)
                {
                    // Nota MIDI rimasta premuta senza rilascio (o esecuzione
                    // interrotta): forziamo il decay per non lasciare colonne
                    // congelate sul piano visivo.
                    state.isPressed = false;
                    state.releaseTimer = 0f;
                }
            }

            if (!state.isPressed)
            {
                state.releaseTimer += Time.deltaTime;
                float t = state.releaseTimer / durataDecadimentoRilascio;
                state.currentHeight = Mathf.Lerp(state.currentHeight, 0f, t);
            }

            if (state.currentHeight <= 0.005f || (state.releaseTimer >= durataDecadimentoRilascio && !state.isPressed))
            {
                Destroy(state.rootObject);
                toRemove.Add(kvp.Key);
            }
            else
            {
                float spessore = IsTastoNero(kvp.Key) ? 0.009f : 0.016f;

                if (state.columnObject != null)
                {
                    state.columnObject.transform.localScale = new Vector3(spessore, state.currentHeight, spessore);
                    state.columnObject.transform.localPosition = new Vector3(0f, state.currentHeight * 0.5f, 0f);
                }

                if (state.particles != null)
                {
                    AggiornaShapeParticelle(state, kvp.Key, state.currentHeight);
                }
            }
        }

        foreach (int id in toRemove) activeNotes.Remove(id);

        DisegnaFiloGravitazionale(true, leftLineRenderer, leftHaloRenderer);
        DisegnaFiloGravitazionale(false, rightLineRenderer, rightHaloRenderer);

        AggiornaPulsazioneFili();
    }

    private void AggiornaPulsazioneFili()
    {
        float pulsazione = activeNotes.Count > 0 ? 1f + 0.1f * Mathf.Sin(Time.time * 2f * Mathf.PI * 0.5f) : 1f;
        if (leftLineRenderer != null) leftLineRenderer.widthMultiplier = filoWidthCore * pulsazione;
        if (rightLineRenderer != null) rightLineRenderer.widthMultiplier = filoWidthCore * pulsazione;
        if (leftHaloRenderer != null) leftHaloRenderer.widthMultiplier = filoWidthHalo * pulsazione;
        if (rightHaloRenderer != null) rightHaloRenderer.widthMultiplier = filoWidthHalo * pulsazione;
    }

    public void OnNoteReceived(int note, float velocity, string action)
    {
        if (note < NOTA_INIZIALE_MIDI || note > NOTA_MAX_MIDI) return;
        if (float.IsNaN(velocity) || float.IsInfinity(velocity)) velocity = 0f;

        if (action == "release")
        {
            if (activeNotes.ContainsKey(note))
            {
                NoteState st = activeNotes[note];
                st.isPressed = false;
                st.releaseTimer = 0f;
                st.pressTimer = 0f;
                if (st.particles != null) st.particles.Stop();
            }
            return;
        }

        if (!activeNotes.ContainsKey(note))
        {
            if (activeNotes.Count >= LIMITE_COLONNE_MASSIME) return;

            NoteState s = new NoteState();

            s.rootObject = new GameObject($"Nota_{note}");
            s.rootObject.transform.SetParent(this.transform, false);
            s.rootObject.transform.localPosition = new Vector3(CalcolaX(note), 0f, 0f);

            s.isLeftHand = DeterminaMano(note, s.rootObject.transform.position);
            NotificaManoSeNecessario(note, s.isLeftHand);

            CreaSagomaColonna(s);
            ConfiguraColonnaParticelle(s);

            s.textObject = CreaEtichettaTesto(s.rootObject, ConvertiMidiInNomeNota(note), Color.white);

            activeNotes[note] = s;
        }

        NoteState state = activeNotes[note];
        state.isPressed = true;
        state.releaseTimer = 0f;
        state.pressTimer = 0f;

        float altezzaMassimaColonne = 0.6f;
        float velocityCalibrata = velocity;

        GameManager gm = FindFirstObjectByType<GameManager>();
        if (gm != null && (gm.statoAttuale == GameManager.AppState.FaiTu || gm.statoAttuale == GameManager.AppState.Tutorial))
        {
            velocityCalibrata = Mathf.Pow(velocity, 1.1f) * 1.3f;
        }
        else
        {
            velocityCalibrata = Mathf.Pow(velocity, 1.4f);
        }

        velocityCalibrata = Mathf.Clamp01(velocityCalibrata);
        // Altezza minima visibile: le pressioni molto tenere ("piano") generano
        // colonne di pochi millimetri, sotto la soglia di rimozione (0.005):
        // la nota sembrava non apparire. Piccola ma sempre visibile finche' tenuta.
        state.currentHeight = Mathf.Max(altezzaMassimaColonne * velocityCalibrata, 0.02f);

        ColoraColonna(state, velocityCalibrata);

        if (state.particles != null)
        {
            AggiornaShapeParticelle(state, note, state.currentHeight);

            state.particles.Play();
            float spessore = IsTastoNero(note) ? 0.009f : 0.016f;
            int nx = Mathf.Max(1, Mathf.CeilToInt(spessore / riempimentoSpacing));
            int ny = Mathf.Max(1, Mathf.CeilToInt(state.currentHeight / riempimentoSpacing));
            int fillVolume = Mathf.Clamp(nx * nx * ny, 1, maxParticlesColonna);
            state.particles.Emit(fillVolume);
        }
    }

    private void AggiornaShapeParticelle(NoteState state, int nota, float height)
    {
        if (state.particles == null) return;
        float spessore = IsTastoNero(nota) ? 0.009f : 0.016f;
        float hEff = Mathf.Max(0.004f, height);
        var shape = state.particles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(spessore, hEff, spessore);
        shape.position = new Vector3(0f, hEff * 0.5f, 0f);
    }

    private void DisegnaFiloGravitazionale(bool isLeft, LineRenderer lrCore, LineRenderer lrHalo)
    {
        noteFiltrateId.Clear();
        puntiControllo.Clear();
        splinePunti.Clear();

        foreach (var kvp in activeNotes)
        {
            if (kvp.Value.isLeftHand == isLeft && kvp.Value.rootObject != null)
            {
                noteFiltrateId.Add(kvp.Key);
            }
        }

        if (noteFiltrateId.Count < 2)
        {
            ApplicaSpline(lrCore, null);
            ApplicaSpline(lrHalo, null);
            return;
        }
        noteFiltrateId.Sort();

        float chordThreshold = 0.08f;
        HashSet<int> chordNoteIds = new HashSet<int>();
        for (int i = 0; i < noteFiltrateId.Count; i++)
        {
            NoteState si = activeNotes[noteFiltrateId[i]];
            for (int j = i + 1; j < noteFiltrateId.Count; j++)
            {
                NoteState sj = activeNotes[noteFiltrateId[j]];
                if (Mathf.Abs(si.pressTimer - sj.pressTimer) < chordThreshold)
                {
                    chordNoteIds.Add(noteFiltrateId[i]);
                    chordNoteIds.Add(noteFiltrateId[j]);
                }
            }
        }

        List<int> legatoIds = new List<int>();
        foreach (int id in noteFiltrateId)
        {
            if (!chordNoteIds.Contains(id))
                legatoIds.Add(id);
        }

        if (legatoIds.Count < 2)
        {
            ApplicaSpline(lrCore, null);
            ApplicaSpline(lrHalo, null);
            return;
        }
        noteFiltrateId = legatoIds;

        for (int i = 0; i < noteFiltrateId.Count; i++)
        {
            NoteState s = activeNotes[noteFiltrateId[i]];
            Vector3 sommitaColonna = s.rootObject.transform.position + Vector3.up * s.currentHeight;

            if (i > 0)
            {
                Vector3 puntoPrecedente = puntiControllo[puntiControllo.Count - 1];
                Vector3 centroMorbido = Vector3.Lerp(puntoPrecedente, sommitaColonna, 0.5f);
                centroMorbido.y -= frecciaCurvaturaGravita;
                puntiControllo.Add(centroMorbido);
            }
            puntiControllo.Add(sommitaColonna);
        }

        if (puntiControllo.Count < 2) return;

        Vector3 pInizioFantasma = puntiControllo[0] - (puntiControllo[1] - puntiControllo[0]);
        Vector3 pFineFantasma = puntiControllo[puntiControllo.Count - 1] + (puntiControllo[puntiControllo.Count - 1] - puntiControllo[puntiControllo.Count - 2]);

        puntiControllo.Insert(0, pInizioFantasma);
        puntiControllo.Add(pFineFantasma);

        for (int i = 0; i < puntiControllo.Count - 3; i++)
        {
            for (int j = 0; j < risoluzioneCurva; j++)
            {
                float t = (float)j / (float)risoluzioneCurva;
                splinePunti.Add(CalcolaCatmullRom(puntiControllo[i], puntiControllo[i + 1], puntiControllo[i + 2], puntiControllo[i + 3], t));
            }
        }
        splinePunti.Add(puntiControllo[puntiControllo.Count - 2]);

        ApplicaSpline(lrCore, splinePunti);
        ApplicaSpline(lrHalo, splinePunti);
    }

    private void ApplicaSpline(LineRenderer lr, List<Vector3> punti)
    {
        if (lr == null) return;
        if (punti == null || punti.Count == 0) { lr.positionCount = 0; return; }
        lr.positionCount = punti.Count;
        for (int i = 0; i < punti.Count; i++) lr.SetPosition(i, punti[i]);
    }

    private void ConfiguraColonnaParticelle(NoteState s)
    {
        s.particles = s.rootObject.AddComponent<ParticleSystem>();

        var main = s.particles.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = particleLifetime;
        main.startSpeed = 0f;
        main.startSize = 0.5f;
        main.maxParticles = maxParticlesColonna;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startRotation3D = true;
        main.startRotationX = 0f;
        main.startRotationY = 0f;
        main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, 360f);
        main.flipRotation = 0.5f;

        var emission = s.particles.emission;
        emission.enabled = true;
        emission.rateOverTime = particleRate;

        var shape = s.particles.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(0.012f, 0.6f, 0.012f);

        var noise = s.particles.noise;
        noise.enabled = true;
        noise.strength = 0.02f;
        noise.frequency = 1.5f;
        noise.octaveCount = 2;
        noise.quality = ParticleSystemNoiseQuality.High;
        noise.scrollSpeed = 0.5f;

        var renderer = s.particles.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        Mesh mesh = CaricaMeshParticella();
        if (mesh != null)
        {
            renderer.mesh = mesh;
            main.startSize = CalcolaDimensioneParticella(mesh);
        }
        renderer.material = CreaMaterialeParticelle();
    }

    private float CalcolaDimensioneParticella(Mesh mesh)
    {
        float dimMax = Mathf.Max(1e-4f, Mathf.Max(
            mesh.bounds.size.x,
            Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z)));
        return particleSize / dimMax;
    }

    private Mesh CaricaMeshParticella()
    {
        if (particleMesh != null) return particleMesh;
        return Resources.Load<Mesh>("Models/Rhombus_Particle");
    }

    private Material CreaMaterialeParticelle()
    {
        if (particleMaterial != null) return particleMaterial;
        Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        Material m = s != null ? new Material(s) : new Material(Shader.Find("Sprites/Default"));
        if (s != null)
        {
            m.SetColor("_BaseColor", Color.white);
            m.SetColor("_Color", Color.white);
            m.SetFloat("_Surface", 1);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_BLENDMODE_ADD");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        return m;
    }

    private void ColoraColonna(NoteState state, float velocityCalibrata)
    {
        Color startColor = state.isLeftHand ? Color.cyan : Color.yellow;
        Color endColor = state.isLeftHand ? Color.blue : Color.red;
        Color coloreSfumato = Color.Lerp(startColor, endColor, velocityCalibrata);

        if (state.columnMaterial != null)
        {
            Color coloreFascio = coloreSfumato * 1.8f;
            state.columnMaterial.color = new Color(coloreFascio.r, coloreFascio.g, coloreFascio.b, traslucenzaColonna);
        }

        if (state.particles != null)
        {
            var main = state.particles.main;
            main.startColor = new ParticleSystem.MinMaxGradient(coloreSfumato, coloreSfumato * particellaLuminosita);
        }
    }

    private void CreaSagomaColonna(NoteState s)
    {
        s.columnObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        if (columnMesh != null) s.columnObject.GetComponent<MeshFilter>().mesh = columnMesh;

        s.columnObject.transform.SetParent(s.rootObject.transform, false);
        Destroy(s.columnObject.GetComponent<BoxCollider>());

        s.columnObject.transform.localScale = new Vector3(0.012f, 0.6f, 0.012f);

        s.columnMaterial = s.columnObject.GetComponent<Renderer>().material;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh != null) s.columnMaterial.shader = sh;
        s.columnMaterial.SetFloat("_Surface", 1);
        s.columnMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        s.columnMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        s.columnMaterial.SetInt("_ZWrite", 0);
        s.columnMaterial.DisableKeyword("_ALPHATEST_ON");
        s.columnMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        s.columnMaterial.EnableKeyword("_BLENDMODE_ADD");
        s.columnMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        s.columnMaterial.color = new Color(0.5f, 0.9f, 0.9f, traslucenzaColonna);
    }

    private Vector3 CalcolaCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t; float t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private void NotificaManoSeNecessario(int nota, bool manoSinistra)
    {
        GameManager gm = FindFirstObjectByType<GameManager>();
        if (gm == null) return;
        if (gm.statoAttuale != GameManager.AppState.FaiTu && gm.statoAttuale != GameManager.AppState.Tutorial) return;

        UdpReceiver receiver = FindFirstObjectByType<UdpReceiver>();
        if (receiver == null) return;
        receiver.InviaNotaDaFaiTu(nota, manoSinistra);
    }

    private bool DeterminaMano(int note, Vector3 posColonnaMondo)
    {
        GameManager gm = FindFirstObjectByType<GameManager>();

        if (gm != null && (gm.statoAttuale == GameManager.AppState.Osservatore || gm.statoAttuale == GameManager.AppState.Seguimi))
        {
            return note < 60;
        }

        if (leftHandTransform == null || rightHandTransform == null) return posColonnaMondo.x < transform.position.x;
        return Vector3.Distance(posColonnaMondo, leftHandTransform.position) < Vector3.Distance(posColonnaMondo, rightHandTransform.position);
    }

    public bool IsTastoNero(int n)
    {
        int notaInOttava = n % 12;
        return (notaInOttava == 1 || notaInOttava == 3 || notaInOttava == 6 || notaInOttava == 8 || notaInOttava == 10);
    }

    private float CalcolaX(int n)
    {
        int rel = n - NOTA_INIZIALE_MIDI;
        int tastiBianchiPrecedenti = (rel / 12) * 7 + whiteKeyOffsets[rel % 12];
        float x = tastiBianchiPrecedenti * LARGHEZZA_TASTO_BIANCO;

        if (IsTastoNero(n))
        {
            x += (LARGHEZZA_TASTO_BIANCO / 2f);
        }
        return x;
    }

    private string ConvertiMidiInNomeNota(int n)
    {
        string[] nomiITA = { "Do", "Do#", "Re", "Re#", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "La#", "Si" };
        string[] nomiENG = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        string nome = usaNotazioneItaliana ? nomiITA[n % 12] : nomiENG[n % 12];

        return nome; // Ora restituisce solo il nome della nota, senza numero dell'ottava
    }

    private GameObject CreaEtichettaTesto(GameObject radicePadre, string testoNota, Color coloreTesto)
    {
        GameObject testoObj = new GameObject("Etichetta_Nota");
        testoObj.transform.SetParent(radicePadre.transform, false);

        testoObj.transform.localPosition = new Vector3(0f, 0.005f, -0.02f);
        testoObj.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);

        TextMeshPro tmp = testoObj.AddComponent<TextMeshPro>();
        tmp.text = testoNota;
        if (GameManager.FontApp != null) tmp.font = GameManager.FontApp;
        tmp.fontSize = 0.12f; // Dimensione ridotta per evitare sovrapposizioni tra tasti vicini
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = coloreTesto;
        tmp.fontStyle = FontStyles.Bold;
        tmp.raycastTarget = false;

        return testoObj;
    }
}