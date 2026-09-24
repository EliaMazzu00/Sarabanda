namespace Sarabanda.Models;

/// <summary>Una nota di una melodia.</summary>
/// <param name="Midi">Numero MIDI della nota (60 = Do centrale), o <c>null</c> per una pausa.</param>
/// <param name="Beats">Durata in battiti (1 = una semiminima).</param>
public readonly record struct Note(int? Midi, double Beats);

/// <summary>Un brano da indovinare.</summary>
public sealed class Song
{
    /// <summary>Identificatore stabile: per un file audio dipende dal percorso.</summary>
    public required string Id { get; init; }

    /// <summary>File audio o melodia.</summary>
    public required SongKind Kind { get; init; }

    /// <summary>Il titolo: è la risposta da dare.</summary>
    public required string Title { get; init; }

    /// <summary>L'artista o l'autore; può essere vuoto.</summary>
    public string Artist { get; init; } = "";

    /// <summary>La playlist (sottocartella) o il tema da cui viene.</summary>
    public required string Playlist { get; init; }

    /// <summary>Percorso del file audio sul disco (solo per <see cref="SongKind.Audio"/>).</summary>
    public string FilePath { get; init; } = "";

    /// <summary>Le note (solo per <see cref="SongKind.Melody"/>).</summary>
    public IReadOnlyList<Note> Notes { get; init; } = Array.Empty<Note>();

    /// <summary>Velocità della melodia, in battiti al minuto.</summary>
    public int Bpm { get; init; } = 100;

    /// <summary>Indirizzo da cui il browser scarica il brano audio.</summary>
    public string Url => Kind == SongKind.Audio ? $"audio/{Id}" : "";

    /// <summary>Titolo e artista su una riga, per la regia.</summary>
    public string Label => Artist.Length > 0 ? $"{Title} — {Artist}" : Title;

    /// <summary>Durata in secondi delle prime <paramref name="noteLimit"/> note (0 = tutte).</summary>
    public double MelodySeconds(int noteLimit)
    {
        var notes = noteLimit > 0 ? Notes.Take(noteLimit) : Notes;
        return notes.Sum(n => n.Beats) * 60.0 / Bpm;
    }
}

/// <summary>Una playlist: una sottocartella di <c>canzoni/</c> o un file di melodie.</summary>
/// <param name="Id">Identificatore: <c>audio:Nome</c> oppure <c>melodia:file</c>.</param>
/// <param name="Name">Nome leggibile.</param>
/// <param name="Kind">Se contiene file audio o melodie.</param>
/// <param name="Count">Quanti brani contiene.</param>
public readonly record struct PlaylistInfo(string Id, string Name, SongKind Kind, int Count);
