using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Barra del tempo della modalita' Osservatore: mostra a che punto e' il brano e
// permette di farlo ripartire da un altro punto trascinando o cliccando, come su
// YouTube. Compare solo dopo aver scelto una canzone (al primo "song_progress"
// del bridge) e sparisce quando la riproduzione viene fermata (bottone "+",
// "Torna al Menu", Cerca).
//
// Vive sul Canvas radice e non dentro il gruppo Osservatore: il gruppo e' scalato
// x4 e cambia altezza durante l'animazione del menu, e la barra si sposterebbe.
// Sta accanto al "+", alla sua stessa altezza, e arriva al massimo fino al
// bordo destro della tastiera.
public class BarraTempoOsservatore : MonoBehaviour
{
    // Tastiera di 88 tasti nelle unita' locali del PianoVisualizer (da La0 a Do8).
    private const float LARGHEZZA_TASTIERA = 1.1985f;
    // Altezza della barra rispetto al diametro del "+".
    private const float FRAZIONE_ALTEZZA_PLUS = 0.6f;
    // Spazio tra il "+" e la barra, in unita' del Canvas.
    private const float SPAZIO_DAL_PLUS = 40f;
    // Dopo un salto il bridge manda ancora per un attimo la vecchia posizione:
    // la ignoriamo, altrimenti la maniglia tornerebbe indietro per un istante.
    private const float PAUSA_DOPO_SALTO = 0.6f;

    // Misure di progetto del pannello: tutto e' disegnato in queste unita' e poi
    // il pannello viene scalato alla larghezza voluta sopra la tastiera.
    private const float LARGHEZZA_PROGETTO = 1000f;
    private const float ALTEZZA_PROGETTO = 130f;
    // Sotto questa larghezza (sempre in unita' di progetto) la barra non si stringe.
    private const float LARGHEZZA_MINIMA_PROGETTO = 500f;

    private RectTransform canvasRt;
    private PianoVisualizer visualizer;
    private UdpReceiver receiver;
    private System.Func<RectTransform> plus;

    private RectTransform pannello;
    private SliderTempo slider;
    private TextMeshProUGUI testoPosizione;
    private TextMeshProUGUI testoDurata;

    private bool abilitata = false;
    private float durata = 0f;
    private float ignoraFinoA = 0f;

    public void Inizializza(Canvas canvas, System.Func<RectTransform> bottonePlus)
    {
        if (canvas == null) return;
        canvasRt = (RectTransform)canvas.rootCanvas.transform;
        plus = bottonePlus;
        CreaPannello();
        pannello.gameObject.SetActive(false);
    }

    // Chiamata quando si sceglie una canzone in Osservatore: la barra comparira'
    // con la prima posizione inviata dal bridge.
    public void Abilita()
    {
        abilitata = true;
        durata = 0f;
        ignoraFinoA = 0f;
    }

    public void Disabilita()
    {
        abilitata = false;
        if (pannello != null) pannello.gameObject.SetActive(false);
    }

    public void Progresso(float posizione, float durataBrano)
    {
        if (!abilitata || pannello == null || durataBrano <= 0f) return;

        durata = durataBrano;
        testoDurata.text = FormattaTempo(durata);

        if (!pannello.gameObject.activeSelf)
        {
            pannello.gameObject.SetActive(true);
            pannello.SetAsLastSibling();
            Posiziona();
            RectTransform area = (RectTransform)slider.transform;
            Debug.Log($"[BARRA TEMPO] visibile: centro={area.position.ToString("F3")} larghezza={area.rect.width * area.lossyScale.x:F3}m altezza={area.rect.height * area.lossyScale.y:F3}m");
            RaggioPuntatore raggio = FindFirstObjectByType<RaggioPuntatore>();
            if (raggio != null) raggio.RinfrescaOra();
        }

        if (slider.Trascinando || Time.unscaledTime < ignoraFinoA) return;
        slider.SetValueWithoutNotify(Mathf.Clamp01(posizione / durata));
        testoPosizione.text = FormattaTempo(posizione);
    }

    void LateUpdate()
    {
        if (pannello == null || !pannello.gameObject.activeSelf) return;
        // "Indietro" e "+" si muovono con l'animazione del menu: ricalcolo leggero.
        Posiziona();
        if (slider.Trascinando) testoPosizione.text = FormattaTempo(slider.value * durata);
    }

    private void SaltaA(float valore)
    {
        if (durata <= 0f) return;
        if (receiver == null) receiver = FindFirstObjectByType<UdpReceiver>();
        if (receiver == null) return;

        float secondi = valore * durata;
        testoPosizione.text = FormattaTempo(secondi);
        ignoraFinoA = Time.unscaledTime + PAUSA_DOPO_SALTO;
        receiver.InviaComandoSaltaA(secondi);
    }

