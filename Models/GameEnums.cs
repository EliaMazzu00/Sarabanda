namespace Sarabanda.Models;

/// <summary>Fase in cui si trova la canzone in gioco.</summary>
public enum GameStatus
{
    /// <summary>Nessuna canzone: si prepara la partita.</summary>
    Idle,

    /// <summary>Canzone pronta, non ancora partita: "pronti?"</summary>
    Ready,

    /// <summary>
    /// Si ascolta: le squadre possono prenotarsi. La musica può essere in pausa (la
    /// regia l'ha fermata, o l'ascolto è finito) ma le prenotazioni restano aperte.
    /// </summary>
    Listening,

    /// <summary>Una squadra si è prenotata: musica ferma, la squadra risponde.</summary>
    Buzzed,

    /// <summary>Titolo svelato: indovinata o no.</summary>
    Revealed
}

/// <summary>Che tipo di brano è.</summary>
public enum SongKind
{
    /// <summary>Un file audio vero (mp3, m4a, ogg, wav...) messo dall'utente in <c>canzoni/</c>.</summary>
    Audio,

    /// <summary>Una melodia scritta nota per nota, suonata dal pianoforte sintetizzato.</summary>
    Melody
}

/// <summary>Dove esce la musica.</summary>
public enum AudioOutput
{
    /// <summary>Dallo schermo (la TV con le casse): il caso normale.</summary>
    Display,

    /// <summary>Dal PC della regia.</summary>
    Admin,

    /// <summary>Da entrambi.</summary>
    Both
}

/// <summary>Da che punto parte un brano audio.</summary>
public enum StartMode
{
    /// <summary>Dall'inizio.</summary>
    Beginning,

    /// <summary>Da un secondo preciso, uguale per tutti i brani.</summary>
    Fixed,

    /// <summary>Da un punto a caso, lontano dalla fine: più difficile.</summary>
    Random
}

/// <summary>Cosa deve fare il lettore di una pagina.</summary>
public enum PlayerAction
{
    /// <summary>Fermo e senza brano.</summary>
    Stop,

    /// <summary>Suona dal punto indicato.</summary>
    Play,

    /// <summary>In pausa.</summary>
    Pause
}
