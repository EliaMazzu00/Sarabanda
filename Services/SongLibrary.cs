using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sarabanda.Models;

namespace Sarabanda.Services;

/// <summary>
/// Trova i brani da indovinare: i file audio messi dall'utente in <c>canzoni/</c> e le
/// melodie scritte nota per nota in <c>melodie/</c>. Della partita non sa nulla.
/// </summary>
/// <remarks>
/// <para><b>Brani audio.</b> Ogni sottocartella di <c>canzoni/</c> è una playlist; i file
/// messi direttamente in <c>canzoni/</c> finiscono nella playlist "Senza cartella". Titolo
/// e artista si ricavano dal nome del file: <c>Artista - Titolo.mp3</c>. I numeri di
/// traccia in testa (<c>01 - </c>, <c>07. </c>) vengono tolti.</para>
/// <para><b>Melodie.</b> Una per riga, con i campi separati da <c>|</c>:</para>
/// <code>TITOLO | AUTORE | BPM | NOTE</code>
/// <para>Le note sono separate da spazi: <c>C4</c>, <c>F#4:0.5</c>, <c>SOL4:2</c>. Dopo i
/// due punti la durata in battiti (1 se manca); <c>R</c> è una pausa. Valgono sia i nomi
/// inglesi (C D E F G A B) sia quelli italiani (DO RE MI FA SOL LA SI), con <c>#</c> o
/// <c>b</c> per diesis e bemolle. Senza ottava si intende la quarta (quella del Do centrale).</para>
/// </remarks>
public sealed partial class SongLibrary
{
    /// <summary>Le estensioni audio che i browser sanno riprodurre.</summary>
    public static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".webm", ".flac" };

    /// <summary>Cartella in cui finiscono i brani caricati dalla regia.</summary>
    public const string UploadFolderName = "Caricate";

    private const string LooseFilesPlaylist = "Senza cartella";
    private const string ThemeNameTag = "# TEMA:";

    private readonly ILogger _logger;
    private Dictionary<string, List<Song>> _byPlaylist = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Song> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<PlaylistInfo> _playlists = new();

    /// <summary>Crea la libreria e legge subito le cartelle.</summary>
    public SongLibrary(string audioPath, string melodyPath, string catalogPath, ILogger logger)
    {
        AudioPath = audioPath;
        MelodyPath = melodyPath;
        CatalogPath = catalogPath;
        _logger = logger;
        Reload();
    }

    /// <summary>La cartella dei brani audio.</summary>
    public string AudioPath { get; }

    /// <summary>La cartella delle melodie.</summary>
    public string MelodyPath { get; }

    /// <summary>La cartella del catalogo online.</summary>
    public string CatalogPath { get; }

    /// <summary>Le playlist trovate: prima quelle audio, poi il catalogo online, poi le melodie.</summary>
    public IReadOnlyList<PlaylistInfo> Playlists => _playlists;

    /// <summary>Le righe di melodie e catalogo scartate all'ultima lettura, con il motivo.</summary>
    public IReadOnlyList<string> Rejected { get; private set; } = Array.Empty<string>();

    /// <summary>Trova un brano per identificatore.</summary>
    public Song? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>I brani delle playlist indicate, nell'ordine dell'elenco.</summary>
    public List<Song> GetSongs(IEnumerable<string> playlistIds)
    {
        var wanted = new HashSet<string>(playlistIds, StringComparer.OrdinalIgnoreCase);
        var songs = new List<Song>();

        foreach (var playlist in _playlists)
            if (wanted.Contains(playlist.Id) && _byPlaylist.TryGetValue(playlist.Id, out var list))
                songs.AddRange(list);

        return songs;
    }

    /// <summary>Rilegge le cartelle da zero.</summary>
    public void Reload()
    {
        var byPlaylist = new Dictionary<string, List<Song>>(StringComparer.OrdinalIgnoreCase);
        var playlists = new List<PlaylistInfo>();
        var rejected = new List<string>();

        try
        {
            ReadAudio(byPlaylist, playlists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cartella dei brani non leggibile: {Path}", AudioPath);
        }

        try
        {
            ReadCatalog(byPlaylist, playlists, rejected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cartella del catalogo non leggibile: {Path}", CatalogPath);
        }

        try
        {
            ReadMelodies(byPlaylist, playlists, rejected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cartella delle melodie non leggibile: {Path}", MelodyPath);
        }

        _byPlaylist = byPlaylist;
        _byId = byPlaylist.Values.SelectMany(s => s).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _playlists = playlists;
        Rejected = rejected;
    }

    // ============================================================
    //  Brani audio
    // ============================================================

    private void ReadAudio(Dictionary<string, List<Song>> byPlaylist, List<PlaylistInfo> playlists)
    {
        Directory.CreateDirectory(AudioPath);

        var files = Directory
            .EnumerateFiles(AudioPath, "*.*", SearchOption.AllDirectories)
            .Where(IsAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(AudioPath, path);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var playlistName = parts.Length > 1 ? parts[0] : LooseFilesPlaylist;
            var playlistId = "audio:" + playlistName;
            var (artist, title) = ParseFileName(Path.GetFileNameWithoutExtension(path));

            if (!byPlaylist.TryGetValue(playlistId, out var list))
                byPlaylist[playlistId] = list = new List<Song>();

            list.Add(new Song
            {
                Id = HashId(relative),
                Kind = SongKind.Audio,
                Title = title,
                Artist = artist,
                Playlist = playlistName,
                FilePath = path
            });
        }

        // Le cartelle in ordine alfabetico, "Senza cartella" per ultima.
        foreach (var id in byPlaylist.Keys
                     .OrderBy(k => k.Equals("audio:" + LooseFilesPlaylist, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            playlists.Add(new PlaylistInfo(id, id["audio:".Length..], SongKind.Audio, byPlaylist[id].Count));
        }
    }

    /// <summary>Vero se il file ha un'estensione audio riconosciuta.</summary>
    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ricava artista e titolo dal nome del file: <c>"03 - Lucio Dalla - Caruso"</c>
    /// diventa ("Lucio Dalla", "Caruso"). Senza trattino, è tutto titolo.
    /// </summary>
    public static (string Artist, string Title) ParseFileName(string name)
    {
        var clean = name.Replace('_', ' ').Trim();
        clean = TrackNumber().Replace(clean, "");

        var dash = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0)
            return ("", Tidy(clean.Length > 0 ? clean : name));

        var artist = Tidy(clean[..dash]);
        var title = Tidy(clean[(dash + 3)..]);
        return title.Length == 0 ? ("", artist) : (artist, title);
    }

    private static string Tidy(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^\d{1,3}\s*[-._)]\s*|^\d{1,3}\s+(?=\D)")]
    private static partial Regex TrackNumber();

    private static string HashId(string relativePath)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Salva un file caricato dalla regia nella cartella <c>canzoni/Caricate</c>, senza
    /// sovrascrivere niente: se il nome c'è già, aggiunge un numero.
    /// </summary>
    /// <returns>Il percorso del file salvato.</returns>
    public async Task<string> SaveUploadAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(AudioPath, UploadFolderName);
        Directory.CreateDirectory(folder);

        var safeName = string.Concat(Path.GetFileName(fileName).Split(Path.GetInvalidFileNameChars()));
        if (safeName.Length == 0 || !IsAudioFile(safeName))
            throw new InvalidOperationException($"\"{fileName}\" non è un file audio riconosciuto.");

        var target = Path.Combine(folder, safeName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);

        for (int n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{stem} ({n}){extension}");

        await using var output = File.Create(target);
        await content.CopyToAsync(output, cancellationToken);
        return target;
    }

    // ============================================================
    //  Catalogo online
    // ============================================================

    /// <summary>
    /// Legge i file del catalogo: una canzone per riga, <c>TITOLO | ARTISTA | ANNO</c>
    /// (l'anno è facoltativo). L'audio non c'è: lo cerca <see cref="PreviewService"/> al
    /// momento del gioco.
    /// </summary>
    private void ReadCatalog(Dictionary<string, List<Song>> byPlaylist, List<PlaylistInfo> playlists, List<string> rejected)
    {
        if (!Directory.Exists(CatalogPath))
            return;

        var files = Directory
            .EnumerateFiles(CatalogPath, "*.txt")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            var fileId = Path.GetFileNameWithoutExtension(path);
            var name = ReadThemeName(lines, fileId);
            var id = "online:" + fileId;
            var songs = new List<Song>();

            int number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (TryParseCatalogLine(line, name, out var song, out var error))
                    songs.Add(song);
                else
                    rejected.Add($"{Path.GetFileName(path)}, riga {number}: {error}");
            }

            byPlaylist[id] = songs;
            playlists.Add(new PlaylistInfo(id, name, SongKind.Online, songs.Count));
        }
    }

    /// <summary>Interpreta una riga del catalogo: <c>TITOLO | ARTISTA | ANNO</c>.</summary>
    public static bool TryParseCatalogLine(string line, string playlist, out Song song, out string error)
    {
        song = null!;
        var fields = line.Split('|').Select(f => Tidy(f.Trim())).ToArray();

        if (fields.Length < 2 || fields[0].Length == 0 || fields[1].Length == 0)
        {
            error = "servono almeno titolo e artista: TITOLO | ARTISTA | ANNO";
            return false;
        }

        int? year = null;
        if (fields.Length > 2 && fields[2].Length > 0)
        {
            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) || y < 1900 || y > 2100)
            {
                error = $"anno \"{fields[2]}\" non valido";
                return false;
            }
            year = y;
        }

        song = new Song
        {
            // Lo stesso brano ha lo stesso identificatore in qualsiasi playlist compaia.
            Id = "c" + HashId(fields[1] + "|" + fields[0]),
            Kind = SongKind.Online,
            Title = fields[0],
            Artist = fields[1],
            Playlist = playlist,
            Year = year
        };
        error = "";
        return true;
    }

    // ============================================================
    //  Melodie
    // ============================================================

    private void ReadMelodies(Dictionary<string, List<Song>> byPlaylist, List<PlaylistInfo> playlists, List<string> rejected)
    {
        if (!Directory.Exists(MelodyPath))
            return;

        var files = Directory
            .EnumerateFiles(MelodyPath, "*.txt")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            var fileId = Path.GetFileNameWithoutExtension(path);
            var name = ReadThemeName(lines, fileId);
            var id = "melodia:" + fileId;
            var songs = new List<Song>();

            int number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (TryParseMelody(line, name, $"{fileId}-{number}", out var song, out var error))
                    songs.Add(song);
                else
                    rejected.Add($"{Path.GetFileName(path)}, riga {number}: {error}");
            }

            byPlaylist[id] = songs;
            playlists.Add(new PlaylistInfo(id, name, SongKind.Melody, songs.Count));
        }
    }

    /// <summary>Interpreta una riga <c>TITOLO | AUTORE | BPM | NOTE</c>.</summary>
    public static bool TryParseMelody(string line, string playlist, string id, out Song song, out string error)
    {
        song = null!;
        var fields = line.Split('|').Select(f => f.Trim()).ToArray();

        if (fields.Length < 4)
        {
            error = "servono quattro campi: TITOLO | AUTORE | BPM | NOTE";
            return false;
        }

        if (fields[0].Length == 0)
        {
            error = "manca il titolo";
            return false;
        }

        if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bpm) || bpm < 20 || bpm > 400)
        {
            error = $"velocità \"{fields[2]}\" non valida (un numero fra 20 e 400)";
            return false;
        }

        var notes = new List<Note>();
        foreach (var token in fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseNote(token, out var note))
            {
                error = $"nota \"{token}\" non valida";
                return false;
            }
            notes.Add(note);
        }

        if (!notes.Any(n => n.Midi is not null))
        {
            error = "nessuna nota";
            return false;
        }

        song = new Song
        {
            Id = "m-" + id,
            Kind = SongKind.Melody,
            Title = fields[0],
            Artist = fields[1],
            Playlist = playlist,
            Notes = notes,
            Bpm = bpm
        };
        error = "";
        return true;
    }

    [GeneratedRegex(@"^(?<name>DO|RE|MI|FA|SOL|LA|SI|[A-G])(?<acc>#|♯|B|♭)?(?<oct>-?\d)?(?::(?<beats>[\d.,/]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteToken();

    [GeneratedRegex(@"^(?:R|P|-)(?::(?<beats>[\d.,/]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex RestToken();

    private static readonly Dictionary<string, int> Semitones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = 0, ["D"] = 2, ["E"] = 4, ["F"] = 5, ["G"] = 7, ["A"] = 9, ["B"] = 11,
        ["DO"] = 0, ["RE"] = 2, ["MI"] = 4, ["FA"] = 5, ["SOL"] = 7, ["LA"] = 9, ["SI"] = 11,
    };

    /// <summary>Interpreta una nota come <c>F#4:0.5</c> o <c>SOL:2</c>, o una pausa <c>R:1</c>.</summary>
    public static bool TryParseNote(string token, out Note note)
    {
        note = default;

        var rest = RestToken().Match(token);
        if (rest.Success)
        {
            if (!TryParseBeats(rest.Groups["beats"].Value, out var restBeats))
                return false;
            note = new Note(null, restBeats);
            return true;
        }

        var match = NoteToken().Match(token);
        if (!match.Success || !TryParseBeats(match.Groups["beats"].Value, out var beats))
            return false;

        int semitone = Semitones[match.Groups["name"].Value];
        var accidental = match.Groups["acc"].Value;
        if (accidental is "#" or "♯")
            semitone++;
        else if (accidental.Length > 0)
            semitone--;

        int octave = match.Groups["oct"].Success ? int.Parse(match.Groups["oct"].Value, CultureInfo.InvariantCulture) : 4;
        int midi = 12 * (octave + 1) + semitone;

        if (midi < 21 || midi > 108)
            return false;

        note = new Note(midi, beats);
        return true;
    }

    /// <summary>Durata in battiti: vuota = 1; accetta <c>0.5</c>, <c>0,5</c> e <c>1/3</c>.</summary>
    private static bool TryParseBeats(string text, out double beats)
    {
        beats = 1;
        if (text.Length == 0)
            return true;

        if (text.Contains('/'))
        {
            var parts = text.Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                && den > 0)
            {
                beats = num / den;
                return beats > 0 && beats <= 16;
            }
            return false;
        }

        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out beats))
            return false;

        return beats > 0 && beats <= 16;
    }

    private static string ReadThemeName(IEnumerable<string> lines, string fallbackId)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith(ThemeNameTag, StringComparison.OrdinalIgnoreCase))
            {
                var name = line[ThemeNameTag.Length..].Trim();
                if (name.Length > 0)
                    return name;
            }
        }

        var cleaned = fallbackId.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-', '_', ' ')
                                .Replace('-', ' ').Replace('_', ' ');
        return cleaned.Length == 0 ? fallbackId : char.ToUpper(cleaned[0]) + cleaned[1..];
    }
}