    // ---------------------------------------------------------------
    // POSIZIONE
    // ---------------------------------------------------------------
    private void Posiziona()
    {
        if (visualizer == null) visualizer = FindFirstObjectByType<PianoVisualizer>();
        RectTransform rtPlus = plus != null ? plus() : null;
        if (visualizer == null || canvasRt == null || rtPlus == null) return;

        Rect r = RectSulCanvas(rtPlus);
        float altezza = r.height * FRAZIONE_ALTEZZA_PLUS;
        if (altezza < 1f) return;
        float scala = altezza / ALTEZZA_PROGETTO;

        // Dal "+" fino al bordo destro della tastiera, senza superare la misura di progetto.
        float sinistra = r.xMax + SPAZIO_DAL_PLUS;
        Vector3 fineTastiera = canvasRt.InverseTransformPoint(visualizer.transform.TransformPoint(new Vector3(LARGHEZZA_TASTIERA, 0f, 0f)));
        float larghezza = Mathf.Min(fineTastiera.x - sinistra, LARGHEZZA_PROGETTO * scala);
        larghezza = Mathf.Max(larghezza, LARGHEZZA_MINIMA_PROGETTO * scala);

        pannello.sizeDelta = new Vector2(larghezza / scala, ALTEZZA_PROGETTO);
        pannello.localScale = new Vector3(scala, scala, 1f);
        pannello.localRotation = Quaternion.identity;
        pannello.localPosition = new Vector3(sinistra + larghezza * 0.5f, r.center.y, 0f);
    }

