using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Spartito della modalita' Seguimi: il bridge lo fa generare a MuseScore dal file
// MIDI del brano scelto ("get_score" -> "score_ready") e lo salva come pagine PNG
// piu' score.json, che dice da che secondo del brano parte ogni pagina. Qui si
// mostra solo la pagina che si sta suonando, sotto il bottone e larga quanto lui, e la si
// cambia seguendo il brano: la posizione della dimostrazione ("song_progress") e
// quella dell'accordo atteso ("expect_notes" con "time").
//
// Il bottone "Visualizza spartito" / "Chiudi spartito" sta accanto al "+", alla
// sua stessa altezza e alla stessa distanza che c'e' tra "Torna al Menu" e il "+".
// Anche lo spartito vive sul Canvas radice e non nel gruppo Seguimi (scalato x4 e
// animato).
public class SpartitoSeguimi : MonoBehaviour
{
    // Altezza del bottone rispetto al diametro del "+" e spazio dal "+".
    private const float FRAZIONE_ALTEZZA_PLUS = 0.6f;
    // Spazio dal "+" se "Torna al Menu" non si trova.
    private const float SPAZIO_DAL_PLUS = 40f;
    // Spazio tra il bottone e lo spartito, rispetto all'altezza del bottone.
    private const float FRAZIONE_SPAZIO_SOTTO = 0.15f;
    // Misure di progetto del bottone: viene scalato come la barra del tempo.
    private const float LARGHEZZA_BOTTONE = 520f;
    private const float ALTEZZA_BOTTONE = 130f;
    // Proporzioni di una pagina A4 (larghezza / altezza) finche' non c'e' l'immagine.
    private const float PROPORZIONE_PAGINA = 0.707f;
    // Una pagina "comincia" un attimo prima del suo primo accordo.
    private const float ANTICIPO_PAGINA = 0.05f;

    private const string TESTO_APRI = "Visualizza spartito";
    private const string TESTO_CHIUDI = "Chiudi spartito";
    private const string TESTO_IN_PREPARAZIONE = "Preparazione dello spartito con MuseScore...";
    private const string TESTO_NON_DISPONIBILE = "Spartito non disponibile.\nServe MuseScore 4 installato sul PC.";

    private RectTransform canvasRt;
    private System.Func<RectTransform> plus;
    private System.Func<RectTransform> indietro;

    private RectTransform bottone;
    private TextMeshProUGUI etichettaBottone;
    private RectTransform pannello;
    private RawImage pagina;
    private TextMeshProUGUI messaggio;

    // Brano attivo: il bottone c'e' solo mentre si suona un brano.
    private bool abilitato = false;
    private bool aperto = false;
    // Ogni richiesta ha un numero: le risposte di brani precedenti si scartano.
    private int richiestaCorrente = 0;
    private bool inPreparazione = false;
    private string cartella;
    private int numeroPagine = 0;
    private float[] iniziPagine = new float[0];
    private int paginaMostrata = -1;
    private int paginaVoluta = 0;
    private Texture2D texturePagina;

    [System.Serializable]
    private class InfoSpartito
    {
        public int pages;
        public float[] page_starts;
    }

    public void Inizializza(Canvas canvas, System.Func<RectTransform> bottonePlus, System.Func<RectTransform> bottoneIndietro)
    {
        if (canvas == null) return;
        canvasRt = (RectTransform)canvas.rootCanvas.transform;
        plus = bottonePlus;
        indietro = bottoneIndietro;
        CreaBottone();
        CreaPannello();
        bottone.gameObject.SetActive(false);
        pannello.gameObject.SetActive(false);
    }

    // Chiamata quando si sceglie un brano in Seguimi: restituisce il numero da
    // mandare al bridge con "get_score".
    public int NuovaRichiesta()
    {
        Svuota();
        richiestaCorrente++;
        inPreparazione = true;
        abilitato = true;
        if (bottone != null)
        {
            bottone.gameObject.SetActive(true);
            bottone.SetAsLastSibling();
            Posiziona();
            RinfrescaRaggio();
        }
        AggiornaVista();
        return richiestaCorrente;
    }

