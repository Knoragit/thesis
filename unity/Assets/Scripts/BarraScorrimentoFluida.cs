using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Scrollbar del viewer del report pensata per il raggio dei controller VR.
// Quella standard, premuta fuori dalla maniglia, avanza a scatti di una
// "pagina" alla volta e aspetta la soglia di trascinamento prima di seguire il
// puntatore: col raggio che trema risultava poco fluida. Qui la maniglia segue
// subito il punto premuto (o resta agganciata dove la si e' presa) e il
// movimento e' leggermente ammorbidito per assorbire il tremolio del raggio.
public class BarraScorrimentoFluida : Scrollbar
{
    // Quanto in fretta la barra raggiunge il punto indicato: piu' alto = piu'
    // reattiva, piu' basso = piu' morbida.
    private const float Reattivita = 25f;

    private float valoreObiettivo;
    private bool inseguendo = false;
    private Vector2 presaSullaManiglia;

    public override void OnInitializePotentialDrag(PointerEventData eventData)
    {
        // Nessuna soglia: la barra si muove dal primo spostamento del raggio.
        eventData.useDragThreshold = false;
    }

    public override void OnPointerDown(PointerEventData eventData)
    {
        // Niente base.OnPointerDown: e' lui che fa gli scatti a "pagine".
        if (eventData.button != PointerEventData.InputButton.Left || !IsActive() || !IsInteractable()) return;

        presaSullaManiglia = Vector2.zero;
        if (handleRect != null && RectTransformUtility.RectangleContainsScreenPoint(handleRect, eventData.position, eventData.pressEventCamera))
        {
            // Presa sulla maniglia: resta agganciata nel punto preso, senza saltare.
            RectTransformUtility.ScreenPointToLocalPointInRectangle(handleRect, eventData.position, eventData.pressEventCamera, out Vector2 locale);
            presaSullaManiglia = locale - handleRect.rect.center;
        }
        Insegui(eventData);
    }

    public override void OnBeginDrag(PointerEventData eventData) { }

    public override void OnDrag(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left || !IsActive() || !IsInteractable()) return;
        Insegui(eventData);
    }

    // Converte il punto del raggio in un valore 0..1 della barra.
    private void Insegui(PointerEventData eventData)
    {
        if (handleRect == null) return;
        RectTransform area = handleRect.parent as RectTransform;
        if (area == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(area, eventData.position, eventData.pressEventCamera, out Vector2 locale)) return;

        int asse = (direction == Direction.BottomToTop || direction == Direction.TopToBottom) ? 1 : 0;
        Rect r = area.rect;
        float corsa = r.size[asse] * (1f - size);
        if (corsa <= 0f) return;

        float inizio = r.min[asse] + r.size[asse] * size * 0.5f;
        float v = Mathf.Clamp01((locale[asse] - presaSullaManiglia[asse] - inizio) / corsa);
        if (direction == Direction.RightToLeft || direction == Direction.TopToBottom) v = 1f - v;

        valoreObiettivo = v;
        inseguendo = true;
    }

    private void Update()
    {
        if (!inseguendo) return;
        float nuovo = Mathf.Lerp(value, valoreObiettivo, 1f - Mathf.Exp(-Reattivita * Time.unscaledDeltaTime));
        if (Mathf.Abs(nuovo - valoreObiettivo) < 0.0005f)
        {
            nuovo = valoreObiettivo;
            inseguendo = false;
        }
        value = nuovo;
    }

    protected override void OnDisable()
    {
        inseguendo = false;
        base.OnDisable();
    }
}
