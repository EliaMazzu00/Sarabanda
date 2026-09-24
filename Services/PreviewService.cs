using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sarabanda.Models;

namespace Sarabanda.Services;

/// <summary>Un'anteprima trovata e già scaricata: la si serve agli schermi dalla memoria.</summary>
/// <param name="Audio">I byte dell'audio (30 secondi circa).</param>
/// <param name="ContentType">Il tipo del file audio.</param>
/// <param name="Cover">I byte della copertina, o <c>null</c>.</param>
/// <param name="CoverType">Il tipo dell'immagine di copertina.</param>
/// <param name="Source">Da dove viene: "Deezer" o "iTunes".</param>
/// <param name="FoundTitle">Il titolo trovato, per controllo in regia.</param>
/// <param name="FoundArtist">L'artista trovato.</param>
public sealed record Preview(byte[] Audio, string ContentType, byte[]? Cover, string CoverType,
                             string Source, string FoundTitle, string FoundArtist);

/// <summary>Esito della ricerca di un'anteprima.</summary>
public enum PreviewStatus
{
    /// <summary>Trovata e scaricata.</summary>
    Found,

    /// <summary>I servizi hanno risposto, ma il brano non c'è (o non ha anteprima).</summary>
    NotFound,

    /// <summary>Nessuna risposta: manca Internet o i servizi non sono raggiungibili.</summary>
    Offline
}

/// <summary>
/// Trova le anteprime ufficiali di 30 secondi dei brani del catalogo, prima su Deezer e poi
/// su iTunes, e le scarica.
/// </summary>
/// <remarks>
/// <para>
/// Il catalogo contiene solo titolo, artista e anno: nessun audio viene distribuito con
/// il programma. L'anteprima si scarica <b>qui, sul PC della regia</b>, e gli schermi la
/// ricevono dal server come un qualsiasi file della rete locale: così la TV non ha bisogno
/// di Internet.
/// </para>
/// <para>
/// Un risultato si accetta solo se <b>artista e titolo corrispondono</b> (ignorando
/// maiuscole, accenti, punteggiatura e le parentesi tipo "Remastered"): meglio nessun
/// brano che il brano sbagliato. Fra i risultati buoni si scartano live, remix, karaoke e
/// versioni strumentali, a meno che il catalogo non li chieda esplicitamente.
/// </para>
/// </remarks>
public sealed partial class PreviewService
{
    /// <summary>Quante anteprime tenere in memoria (circa 1 MB l'una).</summary>
    private const int CacheSize = 60;

    private readonly HttpClient _http;
    private readonly ILogger<PreviewService> _logger;
    private readonly ConcurrentDictionary<string, Task<(PreviewStatus, Preview?)>> _pending = new();
    private readonly ConcurrentDictionary<string, Preview> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    /// <summary>Crea il servizio con un client HTTP dedicato.</summary>
    public PreviewService(ILogger<PreviewService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sarabanda/1.1 (gioco musicale da fare in casa)");
    }

    /// <summary>L'anteprima già scaricata di un brano, se c'è.</summary>
    public Preview? Cached(string songId) => _cache.GetValueOrDefault(songId);

    /// <summary>
    /// Trova e scarica l'anteprima di un brano. Più richieste per lo stesso brano
    /// condividono la stessa ricerca; un brano già scaricato torna subito dalla memoria.
    /// </summary>
    public Task<(PreviewStatus Status, Preview? Preview)> GetAsync(Song song)
    {
        if (_cache.TryGetValue(song.Id, out var cached))
            return Task.FromResult<(PreviewStatus, Preview?)>((PreviewStatus.Found, cached));

        return _pending.GetOrAdd(song.Id, _ => LoadAsync(song));
    }

    private async Task<(PreviewStatus, Preview?)> LoadAsync(Song song)
    {
        try
        {
            var (status, preview) = await FindAsync(song);

            if (preview is not null)
                Remember(song.Id, preview);

            return (status, preview);
        }
        finally
        {
            _pending.TryRemove(song.Id, out _);
        }
    }

    private void Remember(string id, Preview preview)
    {
        if (_cache.TryAdd(id, preview))
            _cacheOrder.Enqueue(id);

        while (_cacheOrder.Count > CacheSize && _cacheOrder.TryDequeue(out var old))
            _cache.TryRemove(old, out _);
    }

