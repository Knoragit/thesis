using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Alone luminoso ("glow") per i bottoni: crea un figlio "Glow" con un Image che usa
// una sprite procedurale morbida a forma di anello (segue la forma arrotondata del
// bottone). Il centro della sprite e' trasparente, quindi disegnandolo sopra al
// bottone resta visibile solo l'alone esterno: nessun ritocco al colore del bottone.
//
// Essendo un figlio, il glow scala e si nasconde insieme al bottone (sfumature dei
// CanvasGroup comprese). E' sempre visibile a riposo e si intensifica all'hover.
// Il respiro (si allarga e si restringe, con l'opacita' che va su e giu') parte solo
// quando il raggio e' sopra il bottone: a riposo il glow resta fermo.
// Coesiste con IndietroFeedback/KeyFeedback perche' l'EventSystem notifica tutti
// gli handler presenti sullo stesso oggetto.
public class GlowBottone : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public enum FormaGlow { Rettangolare, Circolare }

    // Il glow usa il blu Steel Blue della palette: sui pulsanti Tan e' nettamente
    // visibile (il nero della camera e' l'unico altro fondo reale qui).
    [SerializeField] private Color colore = GameManager.GlowColore;
    [Range(0f, 1f)] [SerializeField] private float alphaRiposo = 0.40f;
    [Range(0f, 1f)] [SerializeField] private float alphaHover = 0.90f;
    [SerializeField] private float velocitaTransizione = 7f;
    [SerializeField] private FormaGlow forma = FormaGlow.Rettangolare;

    // "Respiro" del glow: si attiva solo con il raggio sopra il bottone e muove
    // insieme opacita' e dimensione dell'alone. Ogni bottone ha una fase casuale
    // (sfasamento) per non pulsare in sincrono con gli altri.
    [SerializeField] private bool pulsazione = true;
    [Range(0f, 0.5f)] [SerializeField] private float ampiezzaPulsazione = 0.18f;
    [Range(0f, 0.3f)] [SerializeField] private float ampiezzaScala = 0.12f;
    [SerializeField] private float velocitaPulsazione = 1.6f;

    private Image glowImage;
    private CanvasGroup canvasGroup;
    private RectTransform glowRt;
    private float alphaAttuale;
    private float scalaAttuale = 1f;
    private float sfasamento;
    private bool sopra = false;

    private static Sprite sprRettangolare;
    private static Sprite sprCircolare;

    void Start()
    {
        CreaGlow();
    }

    protected void OnEnable()
    {
        sopra = false;
        alphaAttuale = alphaRiposo;
        // Fase casuale: ogni glow respira con il proprio ritardo.
        sfasamento = Random.value * Mathf.PI * 2f;
        scalaAttuale = 1f;
        if (glowRt != null) glowRt.localScale = Vector3.one;
        ApplicaAlpha();
    }

    protected void OnDisable()
    {
        sopra = false;
        alphaAttuale = alphaRiposo;
        scalaAttuale = 1f;
    }

    void Update()
    {
        if (canvasGroup == null || glowRt == null) return;

        // Il respiro parte solo con il raggio sopra il bottone: a riposo il glow
        // resta fermo (scala 1, opacita' costante).
        float k = (pulsazione && sopra) ? Mathf.Sin((Time.time + sfasamento) * velocitaPulsazione) : 0f;

        float scalaTarget = 1f + k * ampiezzaScala;
        if (!Mathf.Approximately(scalaAttuale, scalaTarget))
        {
            scalaAttuale = Mathf.MoveTowards(scalaAttuale, scalaTarget, velocitaTransizione * Time.deltaTime);
            glowRt.localScale = Vector3.one * scalaAttuale;
        }

        float target = sopra ? Mathf.Clamp01(alphaHover + k * ampiezzaPulsazione) : alphaRiposo;
        if (!Mathf.Approximately(alphaAttuale, target))
        {
            alphaAttuale = Mathf.MoveTowards(alphaAttuale, target, velocitaTransizione * Time.deltaTime);
            ApplicaAlpha();
        }
    }

    public void OnPointerEnter(PointerEventData e)
    {
        sopra = true;
    }

    public void OnPointerExit(PointerEventData e)
    {
        sopra = false;
    }

    // Applica (o recupera) il glow su un bottone. Se colore e' null mantiene quello
    // gia' impostato sul componente.
    public static void Applica(GameObject go, FormaGlow forma = FormaGlow.Rettangolare, Color? colore = null)
    {
        if (go == null || go.GetComponent<Image>() == null) return;

        GlowBottone glow = go.GetComponent<GlowBottone>();
        if (glow == null) glow = go.AddComponent<GlowBottone>();
        glow.forma = forma;
        if (colore.HasValue) glow.colore = colore.Value;
    }

    private void ApplicaAlpha()
    {
        // L'alpha va sul CanvasGroup: applicato a render-time, non ricostruisce il
        // mesh del bottone a ogni frame (importante con molti glow su Quest).
        if (canvasGroup != null) canvasGroup.alpha = alphaAttuale;
    }

    private void CreaGlow()
    {
        if (glowImage != null) return;

        Image bottone = GetComponent<Image>();
        if (bottone == null) return;

        float mult = Mathf.Max(0.01f, bottone.pixelsPerUnitMultiplier);
        // Di quanto il glow sporge oltre il bordo del bottone (unita' UI).
        float espansione = forma == FormaGlow.Circolare ? 14f : Mathf.Ceil(18f / mult) + 2f;

        GameObject go = new GameObject("Glow", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        go.transform.SetAsFirstSibling();

        RectTransform rt = (RectTransform)go.transform;
        glowRt = rt;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(-espansione, -espansione);
        rt.offsetMax = new Vector2(espansione, espansione);
        rt.localScale = Vector3.one;

        glowImage = go.AddComponent<Image>();
        glowImage.raycastTarget = false;

        if (forma == FormaGlow.Circolare)
        {
            glowImage.sprite = SpriteCircolare;
            glowImage.type = Image.Type.Simple;
        }
        else
        {
            glowImage.sprite = SpriteRettangolare;
            glowImage.type = Image.Type.Sliced;
            glowImage.pixelsPerUnitMultiplier = mult;
        }

        // Il colore RGB e' fisso; l'intensita' la modula il CanvasGroup.
        glowImage.color = new Color(colore.r, colore.g, colore.b, 1f);
        canvasGroup = go.AddComponent<CanvasGroup>();
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        ApplicaAlpha();
    }

    private static Sprite SpriteRettangolare
    {
        get { if (sprRettangolare == null) sprRettangolare = CreaSpriteRettangolare(); return sprRettangolare; }
    }

    private static Sprite SpriteCircolare
    {
        get { if (sprCircolare == null) sprCircolare = CreaSpriteCircolare(); return sprCircolare; }
    }

    private static Texture2D NuovaTexture()
    {
        Texture2D tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        return tex;
    }

    // Rettangolo arrotondato: alpha = 0 dentro la forma, alone gaussiano verso
    // l'esterno. Il margine attorno alla forma lascia spazio all'alone e definisce
    // il bordo di 9-slice.
    private static Sprite CreaSpriteRettangolare()
    {
        const int DIM = 128;
        const float MARGINE = 18f;
        const float RAGGIO = 20f;
        const float BETA = 6f;
        float hx = DIM * 0.5f - MARGINE;
        float hy = DIM * 0.5f - MARGINE;

        Texture2D tex = NuovaTexture();
        Color[] px = new Color[DIM * DIM];
        for (int y = 0; y < DIM; y++)
        {
            for (int x = 0; x < DIM; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - DIM * 0.5f);
                float dy = Mathf.Abs(y + 0.5f - DIM * 0.5f);
                float qx = dx - (hx - RAGGIO);
                float qy = dy - (hy - RAGGIO);
                float ax = Mathf.Max(qx, 0f);
                float ay = Mathf.Max(qy, 0f);
                float d = Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - RAGGIO;
                float a = d > 0f ? Mathf.Exp(-(d * d) / (2f * BETA * BETA)) : 0f;
                px[y * DIM + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, true);

        int bordo = Mathf.RoundToInt(MARGINE + RAGGIO);
        return Sprite.Create(tex, new Rect(0, 0, DIM, DIM), new Vector2(0.5f, 0.5f),
                             100f, 0, SpriteMeshType.FullRect, new Vector4(bordo, bordo, bordo, bordo));
    }

    // Cerchio pieno con lo stesso alone esterno (per il pulsante "+").
    private static Sprite CreaSpriteCircolare()
    {
        const int DIM = 128;
        const float MARGINE = 10f;
        const float RAGGIO_FORMA = DIM * 0.5f - MARGINE;
        const float BETA = 6f;
        float centro = DIM * 0.5f;

        Texture2D tex = NuovaTexture();
        Color[] px = new Color[DIM * DIM];
        for (int y = 0; y < DIM; y++)
        {
            for (int x = 0; x < DIM; x++)
            {
                float dx = x + 0.5f - centro;
                float dy = y + 0.5f - centro;
                float d = Mathf.Sqrt(dx * dx + dy * dy) - RAGGIO_FORMA;
                float a = d > 0f ? Mathf.Exp(-(d * d) / (2f * BETA * BETA)) : 0f;
                px[y * DIM + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, DIM, DIM), new Vector2(0.5f, 0.5f), 100f,
                             0, SpriteMeshType.FullRect, Vector4.zero);
    }
}
