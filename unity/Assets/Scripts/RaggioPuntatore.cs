using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Rendering;

public class RaggioPuntatore : MonoBehaviour
{
    private LineRenderer linea;
    private Transform cursore;
    private Material materialeLinea;
    private Material materialeCursore;
    private Canvas canvas;
    private GameManager gameManager;
    private float prossimoLog = 0f;
    private bool mostravaLaser = false;

    private OVRHand[] mani = new OVRHand[0];
    private Graphic[] grafiche = new Graphic[0];
    private float prossimoRinfresco = 0f;
    private const float INTERVALLO_RINFRESCO = 0.5f;

    private const int LARGHEZZA_CIRCOLO = 64;

    void Start()
    {
        transform.localScale = Vector3.one;
        gameManager = FindFirstObjectByType<GameManager>();

        materialeLinea = CreaMaterialeTrasparente();
        materialeCursore = CreaMaterialeTrasparente();

        GameObject lineaGO = new GameObject("Linea", typeof(LineRenderer));
        lineaGO.transform.SetParent(transform, false);
        linea = lineaGO.GetComponent<LineRenderer>();
        linea.useWorldSpace = true;
        linea.positionCount = 2;
        linea.widthMultiplier = 0.005f;
        linea.startColor = new Color(1f, 1f, 1f, 0.85f);
        linea.endColor = new Color(1f, 1f, 1f, 0.85f);
        linea.numCapVertices = 4;
        if (materialeLinea != null) linea.material = materialeLinea;

        GameObject cursoreGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        cursoreGO.name = "Cursore";
        Collider coll = cursoreGO.GetComponent<Collider>();
        if (coll != null) Destroy(coll);
        cursore = cursoreGO.transform;
        cursore.SetParent(transform, false);
        cursore.localScale = Vector3.one * 0.024f;
        if (materialeCursore != null)
        {
            // Queue più alta della UI (3000): il cursore sta sempre in primo piano.
            materialeCursore.renderQueue = 4000;
            materialeCursore.SetTexture("_BaseMap", CreaTextureCerchio());
            materialeCursore.mainTexture = materialeCursore.GetTexture("_BaseMap");
            cursoreGO.GetComponent<MeshRenderer>().sharedMaterial = materialeCursore;
        }

        Nascondi(false);
    }

    void LateUpdate()
    {
        if (gameManager != null && gameManager.statoAttuale == GameManager.AppState.Onboarding)
        {
            Nascondi(true, "[RAGGIO] Onboarding: laser nascosto");
            return;
        }

        if (canvas == null)
        {
            TrovaCanvas();
            if (canvas == null)
            {
                Nascondi(false);
                return;
            }
        }

        RinfrescaCache();

        Ray ray;
        if (!ProvaRayMano(out ray, out string sorgente))
        {
            Nascondi(true, "[RAGGIO] mano non tracciata / PointerPose non valida");
            return;
        }

        Transform ct = canvas.transform;
        Plane piano = new Plane(ct.forward, ct.position);

        if (!piano.Raycast(ray, out float t) || t <= 0.01f)
        {
            Nascondi(false);
            return;
        }

        Vector3 punto = ray.GetPoint(t);

        if (!InterattivoSottoIlPunto(punto))
        {
            if (Time.time > prossimoLog)
            {
                prossimoLog = Time.time + 1f;
                Debug.Log("[RAGGIO] pannello colpito, nessun elemento interattivo sotto il punto | origine=" + sorgente
                          + " | punto=" + punto.ToString("F3"));
            }
            Nascondi(false);
            return;
        }

        linea.SetPosition(0, ray.origin);
        linea.SetPosition(1, punto);

        cursore.position = punto + ct.forward * 0.008f;
        cursore.rotation = ct.rotation;

        if (!mostravaLaser)
        {
            if (Time.time > prossimoLog)
            {
                prossimoLog = Time.time + 1f;
                Debug.Log("[RAGGIO] laser VISIBILE | origine=" + sorgente
                          + " | punto=" + punto.ToString("F3"));
            }
            mostravaLaser = true;
        }

        linea.enabled = true;
        cursore.gameObject.SetActive(true);
    }

    // Cache delle componenti: FindObjectsByType solo ogni 0.5s, non ogni frame.
    private void RinfrescaCache()
    {
        if (Time.time < prossimoRinfresco) return;
        prossimoRinfresco = Time.time + INTERVALLO_RINFRESCO;
        mani = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
        grafiche = FindObjectsByType<Graphic>(FindObjectsSortMode.None);
    }

    // Forza l'aggiornamento immediato della cache: chiamato quando la UI cambia
    // (lista canzoni ricostruita, menù chiuso/aperto), così il laser non insegue
    // con mezzo secondo di ritardo elementi vecchi/distrutti (sfarfallio).
    public void RinfrescaOra()
    {
        prossimoRinfresco = 0f;
        RinfrescaCache();
    }