    /// <summary>
    /// Sceglie il risultato migliore: prima Deezer; se Deezer ha solo versioni non originali
    /// (live, karaoke, cover…) o niente, si guarda anche iTunes e si tiene la versione migliore.
    /// </summary>
    private async Task<(bool AnyAnswer, Candidate? Best)> ChooseAsync(Song song)
    {
        bool anyAnswer = false;
        Candidate? best = null;

        foreach (var search in new Func<Song, Task<Candidate?>>[] { SearchDeezerAsync, SearchItunesAsync })
        {
            if (best is { Penalty: < UnwantedPenalty })
                break;

            try
            {
                var candidate = await search(song);
                anyAnswer = true;

                if (candidate is not null && (best is null || candidate.Penalty < best.Penalty))
                    best = candidate;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Ricerca dell'anteprima non riuscita per {Song}", song.Label);
            }
        }

        return (anyAnswer, best);
    }

    /// <summary>Trova il brano e ne scarica anteprima e copertina.</summary>
    private async Task<(PreviewStatus, Preview?)> FindAsync(Song song)
    {
        var (anyAnswer, candidate) = await ChooseAsync(song);

        if (candidate is null)
            return (anyAnswer ? PreviewStatus.NotFound : PreviewStatus.Offline, null);

        try
        {
            var audio = await _http.GetByteArrayAsync(candidate.PreviewUrl);
            byte[]? cover = null;

            if (candidate.CoverUrl is { Length: > 0 } coverUrl)
            {
                try
                {
                    cover = await _http.GetByteArrayAsync(coverUrl);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Senza copertina si gioca lo stesso.
                }
            }

            return (PreviewStatus.Found, new Preview(audio, candidate.ContentType, cover, "image/jpeg",
                                                     candidate.Source, candidate.Title, candidate.Artist));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Download dell'anteprima non riuscito per {Song}", song.Label);
            return (PreviewStatus.Offline, null);
        }
    }

    /// <summary>
    /// Controlla che un brano abbia un'anteprima, senza scaricarla: serve alla verifica del
    /// catalogo. Dice anche se la versione trovata non è quella originale.
    /// </summary>
    public async Task<(PreviewStatus Status, string? Source, string? Title, string? Artist, bool Original)> CheckAsync(Song song)
    {
        var (anyAnswer, candidate) = await ChooseAsync(song);

        return candidate is null
            ? (anyAnswer ? PreviewStatus.NotFound : PreviewStatus.Offline, null, null, null, false)
            : (PreviewStatus.Found, candidate.Source, candidate.Title, candidate.Artist, candidate.Penalty < UnwantedPenalty);
    }

    private sealed record Candidate(string Title, string Artist, string PreviewUrl, string ContentType, string? CoverUrl, string Source, int Penalty);

    // ============================================================
    //  Deezer
    // ============================================================

    /// <summary>Deezer accetta circa 50 richieste ogni 5 secondi: se ne fa una ogni 130 ms al massimo.</summary>
    private static readonly TimeSpan DeezerSpacing = TimeSpan.FromMilliseconds(130);

    private readonly SemaphoreSlim _deezerGate = new(1, 1);
    private DateTime _deezerLast = DateTime.MinValue;

    private async Task<Candidate?> SearchDeezerAsync(Song song)
    {
        var url = "https://api.deezer.com/search?limit=25&q=" + Uri.EscapeDataString($"{MainArtist(song.Artist)} {StripBrackets(song.Title)}");
        DeezerResult? result = null;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            await _deezerGate.WaitAsync();
            try
            {
                var wait = _deezerLast + DeezerSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait);

                result = await _http.GetFromJsonAsync<DeezerResult>(url);
                _deezerLast = DateTime.UtcNow;
            }
            finally
            {
                _deezerGate.Release();
            }

            // Quota superata (codice 4): si aspetta e si riprova.
            if (result?.Error is not { Code: 4 })
                break;