    // Rettangolo di un elemento nelle coordinate del Canvas, senza la sua scala
    // propria: il "+" cresce da 0 durante l'animazione e conta la misura finale.
    private Rect RectSulCanvas(RectTransform rt)
    {
        Transform genitore = rt.parent;
        Rect r = rt.rect;
        Vector3 a = rt.localPosition + new Vector3(r.xMin, r.yMin, 0f);
        Vector3 b = rt.localPosition + new Vector3(r.xMax, r.yMax, 0f);
        if (genitore != null)
        {
            a = canvasRt.InverseTransformPoint(genitore.TransformPoint(a));
            b = canvasRt.InverseTransformPoint(genitore.TransformPoint(b));
        }
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // ---------------------------------------------------------------
    // GRAFICA
    // ---------------------------------------------------------------
    private void CreaPannello()
    {
        Sprite arrotondato = GameManager.SpriteArrotondato;

        GameObject p = new GameObject("BarraTempoOsservatore", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(canvasRt, false);
        pannello = (RectTransform)p.transform;
        pannello.anchorMin = new Vector2(0.5f, 0.5f);
        pannello.anchorMax = new Vector2(0.5f, 0.5f);
        pannello.pivot = new Vector2(0.5f, 0.5f);
        pannello.sizeDelta = new Vector2(LARGHEZZA_PROGETTO, ALTEZZA_PROGETTO);

        // Sfondo Steel Blue arrotondato con ombra, come gli altri pannelli.
        Image sfondo = p.GetComponent<Image>();
        sfondo.color = GameManager.SteelBlue;
        sfondo.raycastTarget = false;
        if (arrotondato != null)
        {
            sfondo.sprite = arrotondato;
            sfondo.type = Image.Type.Sliced;
        }
        GameManager.ApplicaPenombra(p);

        // Tempi ai due lati e slider in mezzo: la larghezza del pannello cambia con
        // lo spazio disponibile, quindi tutto e' ancorato ai bordi.
        testoPosizione = CreaTesto(pannello, "Posizione", 0f, 75f, "0:00");
        testoDurata = CreaTesto(pannello, "Durata", 1f, -75f, "0:00");
        slider = CreaSlider(pannello, 150f, 80f);
        slider.Rilasciato += SaltaA;
    }

    private static TextMeshProUGUI CreaTesto(Transform parent, string nome, float ancoraX, float x, string testo)
    {
        GameObject go = new GameObject(nome, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(ancoraX, 0.5f);
        rt.anchorMax = new Vector2(ancoraX, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.sizeDelta = new Vector2(120f, 80f);

        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.text = testo;
        label.fontSize = 40f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = GameManager.TestoChiaro;
        label.raycastTarget = false;
        GameManager.ApplicaStileTesto(label);
        return label;
    }

    private static SliderTempo CreaSlider(Transform parent, float marginiLati, float altezzaArea)
    {
        Sprite arrotondato = GameManager.SpriteArrotondato;

        // Il GameObject dello slider ha un'immagine trasparente alta quanto tutta
        // l'area: si prende col raggio anche mirando un po' sopra o sotto la linea.
        GameObject go = new GameObject("Slider", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(-2f * marginiLati, altezzaArea);
        Image area = go.GetComponent<Image>();
        area.color = new Color(1f, 1f, 1f, 0f);
        area.raycastTarget = true;
        // Senza questo una grafica del tutto trasparente non viene disegnata e il
        // raycaster di Meta la salta: lo slider non riceverebbe mai il click.
        area.canvasRenderer.cullTransparentMesh = false;

        // Linea di fondo: tutto il brano.
        Image fondo = CreaImmagine("Fondo", go.transform, GameManager.SfondoScuro(0.45f), arrotondato);
        RectTransform frt = fondo.rectTransform;
        frt.anchorMin = new Vector2(0f, 0.5f);
        frt.anchorMax = new Vector2(1f, 0.5f);
        frt.sizeDelta = new Vector2(0f, 18f);

        // Parte gia' suonata, in Dusty Rose.
        GameObject areaRiempimento = new GameObject("Fill Area", typeof(RectTransform));
        areaRiempimento.transform.SetParent(go.transform, false);
        RectTransform art = (RectTransform)areaRiempimento.transform;
        art.anchorMin = new Vector2(0f, 0.5f);
        art.anchorMax = new Vector2(1f, 0.5f);
        art.sizeDelta = new Vector2(0f, 18f);
        Image riempimento = CreaImmagine("Fill", areaRiempimento.transform, GameManager.DustyRose, arrotondato);
        riempimento.rectTransform.sizeDelta = Vector2.zero;

        // Maniglia tonda Tan, come i bottoni.
        GameObject areaManiglia = new GameObject("Handle Slide Area", typeof(RectTransform));
        areaManiglia.transform.SetParent(go.transform, false);
        RectTransform hrt = (RectTransform)areaManiglia.transform;
        hrt.anchorMin = Vector2.zero;
        hrt.anchorMax = Vector2.one;
        hrt.offsetMin = Vector2.zero;
        hrt.offsetMax = Vector2.zero;
        Image maniglia = CreaImmagine("Handle", areaManiglia.transform, GameManager.TanColor, SpriteCerchio());
        maniglia.type = Image.Type.Simple;
        maniglia.rectTransform.sizeDelta = new Vector2(54f, 0f);
        maniglia.rectTransform.anchorMin = new Vector2(0f, 0.5f - 27f / altezzaArea);
        maniglia.rectTransform.anchorMax = new Vector2(0f, 0.5f + 27f / altezzaArea);
        GameManager.ApplicaPenombra(maniglia.gameObject);

        SliderTempo slider = go.AddComponent<SliderTempo>();
        slider.fillRect = riempimento.rectTransform;
        slider.handleRect = maniglia.rectTransform;
        slider.targetGraphic = maniglia;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        ColorBlock colori = slider.colors;
        colori.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
        colori.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
        slider.colors = colori;
        slider.SetValueWithoutNotify(0f);
        return slider;
    }

    private static Image CreaImmagine(string nome, Transform parent, Color colore, Sprite sprite)
    {
        GameObject go = new GameObject(nome, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        Image img = go.GetComponent<Image>();
        img.color = colore;
        img.raycastTarget = false;
        if (sprite != null)
        {
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
        }
        return img;
    }

    private static Sprite spriteCerchio;

    private static Sprite SpriteCerchio()
    {
        if (spriteCerchio != null) return spriteCerchio;
        const int lato = 64;
        Texture2D tex = new Texture2D(lato, lato, TextureFormat.RGBA32, false);
        float centro = (lato - 1) * 0.5f;
        float raggio = centro - 1f;
        Color[] px = new Color[lato * lato];
        for (int y = 0; y < lato; y++)
        {
            for (int x = 0; x < lato; x++)
            {
                float dist = Mathf.Sqrt((x - centro) * (x - centro) + (y - centro) * (y - centro));
                px[y * lato + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(raggio + 0.5f - dist));
            }
        }
        tex.SetPixels(px);
        tex.Apply(false, true);
        spriteCerchio = Sprite.Create(tex, new Rect(0, 0, lato, lato), new Vector2(0.5f, 0.5f), 100f);
        return spriteCerchio;
    }

    private static string FormattaTempo(float secondi)
    {
        int totale = Mathf.Max(0, Mathf.FloorToInt(secondi));
        return $"{totale / 60}:{totale % 60:00}";
    }
}
