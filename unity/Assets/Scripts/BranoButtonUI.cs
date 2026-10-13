using UnityEngine;
using UnityEngine.UI;
using TMPro;

[RequireComponent(typeof(Button))]
public class BranoButtonUI : MonoBehaviour
{
    [Header("Dati del Brano")]
    public string titolo;
    public string autore;
    public string difficolta;
    public string nomeFileMidi;

    [Header("Riferimenti TextMeshPro del Bottone")]
    public TextMeshProUGUI testoTitoloEAutore;
    public TextMeshProUGUI testoDifficolta;

    void Start()
    {
        if (testoTitoloEAutore != null)
        {
            // Autore in Inchiostro: e' il tono scuro della palette e regge sul Tan.
            testoTitoloEAutore.text = $"{titolo}\n<size=70%><color=#{GameManager.Hex(GameManager.Inchiostro)}>{autore}</color></size>";
        }

        if (testoDifficolta != null)
        {
            testoDifficolta.text = difficolta;
            if (difficolta.ToLower() == "facile") testoDifficolta.color = Color.green;
            else if (difficolta.ToLower() == "intermedio") testoDifficolta.color = Color.yellow;
            else if (difficolta.ToLower() == "difficile") testoDifficolta.color = Color.red;
        }

        Button bottone = GetComponent<Button>();
        bottone.onClick.AddListener(AlClickDelBottone);
    }

    private void AlClickDelBottone()
    {
        PianoVisualizer visualizer = FindFirstObjectByType<PianoVisualizer>();
        if (visualizer != null)
        {
            visualizer.ResetVisualizer();
            visualizer.PulisciNoteAtteseVisive();
        }

        UdpReceiver receiver = FindFirstObjectByType<UdpReceiver>();
        GameManager gm = FindFirstObjectByType<GameManager>();

        if (receiver != null && gm != null)
        {
            bool isSeguimi = (gm.statoAttuale == GameManager.AppState.Seguimi);
            receiver.InviaComandoRiproduzione(nomeFileMidi, isSeguimi);
        }
        else if (receiver != null)
        {
            receiver.InviaComandoRiproduzione(nomeFileMidi, false);
        }

        // Se eravamo in modalità ricerca, usciamo e torniamo alla lista completa.
        SongListManager slm = FindFirstObjectByType<SongListManager>();
        if (slm != null) slm.CanzoneAvviata();
    }
}