using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// Slider della barra del tempo di Osservatore, pensato per il raggio dei
// controller come BarraScorrimentoFluida: si muove dal primo spostamento (niente
// soglia di trascinamento) e un click in un punto qualsiasi porta li' la maniglia,
// come su YouTube. Il salto vero e proprio parte solo al rilascio, cosi' mentre
// si trascina il brano non riparte a ogni frame.
public class SliderTempo : Slider, IEndDragHandler
{
    public event System.Action<float> Rilasciato;
    public bool Trascinando { get; private set; }

    public override void OnInitializePotentialDrag(PointerEventData eventData)
    {
        eventData.useDragThreshold = false;
    }

    public override void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left || !IsActive() || !IsInteractable()) return;
        Trascinando = true;
        base.OnPointerDown(eventData);
    }

    public override void OnPointerUp(PointerEventData eventData)
    {
        base.OnPointerUp(eventData);
        Rilascia();
    }

    // Se il modulo di input chiude il trascinamento senza mandare il pointer up,
    // il salto parte comunque.
    public void OnEndDrag(PointerEventData eventData)
    {
        Rilascia();
    }

    private void Rilascia()
    {
        if (!Trascinando) return;
        Trascinando = false;
        Debug.Log($"[BARRA TEMPO] rilasciata a {value:F3}");
        Rilasciato?.Invoke(value);
    }

    protected override void OnDisable()
    {
        Trascinando = false;
        base.OnDisable();
    }
}
