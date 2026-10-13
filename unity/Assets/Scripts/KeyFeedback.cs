using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

// Feedback visivo per i tasti della tastiera virtuale: quando il cursore/controller
// ci passa sopra il tasto si ingrandisce leggermente e cambia fortemente colore,
// così è più facile vedere quale tasto si sta per premere.
public class KeyFeedback : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
                           IPointerDownHandler, IPointerUpHandler
{
    private Image img;
    private TextMeshProUGUI etichetta;
    private Vector3 scalaBase = Vector3.one;
    private bool premuto = false;

    // Riferimenti alla palette: senza questi, i tasti resterebbero sul vecchio colore
    // a ogni cambio di palette.
    private static readonly Color ColoreBase = GameManager.TanColor;     // Tan
    private static readonly Color ColoreHover = GameManager.DustyRose;   // Dusty Rose
    private static readonly Color ColorePremuto = GameManager.DustyRose;

    private const float FattoreHover = 1.18f;    // ingrandimento al passaggio del raggio
    private const float FattorePremuto = 1.08f;

    void Awake()
    {
        img = GetComponent<Image>();
        etichetta = GetComponentInChildren<TextMeshProUGUI>(true);
        scalaBase = transform.localScale;
        Applica(1f, ColoreBase);
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
        if (!premuto) Applica(FattoreHover, ColoreHover);
    }

    public void OnPointerExit(PointerEventData e)
    {
        if (!premuto) Rilascia();
    }

    public void OnPointerDown(PointerEventData e)
    {
        premuto = true;
        Applica(FattorePremuto, ColorePremuto);
    }

    public void OnPointerUp(PointerEventData e)
    {
        premuto = false;
        Rilascia();
    }

    private void Rilascia()
    {
        premuto = false;
        Applica(1f, ColoreBase);
    }

    // Con Tan e Dusty Rose il nero e' l'unico colore che regge su entrambi (11.21:1
    // e 6.10:1), quindi il tasto e' sempre con lettering nero: il parametro
    // testoScuro della firma storica non serve piu' e resta ignorato.
    private void Applica(float fattoreScala, Color colore)
    {
        transform.localScale = scalaBase * fattoreScala;
        if (img != null) img.color = colore;
        if (etichetta != null) etichetta.color = Color.black;
    }
}