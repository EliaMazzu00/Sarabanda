namespace Sarabanda.Models;

/// <summary>Una squadra (o un concorrente singolo).</summary>
/// <remarks>
/// È immutabile: il servizio sostituisce la squadra con una copia aggiornata, così le
/// pagine possono scorrere l'elenco mentre un altro thread lo cambia.
/// </remarks>
public sealed record Team
{
    /// <summary>I colori delle squadre, nell'ordine: rosso, blu, verde, giallo, viola, arancio, azzurro, rosa.</summary>
    public static readonly string[] Colors =
    {
        "#ff4d6d", "#4d9bff", "#3ddc84", "#ffd23f", "#b36bff", "#ff9f43", "#2fe0e0", "#ff6bd6"
    };

    /// <summary>Nomi proposti alle squadre nuove.</summary>
    public static readonly string[] DefaultNames =
    {
        "Rossi", "Blu", "Verdi", "Gialli", "Viola", "Arancioni", "Azzurri", "Rosa"
    };

    /// <summary>Posizione della squadra, da 0.</summary>
    public required int Index { get; init; }

    /// <summary>Il nome.</summary>
    public required string Name { get; init; }

    /// <summary>I punti.</summary>
    public int Score { get; init; }

    /// <summary>Il colore della squadra.</summary>
    public string Color => Colors[Index % Colors.Length];

    /// <summary>Il numero da mostrare (da 1).</summary>
    public int Number => Index + 1;
}

/// <summary>Una canzone giocata, per lo storico della serata.</summary>
/// <param name="Title">Titolo e artista.</param>
/// <param name="Winner">Chi l'ha indovinata, o <c>null</c> se nessuno.</param>
/// <param name="WinnerColor">Il colore della squadra vincente.</param>
public readonly record struct SongRecord(string Title, string? Winner, string? WinnerColor);