    // Risposta del bridge: pagine 0 = MuseScore non c'e' o non e' riuscito.
    public void SpartitoPronto(int richiesta, string cartellaSpartito, int pagine)
    {
        if (richiesta != richiestaCorrente || !abilitato) return;
        inPreparazione = false;
        cartella = cartellaSpartito;
        numeroPagine = pagine;
        iniziPagine = new float[0];
        if (pagine > 0 && !string.IsNullOrEmpty(cartella))
        {
            string info = Path.Combine(cartella, "score.json");
            try
            {
                InfoSpartito dati = JsonUtility.FromJson<InfoSpartito>(File.ReadAllText(info));
                if (dati != null && dati.page_starts != null) iniziPagine = dati.page_starts;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SPARTITO] score.json non leggibile: {e.Message}");
            }
        }
        Debug.Log($"[SPARTITO] pronto: {pagine} pagine in {cartella}");
        paginaMostrata = -1;
        AggiornaVista();
    }

    // Secondo del brano a cui si e' arrivati: sceglie la pagina da mostrare.
    public void AggiornaTempo(float secondi)
    {
        if (!abilitato || numeroPagine <= 0) return;
        int nuova = 0;
        for (int i = 0; i < iniziPagine.Length && i < numeroPagine; i++)
        {
            if (iniziPagine[i] <= secondi + ANTICIPO_PAGINA) nuova = i;
        }
        paginaVoluta = nuova;
        if (aperto) MostraPagina(paginaVoluta);
    }

    // Brano fermato (cambio modalita', "+", Torna al Menu): sparisce tutto.
    public void Disabilita()
    {
        abilitato = false;
        aperto = false;
        Svuota();
        if (bottone != null) bottone.gameObject.SetActive(false);
        if (pannello != null) pannello.gameObject.SetActive(false);
    }

    private void Svuota()
    {
        inPreparazione = false;
        cartella = null;
        numeroPagine = 0;
        iniziPagine = new float[0];
        paginaVoluta = 0;
        paginaMostrata = -1;
        if (pagina != null) pagina.texture = null;
        if (texturePagina != null)
        {
            Destroy(texturePagina);
            texturePagina = null;
        }
    }

    private void Alterna()
    {
        if (!abilitato) return;
        aperto = !aperto;
        AggiornaVista();
    }

    private void AggiornaVista()
    {
        if (etichettaBottone != null) etichettaBottone.text = aperto ? TESTO_CHIUDI : TESTO_APRI;
        if (pannello == null) return;

        pannello.gameObject.SetActive(abilitato && aperto);
        if (!abilitato || !aperto) return;
        pannello.SetAsLastSibling();

        if (numeroPagine > 0)
        {
            messaggio.gameObject.SetActive(false);
            MostraPagina(paginaVoluta);
        }
        else
        {
            pagina.texture = null;
            pagina.color = new Color(1f, 1f, 1f, 0f);
            messaggio.text = inPreparazione ? TESTO_IN_PREPARAZIONE : TESTO_NON_DISPONIBILE;
            messaggio.gameObject.SetActive(true);
            Posiziona();
        }
    }

    // Carica una pagina alla volta: un brano lungo puo' avere decine di pagine e
    // tenerle tutte in memoria come texture non serve.
    private void MostraPagina(int indice)
    {
        if (indice == paginaMostrata && texturePagina != null) return;
        string percorso = Path.Combine(cartella ?? "", $"pagina_{indice + 1}.png");
        if (!File.Exists(percorso)) return;

        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        if (!tex.LoadImage(File.ReadAllBytes(percorso)))
        {
            Destroy(tex);
            return;
        }
        tex.filterMode = FilterMode.Trilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        if (texturePagina != null) Destroy(texturePagina);
        texturePagina = tex;
        paginaMostrata = indice;
        pagina.texture = tex;
        pagina.color = Color.white;
        Posiziona();
    }

    void LateUpdate()
    {
        // "+" e menu si muovono con l'animazione del menu: ricalcolo leggero.
        if (bottone != null && bottone.gameObject.activeSelf) Posiziona();
    }

    // ---------------------------------------------------------------
    // POSIZIONE
    // ---------------------------------------------------------------
    private void Posiziona()
    {
        if (canvasRt == null || bottone == null) return;

        RectTransform rtPlus = plus != null ? plus() : null;
        if (rtPlus == null) return;
        Rect r = RectSulCanvas(rtPlus);
        float altezza = r.height * FRAZIONE_ALTEZZA_PLUS;
        if (altezza < 1f) return;

        // Stesso spazio che c'e' tra "Torna al Menu" e il "+".
        float spazio = SPAZIO_DAL_PLUS;
        RectTransform rtIndietro = indietro != null ? indietro() : null;
        if (rtIndietro != null)
        {
            float scarto = r.xMin - RectSulCanvas(rtIndietro).xMax;
            if (scarto > 0f) spazio = scarto;
        }

        float scala = altezza / ALTEZZA_BOTTONE;
        float larghezza = LARGHEZZA_BOTTONE * scala;
        Vector2 centro = new Vector2(r.xMax + spazio + larghezza * 0.5f, r.center.y);
        bottone.localScale = new Vector3(scala, scala, 1f);
        bottone.localRotation = Quaternion.identity;
        bottone.localPosition = new Vector3(centro.x, centro.y, 0f);

        if (pannello == null || !pannello.gameObject.activeSelf) return;

        // Sotto il bottone e largo quanto lui.
        float proporzione = texturePagina != null && texturePagina.height > 0
            ? (float)texturePagina.width / texturePagina.height
            : PROPORZIONE_PAGINA;
        float altezzaPagina = larghezza / proporzione;
        float cima = centro.y - altezza * 0.5f - altezza * FRAZIONE_SPAZIO_SOTTO;
        pannello.sizeDelta = new Vector2(larghezza, altezzaPagina);
        pannello.localScale = Vector3.one;
        pannello.localRotation = Quaternion.identity;
        pannello.localPosition = new Vector3(centro.x, cima - altezzaPagina * 0.5f, 0f);
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

    private static void RinfrescaRaggio()
    {
        RaggioPuntatore raggio = FindFirstObjectByType<RaggioPuntatore>();
        if (raggio != null) raggio.RinfrescaOra();
    }

    // ---------------------------------------------------------------
    // GRAFICA
    // ---------------------------------------------------------------
    private void CreaBottone()
    {
        GameObject go = new GameObject("BottoneSpartito", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(canvasRt, false);
        bottone = (RectTransform)go.transform;
        bottone.anchorMin = new Vector2(0.5f, 0.5f);
        bottone.anchorMax = new Vector2(0.5f, 0.5f);
        bottone.pivot = new Vector2(0.5f, 0.5f);
        bottone.sizeDelta = new Vector2(LARGHEZZA_BOTTONE, ALTEZZA_BOTTONE);

        // Tan con hover e pressione Dusty Rose, come i bottoni dei brani.
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
        btn.onClick.AddListener(Alterna);

        GameObject testoGO = new GameObject("Testo", typeof(RectTransform));
        testoGO.transform.SetParent(go.transform, false);
        RectTransform trt = (RectTransform)testoGO.transform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(20f, 8f);
        trt.offsetMax = new Vector2(-20f, -8f);
        etichettaBottone = testoGO.AddComponent<TextMeshProUGUI>();
        etichettaBottone.text = TESTO_APRI;
        etichettaBottone.fontSize = 48f;
        etichettaBottone.alignment = TextAlignmentOptions.Center;
        etichettaBottone.color = Color.black;
        etichettaBottone.enableWordWrapping = false;
        etichettaBottone.raycastTarget = false;
        GameManager.ApplicaStileTesto(etichettaBottone);
    }

    private void CreaPannello()
    {
        // Foglio bianco: le pagine di MuseScore hanno lo sfondo trasparente.
        GameObject p = new GameObject("SpartitoSeguimi", typeof(RectTransform), typeof(Image));
        p.transform.SetParent(canvasRt, false);
        pannello = (RectTransform)p.transform;
        pannello.anchorMin = new Vector2(0.5f, 0.5f);
        pannello.anchorMax = new Vector2(0.5f, 0.5f);
        pannello.pivot = new Vector2(0.5f, 0.5f);
        pannello.sizeDelta = new Vector2(500f * PROPORZIONE_PAGINA, 500f);
        Image foglio = p.GetComponent<Image>();
        foglio.color = Color.white;
        foglio.raycastTarget = false;
        GameManager.ApplicaPenombra(p);

        GameObject paginaGO = new GameObject("Pagina", typeof(RectTransform), typeof(RawImage));
        paginaGO.transform.SetParent(p.transform, false);
        RectTransform prt = (RectTransform)paginaGO.transform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;
        pagina = paginaGO.GetComponent<RawImage>();
        pagina.raycastTarget = false;
        pagina.color = new Color(1f, 1f, 1f, 0f);

        GameObject testoGO = new GameObject("Messaggio", typeof(RectTransform));
        testoGO.transform.SetParent(p.transform, false);
        RectTransform trt = (RectTransform)testoGO.transform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(20f, 20f);
        trt.offsetMax = new Vector2(-20f, -20f);
        messaggio = testoGO.AddComponent<TextMeshProUGUI>();
        messaggio.enableAutoSizing = true;
        messaggio.fontSizeMin = 8f;
        messaggio.fontSizeMax = 36f;
        messaggio.alignment = TextAlignmentOptions.Center;
        messaggio.color = GameManager.Inchiostro;
        messaggio.raycastTarget = false;
        GameManager.ApplicaStileTesto(messaggio);
    }
}
