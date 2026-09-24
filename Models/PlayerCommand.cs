namespace Sarabanda.Models;

/// <summary>
/// Il comando che il lettore di una pagina deve eseguire. Viene passato così com'è al
/// JavaScript (i nomi delle proprietà diventano camelCase).
/// </summary>
/// <remarks>
/// Il server non sa a che secondo del file si trova il browser: sa solo <b>quanto si è
/// ascoltato</b> (<see cref="Listened"/>). Il punto di partenza lo calcola ogni lettore da
/// <see cref="StartMode"/>, <see cref="StartSeconds"/> e <see cref="StartFraction"/>, che
/// sono uguali per tutti: così due schermi aperti suonano lo stesso punto del brano.
/// </remarks>
public sealed record PlayerCommand
{
    /// <summary>Numero progressivo: un lettore esegue un comando una volta sola.</summary>
    public int Seq { get; init; }

    /// <summary>"play", "pause" o "stop".</summary>
    public string Action { get; init; } = "stop";

    /// <summary>"audio" o "melody".</summary>
    public string Kind { get; init; } = "audio";

    /// <summary>Identificatore del brano, per capire se è cambiato.</summary>
    public string SongId { get; init; } = "";

    /// <summary>Indirizzo del file audio.</summary>
    public string Url { get; init; } = "";

    /// <summary>"beginning", "fixed" o "random".</summary>
    public string StartMode { get; init; } = "beginning";

    /// <summary>Secondo di partenza per <c>fixed</c>.</summary>
    public double StartSeconds { get; init; }

    /// <summary>Frazione del brano da cui partire per <c>random</c>, da 0 a 1.</summary>
    public double StartFraction { get; init; }

    /// <summary>Secondi già ascoltati da quando la canzone è partita.</summary>
    public double Listened { get; init; }

    /// <summary>Quanto si può ascoltare al massimo (0 = senza limite), per non partire troppo vicino alla fine.</summary>
    public double MaxListen { get; init; }

    /// <summary>Le note della melodia: coppie [midi o -1 per la pausa, battiti].</summary>
    public double[][] Notes { get; init; } = Array.Empty<double[]>();

    /// <summary>Velocità della melodia.</summary>
    public int Bpm { get; init; } = 100;
}