            await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
        }

        if (result?.Error is { } error)
            throw new HttpRequestException($"Deezer non risponde: {error.Message}");

        var best = (result?.Data ?? new())
            .Where(t => !string.IsNullOrEmpty(t.Preview) && Matches(song, t.Title ?? "", t.Artist?.Name ?? ""))
            .OrderBy(t => Penalty(song, t.Title ?? "", t.Album?.Title ?? "", t.Artist?.Name ?? ""))
            .FirstOrDefault();

        return best is null
            ? null
            : new Candidate(best.Title!, best.Artist!.Name!, best.Preview!, "audio/mpeg", best.Album?.CoverXl ?? best.Album?.CoverBig, "Deezer",
                            Penalty(song, best.Title ?? "", best.Album?.Title ?? "", best.Artist?.Name ?? ""));
    }

    private sealed class DeezerResult
    {
        [JsonPropertyName("data")] public List<DeezerTrack>? Data { get; set; }
        [JsonPropertyName("error")] public DeezerError? Error { get; set; }
    }

    private sealed class DeezerError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class DeezerTrack
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("preview")] public string? Preview { get; set; }
        [JsonPropertyName("artist")] public DeezerArtist? Artist { get; set; }
        [JsonPropertyName("album")] public DeezerAlbum? Album { get; set; }
    }

    private sealed class DeezerArtist
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class DeezerAlbum
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("cover_big")] public string? CoverBig { get; set; }
        [JsonPropertyName("cover_xl")] public string? CoverXl { get; set; }
    }

    // ============================================================
    //  iTunes
    // ============================================================

    /// <summary>Quanto lasciare stare iTunes dopo che ha rifiutato le richieste.</summary>
    private static readonly TimeSpan ItunesPause = TimeSpan.FromMinutes(10);

    private DateTime _itunesBlockedUntil = DateTime.MinValue;

    private async Task<Candidate?> SearchItunesAsync(Song song)
    {
        var url = "https://itunes.apple.com/search?entity=song&limit=25&country=IT&term="
                  + Uri.EscapeDataString($"{MainArtist(song.Artist)} {StripBrackets(song.Title)}");
        ItunesResult? result = null;

        // Dopo un rifiuto netto iTunes si lascia stare per un po': ogni tentativo farebbe
        // solo aspettare la regia.
        if (DateTime.UtcNow < _itunesBlockedUntil)
            throw new HttpRequestException("iTunes temporaneamente non disponibile");

        // iTunes accetta una ventina di ricerche al minuto: a un "troppe richieste" (429) si
        // riprova una volta; a un rifiuto (403) si rinuncia subito.
        for (int attempt = 0; ; attempt++)
        {
            using var response = await _http.GetAsync(url);

            if ((int)response.StatusCode == 429 && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                continue;
            }

            if ((int)response.StatusCode is 429 or 403)
                _itunesBlockedUntil = DateTime.UtcNow + ItunesPause;

            response.EnsureSuccessStatusCode();
            result = await response.Content.ReadFromJsonAsync<ItunesResult>();
            break;
        }

        var best = (result?.Results ?? new())
            .Where(t => !string.IsNullOrEmpty(t.PreviewUrl) && Matches(song, t.TrackName ?? "", t.ArtistName ?? ""))
            .OrderBy(t => Penalty(song, t.TrackName ?? "", t.CollectionName ?? "", t.ArtistName ?? ""))
            .FirstOrDefault();

        return best is null
            ? null
            : new Candidate(best.TrackName!, best.ArtistName!, best.PreviewUrl!, "audio/mp4",
                            best.ArtworkUrl100?.Replace("100x100", "600x600"), "iTunes",
                            Penalty(song, best.TrackName ?? "", best.CollectionName ?? "", best.ArtistName ?? ""));
    }

    private sealed class ItunesResult
    {
        [JsonPropertyName("results")] public List<ItunesTrack>? Results { get; set; }
    }

    private sealed class ItunesTrack
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
        [JsonPropertyName("previewUrl")] public string? PreviewUrl { get; set; }
        [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
    }

    // ============================================================
    //  Confronto fra catalogo e risultati
    // ============================================================

    /// <summary>Vero se il risultato è davvero il brano cercato.</summary>
    public static bool Matches(Song song, string title, string artist) =>
        ArtistMatches(song.Artist, artist) && TitleMatches(song.Title, title);

    private static bool ArtistMatches(string wanted, string found)
    {
        // "P!nk" e "Pink" sono la stessa artista; "Ke$ha" e "Kesha" pure.
        wanted = wanted.Replace("!", "i").Replace("$", "s");
        var f = Normalize(found.Replace("!", "i").Replace("$", "s"));
        if (f.Length == 0)
            return false;

        // "Takagi & Ketra", "Carl Brave x Franco126", "Al Bano & Romina Power": basta che
        // uno degli artisti indicati compaia nel risultato (o viceversa).
        return SplitArtists(wanted).Any(a => a.Length > 0 && (f.Contains(a) || a.Contains(f)));
    }

    /// <summary>
    /// Il titolo deve essere lo stesso, a meno di maiuscole, accenti, punteggiatura, parti
    /// fra parentesi e diciture come "- Remastered 2009". Niente confronti "per inizio":
    /// "Baila" non deve diventare "Baila Morena".
    /// </summary>
    private static bool TitleMatches(string wanted, string found)
    {
        var w = CleanTitle(wanted);
        return w.Length > 0 && (w == CleanTitle(found) || Normalize(wanted) == Normalize(found));
    }

    private static string CleanTitle(string title)
    {
        var text = StripBrackets(title);

        // "Song - 2011 Remaster", "Song - From \"Film\"": conta solo la parte prima del trattino.
        int dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            text = text[..dash];

        text = RemasterSuffix().Replace(text, "");
        return Normalize(text);
    }

    [GeneratedRegex(@"\s*(\d{4}\s+)?(digital\s+)?remaster(ed)?(\s+\d{4})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterSuffix();

    /// <summary>Oltre questa penalità il risultato non è la versione originale.</summary>
    private const int UnwantedPenalty = 10;

    [GeneratedRegex(@"\b(live|ao vivo|en vivo|dal vivo|karaoke|instrumental|strumentale|acoustic|acustica|cover|tribute|re-?recorded|sped up|slowed|dub|demo|extended|12""|remix|in the style of|originally performed|string quartet|piano version|lullaby|ringtones?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnwantedVersion();

    /// <summary>
    /// Le parole che, anche nel nome dell'album, dicono che non è l'originale. "Remix" o
    /// "extended" invece no: un singolo intitolato "Brividi (MEDUZA Remix)" contiene anche
    /// la versione originale.
    /// </summary>
    [GeneratedRegex(@"\b(live|ao vivo|en vivo|dal vivo|karaoke|instrumental|strumentale|tribute|in the style of|originally performed|lullaby|ringtones?|8-bit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnwantedAlbum();

    [GeneratedRegex(@"\b(cover|covers|tribute|tributo|experience|made famous|famous by|in the style|piano|ukulele|sounds of|karaoke|orchestra|players|band|lullaby|tribù|revival|singers|hits|all-?star)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnwantedArtist();

    /// <summary>Più è alto, meno il risultato è la versione originale.</summary>
    private static int Penalty(Song song, string title, string album, string artist)
    {
        int penalty = 0;

        // Chi fa cover, tributi e basi: "The Sounds Of Led Zeppelin", "Avicii Cover Band",
        // "Elton John Experience", "Made famous by…". Il nome contiene quello vero, ma non è lui.
        foreach (Match match in UnwantedArtist().Matches(artist))
            if (!song.Artist.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                penalty += UnwantedPenalty * 2;

        // Meglio l'artista esatto di uno che lo "contiene" ("Kinks" per "The Kinks" va bene,
        // ma a parità vince il nome identico).
        var found = Normalize(artist);
        if (!SplitArtists(song.Artist).Contains(found))
            penalty += 3;

        foreach (Match match in UnwantedVersion().Matches(title))
            if (!song.Title.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                penalty += UnwantedPenalty;

        foreach (Match match in UnwantedAlbum().Matches(album))
            if (!song.Title.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                penalty += UnwantedPenalty;

        // A parità, meglio il titolo identico, senza aggiunte.
        if (Normalize(title) != Normalize(song.Title))
            penalty += 1;

        return penalty;
    }

    private static IEnumerable<string> SplitArtists(string artist)
    {
        var separators = new[] { " & ", " x ", " X ", ", ", " feat. ", " feat ", " ft. ", " e ", " and ", " with " };
        var parts = new List<string> { artist };

        foreach (var separator in separators)
            parts = parts.SelectMany(p => p.Split(separator, StringSplitOptions.RemoveEmptyEntries)).ToList();

        return parts.Select(Normalize).Append(Normalize(artist)).Distinct();
    }

    /// <summary>Il primo artista, per la ricerca: "Takagi & Ketra" → "Takagi".</summary>
    private static string MainArtist(string artist) =>
        artist.Split(new[] { " & ", " x ", ", ", " feat" }, StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>Toglie le parti fra parentesi: "Luce (tramonti a nord est)" → "Luce".</summary>
    private static string StripBrackets(string text)
    {
        var builder = new StringBuilder(text.Length);
        int depth = 0;

        foreach (var c in text)
        {
            if (c is '(' or '[')
                depth++;
            else if (c is ')' or ']')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                builder.Append(c);
        }

        var stripped = builder.ToString().Trim();

        // Un titolo fatto solo di parentesi, come "(I Can't Get No) Satisfaction" senza
        // il resto, non deve sparire del tutto.
        return stripped.Length == 0 ? text : stripped;
    }

    /// <summary>Minuscolo, senza accenti, solo lettere e cifre.</summary>
    public static string Normalize(string text)
    {
        // "30°C" e "30ºC" sono lo stesso titolo: gradi e indicatori ordinali non contano.
        var decomposed = text.Replace("&", " and ").Replace("°", "").Replace("º", "").Replace("ª", "")
                             .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
