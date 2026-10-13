using UnityEngine;
using UnityEngine.EventSystems;

// Posto sul Viewport: azzera i bottoni all'inizio e alla fine di ogni drag del
// contenuto, cosi' i bottoni accesi durante lo scroll si spengono da soli.
public class ScrollDragReset : MonoBehaviour, IBeginDragHandler, IEndDragHandler
{
    private static SongListManager slmCache;

    public void OnBeginDrag(PointerEventData eventData)
    {
        Rilascia();
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        Rilascia();
    }

    private void Rilascia()
    {
        if (slmCache == null) slmCache = FindFirstObjectByType<SongListManager>();
        if (slmCache != null) slmCache.RilasciaIndietro();
    }
}