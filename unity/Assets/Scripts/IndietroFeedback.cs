using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// Feedback visivo per i bottoni (Indietro, canzoni, ecc.) che evita l'highlight
// "a catena" durante lo scroll: disabilita la ColorTint di Unity (che lascia lo
// stato evidenziato appeso) e gestisce il colore a mano, ignorando gli enter che
// arrivano mentre un altro oggetto è già premuto (drag della scrollbar/contenuto).
[RequireComponent(typeof(Button))]
public class IndietroFeedback : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
                                IPointerDownHandler, IPointerUpHandler
{
    public TextMeshProUGUI etichetta;
    public bool cambiaColoreEtichetta = false;
    public Color coloreEtichettaBase = Color.white;
    public Color coloreEtichettaHover = Color.black;
    public Color coloreEtichettaPremuto = Color.black;

    // Ingrossamento all'hover/premuto: 0 = disattivato (default). Lo si attiva sui
    // pulsanti "singoli" (menu, Indietro, Cerca, "+", tasti tastiera); le righe
    // canzone restano a 0 per non sovrapporsi ai vicini durante lo scroll.
    public float fattoreHover = 0f;
    public float fattorePremuto = 0f;

    private Image img;
    private Button btn;
    private Color coloreBase;
    private Color coloreHover;
    private Color colorePremuto;
    private bool colorePremutoImpostato = false;
    private bool coloreHoverImpostato = false;
    private bool coloreBaseImpostato = false;
    private bool premuto = false;
    private Vector3 scalaBase = Vector3.one;

    void Awake()
    {
        scalaBase = transform.localScale;
    }

    void Start()
    {
        img = GetComponent<Image>();
        btn = GetComponent<Button>();
        if (btn != null)
        {
            if (!coloreHoverImpostato) coloreHover = btn.colors.highlightedColor;
            btn.transition = Selectable.Transition.None;
        }
        // Se ImpostaColori ha gia' deciso il colore di riposo, non lo sovrascrivere
        // col valore temporaneo dell'Image (spesso bianco durante la costruzione).
        if (img != null && !coloreBaseImpostato) coloreBase = img.color;
        if (img != null) img.color = coloreBase;
        if (!colorePremutoImpostato) colorePremuto = coloreHover;
        if (etichetta == null) etichetta = GetComponentInChildren<TextMeshProUGUI>(true);
    }

    // Aggiorna i colori dopo la cache di Start() (la palette viene applicata
    // da GameManager un frame dopo la creazione).
    public void ImpostaColori(Color baseC, Color hoverC)
    {
        coloreBase = baseC;
        coloreBaseImpostato = true;
        coloreHover = hoverC;
        coloreHoverImpostato = true;
        colorePremuto = hoverC;
        colorePremutoImpostato = true;
        if (img != null) img.color = coloreBase;
        ApplicaEtichetta(false);
    }

    // Colore di pressione indipendente (per le righe canzone: hover Navajo,
    // conferma Muted Olive).
    public void ImpostaColorePremuto(Color premutoC)
    {
        colorePremuto = premutoC;
        colorePremutoImpostato = true;
    }

    void OnEnable()
    {
        Rilascia();
    }

    void OnDisable()
    {
        Rilascia();
    }

    public void OnPointerEnter(PointerEventData e)
    {
        if (!premuto && !AltroPremuto(e)) Applica(coloreHover);
    }

    public void OnPointerExit(PointerEventData e)
    {
        if (!premuto) Rilascia();
    }

    public void OnPointerDown(PointerEventData e)
    {
        premuto = true;
        if (img != null) img.color = colorePremuto;
        if (cambiaColoreEtichetta && etichetta != null) etichetta.color = coloreEtichettaPremuto;
        if (fattorePremuto > 0f) transform.localScale = scalaBase * fattorePremuto;
    }

    public void OnPointerUp(PointerEventData e)
    {
        premuto = false;
        Rilascia();
    }

    public void Rilascia()
    {
        premuto = false;
        if (img != null) img.color = coloreBase;
        // La scala si ripristina solo se questo bottone la gestisce: il "+" e' animato
        // a scala dalle transizioni del gruppo e non va toccato qui.
        if (fattoreHover > 0f || fattorePremuto > 0f) transform.localScale = scalaBase;
        ApplicaEtichetta(false);
    }

    private void Applica(Color colore)
    {
        if (img != null) img.color = colore;
        if (fattoreHover > 0f) transform.localScale = scalaBase * fattoreHover;
        ApplicaEtichetta(true);
    }

    private void ApplicaEtichetta(bool hover)
    {
        if (!cambiaColoreEtichetta || etichetta == null) return;
        etichetta.color = hover ? coloreEtichettaHover : coloreEtichettaBase;
    }

    // Se il raggio sta premendo/trascinando un altro oggetto (es. la scrollbar o
    // il contenuto scorrevole), gli enter su questo bottone non devono illuminarlo.
    private bool AltroPremuto(PointerEventData e)
    {
        return e.pointerPress != null && e.pointerPress != gameObject;
    }
}