    // Preferisce la mano (tra quelle tracciate) il cui raggio interseca già il
    // piano del canvas; se nessuna lo punta, usa la prima mano tracciata valida.
    private bool ProvaRayMano(out Ray ray, out string sorgente)
    {
        ray = default;
        sorgente = null;

        Transform ct = canvas.transform;
        Plane piano = new Plane(ct.forward, ct.position);

        bool hitTrovato = false;
        float miglioreDistanza = float.MaxValue;
        Ray migliore = default;
        string miglioreSorgente = null;

        bool fallbackSet = false;
        Ray fallback = default;
        string fallbackSorgente = null;

        foreach (OVRHand mano in mani)
        {
            if (mano == null || !mano.isActiveAndEnabled) continue;
            if (!mano.IsTracked) continue;
            if (!mano.IsPointerPoseValid) continue;
            Transform pointer = mano.PointerPose;
            if (pointer == null) continue;

            Ray r = new Ray(pointer.position, pointer.forward);
            if (!fallbackSet)
            {
                fallback = r;
                fallbackSorgente = mano.name;
                fallbackSet = true;
            }

            if (piano.Raycast(r, out float t) && t > 0f && (!hitTrovato || t < miglioreDistanza))
            {
                hitTrovato = true;
                miglioreDistanza = t;
                migliore = r;
                miglioreSorgente = mano.name;
            }
        }

        if (hitTrovato)
        {
            ray = migliore;
            sorgente = miglioreSorgente;
            return true;
        }
        if (fallbackSet)
        {
            ray = fallback;
            sorgente = fallbackSorgente;
            return true;
        }
        return false;
    }

    // Qualsiasi elemento UI cliccabile sotto il punto: Graphic attivo con
    // raycastTarget true sullo stesso rootCanvas (bottoni, scrollbar, tasti,
    // campi di testo). La UI world-space non è ritagliata dal rect del canvas,
    // quindi qui non applichiamo alcun limite di rettangolo.
    private bool InterattivoSottoIlPunto(Vector3 punto)
    {
        foreach (Graphic g in grafiche)
        {
            if (g == null || !g.gameObject.activeInHierarchy) continue;
            if (!g.isActiveAndEnabled) continue;
            if (!g.raycastTarget) continue;

            Canvas gc = g.canvas;
            if (gc == null || gc.rootCanvas != canvas.rootCanvas) continue;

            RectTransform rt = g.transform as RectTransform;
            if (rt == null) continue;

            Vector3 locale = rt.InverseTransformPoint(punto);
            if (rt.rect.Contains(new Vector2(locale.x, locale.y)))
            {
                return true;
            }
        }
        return false;
    }

    private void TrovaCanvas()
    {
        canvas = null;
        Canvas[] tutti = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
        foreach (Canvas c in tutti)
        {
            if (c.renderMode != RenderMode.WorldSpace) continue;
            if (c.GetComponent("OVRRaycaster") != null)
            {
                canvas = c;
                return;
            }
        }
        foreach (Canvas c in tutti)
        {
            if (c.renderMode == RenderMode.WorldSpace)
            {
                canvas = c;
                return;
            }
        }
        if (tutti.Length > 0) canvas = tutti[0];
    }

    private void Nascondi(bool log, string motivo = null)
    {
        if (linea != null) linea.enabled = false;
        if (cursore != null) cursore.gameObject.SetActive(false);

        if (mostravaLaser)
        {
            if (log && motivo != null && Time.time > prossimoLog)
            {
                prossimoLog = Time.time + 1f;
                Debug.Log(motivo);
            }
            mostravaLaser = false;
        }
    }

    private void OnDisable()
    {
        if (linea != null) linea.enabled = false;
        if (cursore != null) cursore.gameObject.SetActive(false);
    }

    private Material CreaMaterialeTrasparente()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");

        Material mat = new Material(sh);
        if (sh != null && sh.name.StartsWith("Universal Render Pipeline"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        mat.color = new Color(1f, 1f, 1f, 0.9f);
        mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.9f));
        return mat;
    }

    private Texture2D CreaTextureCerchio()
    {
        Texture2D tex = new Texture2D(LARGHEZZA_CIRCOLO, LARGHEZZA_CIRCOLO, TextureFormat.RGBA32, false);
        float centro = (LARGHEZZA_CIRCOLO - 1) * 0.5f;
        float raggio = centro - 2f;
        Color[] px = new Color[LARGHEZZA_CIRCOLO * LARGHEZZA_CIRCOLO];
        for (int y = 0; y < LARGHEZZA_CIRCOLO; y++)
        {
            for (int x = 0; x < LARGHEZZA_CIRCOLO; x++)
            {
                float dist = Mathf.Sqrt((x - centro) * (x - centro) + (y - centro) * (y - centro));
                float a = Mathf.Clamp01(raggio + 1f - dist);
                px[y * LARGHEZZA_CIRCOLO + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }
}