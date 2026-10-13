using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

public class ScrollbarVisuale : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
    IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
{
    public ScrollRect scroll;
    public RectTransform handle;
    public Image handleImg;
    public Image trackImg;

    // Riferimenti alla palette (vedi GameManager): evita che la scrollbar resti
    // indietro a ogni cambio di colore.
    private static readonly Color ColoreStabile = GameManager.SteelBlue;    // Steel Blue
    private static readonly Color ColoreHover = GameManager.DustyRose;       // Dusty Rose
    private static readonly Color ColoreDrag = GameManager.DustyRose;
    private static readonly Color TracciaStabile = GameManager.ConAlfa(GameManager.SteelBlue, 0.30f);
    private static readonly Color TracciaAttiva = GameManager.ConAlfa(GameManager.SteelBlue, 0.55f);


    private float targetNorm = 1f;
    private bool dragging = false;
    private bool hovering = false;
    private bool eccedeStabile = false;
    private static SongListManager slmCache;
    private float lastDragEventTime = 0f;
    private ScrollRect.MovementType savedMovementType;
    private bool righeBloccate = false;
    private readonly List<Graphic> righeGraphic = new List<Graphic>();
    private readonly List<bool> righeOriginale = new List<bool>();

    private void Update()
    {
        if (scroll == null || handle == null || handleImg == null || trackImg == null) return;

        float k = 1f - Mathf.Exp(-14f * Time.deltaTime);

        // Isteresi: senza, quando l'altezza del contenuto oscilla attorno al
        // limite (es. durante i rebuild della lista) la manopola si accende e
        // spegne ogni frame -> lampeggio visibile.
        float rapporto = 1f;
        if (scroll.content != null && scroll.viewport != null && scroll.viewport.rect.height > 0.01f)
            rapporto = scroll.content.rect.height / scroll.viewport.rect.height;
        if (eccedeStabile)
        {
            if (rapporto < 0.98f) eccedeStabile = false;
        }
        else
        {
            if (rapporto > 1.02f) eccedeStabile = true;
        }

        if (handle.gameObject.activeSelf != eccedeStabile) handle.gameObject.SetActive(eccedeStabile);
        if (!eccedeStabile) return;

        AggiornaDimensioneHandle();

        if (dragging)
        {
            scroll.verticalNormalizedPosition = Mathf.Clamp01(targetNorm);
            handle.anchoredPosition = new Vector2(handle.anchoredPosition.x, PosizioneHandleY(targetNorm));

            if (Time.time - lastDragEventTime > 1f)
            {
                dragging = false;
                hovering = false;
                BloccaRaycastRighe(false);
                scroll.vertical = true;
                scroll.movementType = savedMovementType;
            }
        }
        else
        {
            float norm = Mathf.Clamp01(scroll.verticalNormalizedPosition);
            float targetY = PosizioneHandleY(norm);
            handle.anchoredPosition = new Vector2(handle.anchoredPosition.x, Mathf.Lerp(handle.anchoredPosition.y, targetY, k));
        }

        Color hCol = dragging ? ColoreDrag : (hovering ? ColoreHover : ColoreStabile);
        Color tCol = (dragging || hovering) ? TracciaAttiva : TracciaStabile;
        handleImg.color = Color.Lerp(handleImg.color, hCol, k);
        trackImg.color = Color.Lerp(trackImg.color, tCol, k);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        dragging = true;
        hovering = true;
        lastDragEventTime = Time.time;
        savedMovementType = scroll.movementType;
        scroll.movementType = ScrollRect.MovementType.Unrestricted;
        scroll.vertical = false;
        BloccaRaycastRighe(true);
        RilasciaIndietroOra();
        AggiornaTargetDaEvento(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        lastDragEventTime = Time.time;
        AggiornaTargetDaEvento(eventData);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        dragging = false;
        hovering = false;
        scroll.vertical = true;
        scroll.movementType = savedMovementType;
        BloccaRaycastRighe(false);
        RilasciaIndietroOra();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        dragging = true;
        hovering = true;
        lastDragEventTime = Time.time;
        savedMovementType = scroll.movementType;
        scroll.movementType = ScrollRect.MovementType.Unrestricted;
        scroll.vertical = false;
        BloccaRaycastRighe(true);
        AggiornaTargetDaEvento(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        dragging = false;
        hovering = false;
        scroll.vertical = true;
        scroll.movementType = savedMovementType;
        BloccaRaycastRighe(false);
        RilasciaIndietroOra();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovering = true;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovering = false;
    }

    private void AggiornaTargetDaEvento(PointerEventData eventData)
    {
        if (scroll == null || trackImg == null) return;

        RectTransform trackRt = (RectTransform)trackImg.transform;
        float h = trackRt.rect.height;
        if (h <= 0.01f) return;

        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(trackRt, eventData.position, eventData.pressEventCamera, out local)) return;

        float t = Mathf.Clamp01((local.y + h * 0.5f) / h);
        targetNorm = t;
    }

    // Disabilita/riabilita temporaneamente il raycast sulle righe della lista
    // durante il drag della scrollbar, così l'highlight blu (enter) non si
    // accende su tutte le canzoni che passano sotto il raggio stazionario.
    private void BloccaRaycastRighe(bool bloccate)
    {
        if (scroll == null || scroll.content == null) return;

        if (bloccate)
        {
            if (righeBloccate) return;
            righeBloccate = true;
            righeGraphic.Clear();
            righeOriginale.Clear();
            foreach (Graphic grafica in scroll.content.GetComponentsInChildren<Graphic>(true))
            {
                righeGraphic.Add(grafica);
                righeOriginale.Add(grafica.raycastTarget);
                grafica.raycastTarget = false;
            }
        }
        else
        {
            if (!righeBloccate) return;
            righeBloccate = false;
            for (int i = 0; i < righeGraphic.Count; i++)
            {
                if (righeGraphic[i] != null)
                    righeGraphic[i].raycastTarget = righeOriginale[i];
            }
            righeGraphic.Clear();
            righeOriginale.Clear();
        }
    }

    // Ogni uso della scrollbar pulisce lo stato evidenziato del bottone "Indietro".
    private void RilasciaIndietroOra()
    {
        if (slmCache == null) slmCache = FindFirstObjectByType<SongListManager>();
        if (slmCache != null) slmCache.RilasciaIndietro();
    }

    private bool ContenutoEccede()
    {
        if (scroll.content == null || scroll.viewport == null) return false;
        return scroll.content.rect.height > scroll.viewport.rect.height + 0.5f;
    }

    private void AggiornaDimensioneHandle()
    {
        RectTransform trackRt = (RectTransform)trackImg.transform;
        float trackH = trackRt.rect.height;
        if (trackH <= 0.01f) return;

        float viewH = scroll.viewport.rect.height;
        float contH = scroll.content.rect.height;
        if (contH <= 0.01f) return;

        float ratio = Mathf.Clamp01(viewH / contH);
        float handleH = Mathf.Max(trackH * 0.08f, trackH * ratio);
        Vector2 sd = handle.sizeDelta;
        handle.sizeDelta = new Vector2(sd.x, handleH);
    }

    private float PosizioneHandleY(float norm)
    {
        RectTransform trackRt = (RectTransform)trackImg.transform;
        float trackH = trackRt.rect.height;
        float handleH = handle.sizeDelta.y;
        float min = handleH * 0.5f;
        float max = Mathf.Max(min, trackH - handleH * 0.5f);
        return Mathf.Lerp(min, max, Mathf.Clamp01(norm));
    }
}