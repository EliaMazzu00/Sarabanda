using System.Collections.Concurrent;
using System.Diagnostics;
using Sarabanda.Models;
using Timer = System.Timers.Timer;

namespace Sarabanda.Services;

/// <summary>
/// Lo stato della partita, condiviso da tutte le pagine aperte.
/// </summary>
/// <remarks>
/// <para>
/// È registrato come <b>singleton</b>: una sola partita per processo. La regia
/// (<c>/admin</c>) comanda; lo schermo (<c>/display</c>) mostra e, di solito, suona; i
/// telefoni delle squadre (<c>/remote</c>) e l'endpoint <c>/api/prenota</c> prenotano.
/// Chi vuole restare aggiornato si iscrive a <see cref="OnChange"/>.
/// </para>
/// <para>
/// <b>Il giro di una canzone.</b> <see cref="StartNext"/> la prepara
/// (<see cref="GameStatus.Ready"/>); <see cref="Play"/> la fa partire
/// (<see cref="GameStatus.Listening"/>) e apre le prenotazioni. La prima squadra che si
/// prenota (<see cref="Buzz"/>) ferma la musica (<see cref="GameStatus.Buzzed"/>). La regia
/// giudica: <see cref="JudgeCorrect"/> dà i punti e svela il titolo; <see cref="JudgeWrong"/>
/// esclude la squadra da questa canzone e riapre le prenotazioni alle altre.
/// <see cref="Reveal"/> svela il titolo senza vincitori.
/// </para>
/// <para>
/// <b>La musica suona nei browser, non qui.</b> Il servizio decide cosa devono fare i
/// lettori e lo scrive in <see cref="Command"/>, con un numero progressivo; ogni pagina
/// che suona esegue il comando quando il numero cambia. Il servizio tiene solo il conto
/// di <b>quanto si è ascoltato</b>, con un cronometro monotono, per fermare la musica allo
/// scadere del tempo di ascolto.
/// </para>
/// <para>
/// <b>Concorrenza.</b> Ogni scrittura passa da <c>_lock</c>; <see cref="Notify"/> si chiama
/// fuori dal lock. Le raccolte esposte sono array sostituiti in blocco.
/// </para>
/// </remarks>
public sealed class GameService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Numero massimo di squadre.</summary>
    public const int MaxTeams = 8;

    private readonly object _lock = new();
    private readonly Random _random = new();
    private readonly SongLibrary _library;
    private readonly PreviewService _previews;
    private readonly ILogger<GameService> _logger;
    private readonly Timer _ticker;

    /// <summary>Misura l'ascolto in corso.</summary>
    private readonly Stopwatch _playClock = new();

    /// <summary>Misura il tempo per rispondere dopo la prenotazione.</summary>
    private readonly Stopwatch _answerClock = new();

    private double _listenedAtPause;
    private int _lastNotifiedTick = -1;

    /// <summary>Crea il servizio e seleziona tutte le playlist trovate.</summary>
    public GameService(IWebHostEnvironment environment, PreviewService previews, ILogger<GameService> logger)
    {
        _previews = previews;
        _logger = logger;
        _library = new SongLibrary(
            Path.Combine(environment.ContentRootPath, "canzoni"),
            Path.Combine(environment.ContentRootPath, "melodie"),
            Path.Combine(environment.ContentRootPath, "catalogo"),
            logger);

        _ticker = new Timer(TickInterval.TotalMilliseconds) { AutoReset = true };
        _ticker.Elapsed += (_, _) => OnTick();
        _ticker.Start();

        _teams = Enumerable.Range(0, 2).Select(NewTeam).ToArray();
        _locked = new bool[_teams.Length];

        // All'avvio: le canzoni vere (catalogo e brani propri). Le melodie restano a un clic;
        // se non c'è nient'altro, si parte da quelle.
        var songs = _library.Playlists.Where(p => p.Kind != SongKind.Melody).ToList();
        UsePlaylists((songs.Count > 0 ? songs : _library.Playlists).Select(p => p.Id));
    }

    /// <summary>Scatta a ogni cambiamento di stato: le pagine si ridisegnano.</summary>
    public event Action? OnChange;

    // ============================================================
    //  Configurazione
    // ============================================================

    /// <summary>Punti a chi indovina.</summary>
    public int PointsCorrect { get; private set; } = 1;

    /// <summary>Punti tolti a chi sbaglia (0 = nessuna penalità).</summary>
    public int PenaltyWrong { get; private set; }

    /// <summary>Secondi per rispondere dopo la prenotazione (0 = senza limite).</summary>
    public int AnswerSeconds { get; private set; } = 5;

    /// <summary>Secondi di ascolto al massimo per ogni canzone (0 = tutta).</summary>
    public int MaxListenSeconds { get; private set; } = 30;

    /// <summary>Da che punto partono i brani audio.</summary>
    public StartMode StartFrom { get; private set; } = StartMode.Beginning;

    /// <summary>Il secondo di partenza, per <see cref="StartMode.Fixed"/>.</summary>
    public int StartSeconds { get; private set; } = 30;

    /// <summary>Se la musica riparte da sola dopo una risposta sbagliata.</summary>
    public bool ResumeAfterWrong { get; private set; } = true;

    /// <summary>Se la canzone riparte, senza limiti, quando si svela il titolo.</summary>
    public bool PlayOnReveal { get; private set; } = true;

    /// <summary>Quante note far sentire delle melodie (0 = tutte): il gioco delle "sette note".</summary>
    public int MelodyNotes { get; private set; }

    /// <summary>Se mandare le canzoni in ordine casuale.</summary>
    public bool Shuffle { get; private set; } = true;

    /// <summary>Se lo schermo mostra la playlist da cui viene la canzone.</summary>
    public bool ShowPlaylist { get; private set; } = true;

    /// <summary>Se lo schermo suona gli effetti (prenotazione, giusto, sbagliato).</summary>
    public bool Sounds { get; private set; } = true;

    /// <summary>Dove esce la musica.</summary>
    public AudioOutput Output { get; private set; } = AudioOutput.Display;

    /// <summary>Applica la configurazione. Vale subito, anche a canzone in corso.</summary>
    public void Configure(int pointsCorrect, int penaltyWrong, int answerSeconds, int maxListenSeconds,
                          StartMode startFrom, int startSeconds, bool resumeAfterWrong, bool playOnReveal,
                          int melodyNotes, bool shuffle, bool showPlaylist, bool sounds, AudioOutput output)
    {
        lock (_lock)
        {
            PointsCorrect = Math.Clamp(pointsCorrect, 0, 1000);
            PenaltyWrong = Math.Clamp(penaltyWrong, 0, 1000);
            AnswerSeconds = Math.Clamp(answerSeconds, 0, 120);
            MaxListenSeconds = Math.Clamp(maxListenSeconds, 0, 900);
            StartFrom = startFrom;
            StartSeconds = Math.Clamp(startSeconds, 0, 3600);
            ResumeAfterWrong = resumeAfterWrong;
            PlayOnReveal = playOnReveal;
            MelodyNotes = Math.Clamp(melodyNotes, 0, 200);
            ShowPlaylist = showPlaylist;
            Sounds = sounds;

            if (Shuffle != shuffle)
            {
                Shuffle = shuffle;
                BuildOrderLocked();
            }

            if (Output != output)
            {
                Output = output;
                // Le pagine che non devono più suonare si fermano, le altre riprendono.
                IssueCommandLocked();
            }
        }

        Notify();
    }

    /// <summary>Vero se la pagina di quel tipo deve suonare la musica.</summary>
    public bool PlaysOn(AudioOutput page) => Output == AudioOutput.Both || Output == page;

    // ============================================================
    //  Squadre
    // ============================================================

    private Team[] _teams;
    private bool[] _locked;

    /// <summary>Le squadre, nell'ordine.</summary>
    public IReadOnlyList<Team> Teams => _teams;

    private static Team NewTeam(int index) => new() { Index = index, Name = Team.DefaultNames[index % Team.DefaultNames.Length] };

    /// <summary>Cambia il numero di squadre, da 1 a <see cref="MaxTeams"/>. Punti e nomi di chi resta non cambiano.</summary>
    public void SetTeamCount(int count)
    {
        count = Math.Clamp(count, 1, MaxTeams);

        lock (_lock)
        {
            if (count == _teams.Length)
                return;

            _teams = Enumerable.Range(0, count).Select(i => i < _teams.Length ? _teams[i] : NewTeam(i)).ToArray();

            var locked = new bool[count];
            Array.Copy(_locked, locked, Math.Min(_locked.Length, count));
            _locked = locked;

            // La squadra prenotata non esiste più: si torna ad ascoltare.
            if (BuzzedTeam is { } buzzed && buzzed >= count)
            {
                BuzzedTeam = null;
                _answerClock.Reset();
                Status = GameStatus.Listening;
            }
        }

        Notify();
    }

    /// <summary>Rinomina una squadra.</summary>
    public void SetTeamName(int index, string? name)
    {
        var clean = (name ?? "").Trim();

        lock (_lock)
        {
            if (index < 0 || index >= _teams.Length)
                return;

            clean = clean.Length == 0 ? Team.DefaultNames[index % Team.DefaultNames.Length] : clean[..Math.Min(clean.Length, 24)];
            _teams = Replace(_teams, index, _teams[index] with { Name = clean });
        }

        Notify();
    }

    /// <summary>Aggiunge (o toglie) punti a una squadra. Il punteggio non scende sotto zero.</summary>
    public void AddScore(int index, int delta)
    {
        lock (_lock)
            AddScoreLocked(index, delta);

        Notify();
    }

    private void AddScoreLocked(int index, int delta)
    {
        if (index < 0 || index >= _teams.Length)
            return;

        var team = _teams[index];
        _teams = Replace(_teams, index, team with { Score = Math.Max(0, team.Score + delta) });
    }

    /// <summary>Azzera i punti di tutti.</summary>
    public void ResetScores()
    {
        lock (_lock)
            _teams = _teams.Select(t => t with { Score = 0 }).ToArray();

        Notify();
    }

    /// <summary>Vero se la squadra è esclusa da questa canzone (ha già sbagliato).</summary>
    public bool IsLocked(int index)
    {
        var locked = _locked;
        return index >= 0 && index < locked.Length && locked[index];
    }

    /// <summary>Vero se almeno una squadra può ancora prenotarsi.</summary>
    public bool AnyoneCanBuzz => _locked.Any(l => !l);

    // ============================================================
    //  Canzoni caricate
    // ============================================================

    private Song[] _loaded = Array.Empty<Song>();
    private int[] _order = Array.Empty<int>();
    private bool[] _played = Array.Empty<bool>();
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le playlist disponibili.</summary>
    public IReadOnlyList<PlaylistInfo> Playlists => _library.Playlists;

    /// <summary>Le playlist spuntate in regia.</summary>
    public IReadOnlyCollection<string> SelectedPlaylists => _selected;

    /// <summary>La cartella in cui mettere i brani audio.</summary>
    public string AudioFolder => _library.AudioPath;

    /// <summary>Le righe di melodia scartate, con il motivo.</summary>
    public IReadOnlyList<string> RejectedMelodies => _library.Rejected;

    /// <summary>Le canzoni caricate.</summary>
    public IReadOnlyList<Song> Loaded => _loaded;

    /// <summary>Quante canzoni ci sono in gioco.</summary>
    public int SongCount => _loaded.Length;

    /// <summary>Quante canzoni sono già state giocate.</summary>
    public int PlayedCount => _played.Count(p => p);

    /// <summary>Quante restano.</summary>
    public int RemainingCount => _loaded.Length - PlayedCount;

    /// <summary>Vero se la canzone in quella posizione è già stata giocata.</summary>
    public bool WasPlayed(int index)
    {
        var played = _played;
        return index >= 0 && index < played.Length && played[index];
    }

    /// <summary>Trova un brano audio per identificatore (per l'endpoint che lo serve).</summary>
    public Song? FindSong(string id) => _library.Find(id);

    /// <summary>Usa le canzoni delle playlist indicate.</summary>
    public void UsePlaylists(IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var songs = _library.GetSongs(wanted);

        lock (_lock)
        {
            _selected = wanted;
            _loaded = songs.ToArray();
            _played = new bool[_loaded.Length];
            BuildOrderLocked();

            // La canzone in gioco resta dov'è: la si ritrova nel nuovo elenco se c'è.
            CurrentIndex = CurrentSong is null ? -1 : Array.FindIndex(_loaded, s => s.Id == CurrentSong.Id);
            if (CurrentIndex >= 0)
                _played[CurrentIndex] = true;
        }

        Notify();
    }

    /// <summary>
    /// Rilegge le cartelle (dopo aver aggiunto file a mano o caricato brani) e tiene
    /// spuntate le stesse playlist, più quelle nuove se prima erano spuntate tutte.
    /// </summary>
    public void ReloadLibrary()
    {
        bool hadAll = _library.Playlists.All(p => _selected.Contains(p.Id));
        _library.Reload();

        var ids = hadAll
            ? _library.Playlists.Select(p => p.Id)
            : _selected.Where(id => _library.Playlists.Any(p => p.Id == id));

        UsePlaylists(ids.ToList());
    }

    /// <summary>Salva un brano caricato dalla regia e rilegge le cartelle.</summary>
    public async Task<string> UploadAsync(string fileName, Stream content)
    {
        var saved = await _library.SaveUploadAsync(fileName, content);

        // La playlist dei caricati si spunta da sola: chi carica un brano vuole giocarlo.
        lock (_lock)
            _selected = new HashSet<string>(_selected, StringComparer.OrdinalIgnoreCase) { "audio:" + SongLibrary.UploadFolderName };

        ReloadLibrary();
        return saved;
    }

    private void BuildOrderLocked()
    {
        var order = Enumerable.Range(0, _loaded.Length).ToArray();

        if (Shuffle)
        {
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
        }

        _order = order;
    }

    /// <summary>Segna tutte le canzoni come non giocate e rimescola.</summary>
    public void ResetPlayed()
    {
        lock (_lock)
        {
            _played = new bool[_loaded.Length];
            if (CurrentIndex >= 0 && CurrentIndex < _played.Length && Status != GameStatus.Idle)
                _played[CurrentIndex] = true;
            BuildOrderLocked();
        }

        Notify();
    }

    // ============================================================
    //  Stato della canzone in gioco
    // ============================================================

    /// <summary>Fase corrente.</summary>
    public GameStatus Status { get; private set; } = GameStatus.Idle;

    /// <summary>La canzone in gioco. Il titolo lo vede solo la regia fino alla rivelazione.</summary>
    public Song? CurrentSong { get; private set; }

    /// <summary>Posizione della canzone in gioco fra quelle caricate, o -1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>Quante canzoni sono state mandate in questa serata (per il "canzone n°").</summary>
    public int SongNumber { get; private set; }

    /// <summary>La squadra prenotata, o <c>null</c>.</summary>
    public int? BuzzedTeam { get; private set; }

    /// <summary>Chi ha indovinato la canzone svelata, o <c>null</c>.</summary>
    public int? Winner { get; private set; }

    /// <summary>Quante note della melodia si sentono in questa canzone (0 = tutte).</summary>
    public int NoteLimit { get; private set; }

    /// <summary>Vero se la musica sta suonando.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Vero se il tempo di ascolto è finito (o il brano è arrivato in fondo).</summary>
    public bool ListenOver { get; private set; }

    /// <summary>Vero se il brano si sta ascoltando senza limite (dopo la rivelazione).</summary>
    private bool _unlimited;

    /// <summary>Punto di partenza casuale del brano, uguale per tutti i lettori.</summary>
    private double _startFraction;

    /// <summary>L'ultimo problema segnalato da un lettore, da mostrare in regia.</summary>
    public string? PlayerError { get; private set; }

    private SongRecord[] _history = Array.Empty<SongRecord>();

    /// <summary>Le canzoni giocate, dalla più recente.</summary>
    public IReadOnlyList<SongRecord> History => _history;

    /// <summary>Vero se c'è una canzone in gioco.</summary>
    public bool IsActive => Status != GameStatus.Idle;

    /// <summary>Secondi ascoltati di questa canzone.</summary>
    public double Listened => _listenedAtPause + (_playClock.IsRunning ? _playClock.Elapsed.TotalSeconds : 0);

    /// <summary>
    /// Il limite di ascolto attuale in secondi: il tempo configurato, oppure la durata della
    /// melodia se è più corta. <c>null</c> se non c'è limite noto (brano audio intero).
    /// </summary>
    public double? ListenLimit
    {
        get
        {
            if (_unlimited)
                return CurrentSong is { Kind: SongKind.Melody } m ? m.MelodySeconds(NoteLimit) : null;

            double? limit = MaxListenSeconds > 0 ? MaxListenSeconds : null;

            if (CurrentSong is { Kind: SongKind.Melody } melody)
            {
                var length = melody.MelodySeconds(NoteLimit);
                limit = limit is { } l ? Math.Min(l, length) : length;
            }

            return limit;
        }
    }

    /// <summary>Frazione di ascolto consumata, da 0 a 1, o <c>null</c> se non c'è limite.</summary>
    public double? ListenFraction => ListenLimit is { } limit && limit > 0 ? Math.Clamp(Listened / limit, 0, 1) : null;

    /// <summary>Secondi rimasti per rispondere, o <c>null</c> se non c'è limite o nessuno è prenotato.</summary>
    public int? AnswerRemaining =>
        Status == GameStatus.Buzzed && AnswerSeconds > 0
            ? Math.Max(0, (int)Math.Ceiling(AnswerSeconds - _answerClock.Elapsed.TotalSeconds))
            : null;

    /// <summary>Frazione del tempo di risposta rimasta, per l'anello sullo schermo.</summary>
    public double AnswerFraction =>
        AnswerSeconds <= 0 ? 1 : Math.Clamp(1 - _answerClock.Elapsed.TotalSeconds / AnswerSeconds, 0, 1);

    // ---- Effetti sonori ----

    /// <summary>L'ultimo evento sonoro; lo schermo lo suona quando cambia <see cref="CueId"/>.</summary>
    public SoundCue Cue { get; private set; }

    /// <summary>La squadra a cui si riferisce l'evento sonoro (per il timbro del campanello).</summary>
    public int CueTeam { get; private set; }

    /// <summary>Contatore degli eventi sonori.</summary>
    public int CueId { get; private set; }

    private void CueLocked(SoundCue cue, int team = 0)
    {
        Cue = cue;
        CueTeam = team;
        CueId++;
    }

    // ============================================================
    //  Comando per i lettori
    // ============================================================

    /// <summary>Il comando che i lettori devono eseguire.</summary>
    public PlayerCommand Command { get; private set; } = new();

    /// <summary>Scrive il comando che corrisponde allo stato attuale e ne aumenta il numero.</summary>
    private void IssueCommandLocked()
    {
        var song = CurrentSong;
        var action = song is null ? "stop" : IsPlaying ? "play" : "pause";

        Command = new PlayerCommand
        {
            Seq = Command.Seq + 1,
            Action = action,
            Kind = song?.Kind == SongKind.Melody ? "melody" : "audio",
            SongId = song?.Id ?? "",
            // Un brano del catalogo si può suonare solo quando l'anteprima è arrivata.
            Url = song is { Kind: SongKind.Online } && PreviewState != PreviewState.Ready ? "" : song?.Url ?? "",
            // L'anteprima è già un estratto di 30 secondi: si parte sempre dal suo inizio.
            StartMode = song?.Kind == SongKind.Online ? "beginning" : StartFrom switch
            {
                StartMode.Fixed => "fixed",
                StartMode.Random => "random",
                _ => "beginning"
            },
            StartSeconds = StartSeconds,
            StartFraction = _startFraction,
            Listened = Listened,
            MaxListen = MaxListenSeconds,
            Notes = song?.Kind == SongKind.Melody
                ? (NoteLimit > 0 ? song.Notes.Take(NoteLimit) : song.Notes)
                    .Select(n => new[] { n.Midi ?? -1, n.Beats }).ToArray()
                : Array.Empty<double[]>(),
            Bpm = song?.Bpm ?? 100
        };
    }

    private void StartPlayingLocked()
    {
        if (IsPlaying || CurrentSong is null)
            return;

        // Anteprima non ancora arrivata: si parte appena è pronta.
        if (CurrentSong.Kind == SongKind.Online && PreviewState != PreviewState.Ready)
        {
            _playWhenReady = PreviewState == PreviewState.Loading;
            return;
        }

        IsPlaying = true;
        _playClock.Restart();
        IssueCommandLocked();
    }

    private void StopPlayingLocked()
    {
        _playWhenReady = false;

        if (!IsPlaying)
            return;

        _listenedAtPause = Listened;
        _playClock.Reset();
        IsPlaying = false;
        IssueCommandLocked();
    }

    private void RewindLocked()
    {
        _listenedAtPause = 0;
        _playClock.Reset();
        IsPlaying = false;
        ListenOver = false;
    }

    // ============================================================
    //  Conduzione
    // ============================================================

    /// <summary>Prepara la prossima canzone non ancora giocata (ricomincia il giro se sono finite).</summary>
    public void StartNext()
    {
        lock (_lock)
        {
            if (_loaded.Length == 0)
                return;

            if (_played.All(p => p))
            {
                _played = new bool[_loaded.Length];
                BuildOrderLocked();
            }

            int start = CurrentIndex >= 0 ? Array.IndexOf(_order, CurrentIndex) + 1 : 0;

            for (int step = 0; step < _order.Length; step++)
            {
                int index = _order[(start + step) % _order.Length];
                if (!_played[index])
                {
                    PrepareLocked(index);
                    break;
                }
            }
        }

        Notify();
    }

    /// <summary>Prepara una canzone precisa, scelta dall'elenco in regia.</summary>
    public void StartAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _loaded.Length)
                return;

            PrepareLocked(index);
        }

        Notify();
    }

    private void PrepareLocked(int index)
    {
        CurrentSong = _loaded[index];
        CurrentIndex = index;
        _played[index] = true;
        SongNumber++;

        BuzzedTeam = null;
        Winner = null;
        _locked = new bool[_teams.Length];
        _answerClock.Reset();
        _unlimited = false;
        _startFraction = _random.NextDouble();
        NoteLimit = CurrentSong.Kind == SongKind.Melody ? MelodyNotes : 0;
        PlayerError = null;
        _playWhenReady = false;
        PreviewState = CurrentSong.Kind == SongKind.Online ? PreviewState.Loading : PreviewState.None;
        CurrentPreview = null;

        RewindLocked();
        Status = GameStatus.Ready;
        IssueCommandLocked();
        CueLocked(SoundCue.NewSong);

        if (CurrentSong.Kind == SongKind.Online)
        {
            _ = LoadPreviewAsync(CurrentSong);
            PrefetchNextLocked(index);
        }
    }

    // ============================================================
    //  Anteprime del catalogo online
    // ============================================================

    private bool _playWhenReady;

    /// <summary>Vero se la regia ha premuto "suona" e si aspetta solo che arrivi l'anteprima.</summary>
    public bool WaitingForPreview => _playWhenReady;

    /// <summary>A che punto è l'anteprima del brano in gioco (solo catalogo online).</summary>
    public PreviewState PreviewState { get; private set; }

    /// <summary>L'anteprima del brano in gioco, quando è arrivata.</summary>
    public Preview? CurrentPreview { get; private set; }

    /// <summary>Indirizzo della copertina da mostrare quando si svela il titolo, se c'è.</summary>
    public string? CoverUrl => CurrentPreview?.Cover is not null && CurrentSong is { } song ? $"cover/{song.Id}" : null;

    /// <summary>Cerca l'anteprima e, se nel frattempo è stato premuto "suona", fa partire la musica.</summary>
    private async Task LoadPreviewAsync(Song song)
    {
        var (status, preview) = await _previews.GetAsync(song);

        lock (_lock)
        {
            if (CurrentSong?.Id != song.Id || PreviewState != PreviewState.Loading)
                return;

            if (preview is not null)
            {
                CurrentPreview = preview;
                PreviewState = PreviewState.Ready;

                if (_playWhenReady && Status is GameStatus.Ready or GameStatus.Listening or GameStatus.Revealed)
                {
                    _playWhenReady = false;
                    if (Status == GameStatus.Ready)
                        Status = GameStatus.Listening;
                    StartPlayingLocked();
                }
                else
                {
                    // Gli schermi intanto scaricano il brano, così parte senza attese.
                    IssueCommandLocked();
                }
            }
            else
            {
                PreviewState = PreviewState.Failed;
                _playWhenReady = false;
                PlayerError = status == PreviewStatus.Offline
                    ? "Non c'è connessione a Internet: i brani del catalogo 🌐 hanno bisogno di Internet sul PC della regia. Usa le melodie o le tue canzoni."
                    : $"Anteprima non trovata per \"{song.Label}\" né su Deezer né su iTunes.";
            }
        }

        Notify();
    }

    /// <summary>Scarica in anticipo l'anteprima della prossima canzone, per non far aspettare.</summary>
    private void PrefetchNextLocked(int currentIndex)
    {
        int start = Array.IndexOf(_order, currentIndex) + 1;

        for (int step = 0; step < _order.Length; step++)
        {
            int index = _order[(start + step) % _order.Length];
            if (!_played[index])
            {
                if (_loaded[index] is { Kind: SongKind.Online } next)
                    _ = _previews.GetAsync(next);
                return;
            }
        }
    }

    /// <summary>
    /// Fa suonare la musica: la prima volta apre le prenotazioni, poi riprende da dove si
    /// era fermata. Se l'ascolto era finito, riparte da capo.
    /// </summary>
    public void Play()
    {
        lock (_lock)
        {
            switch (Status)
            {
                case GameStatus.Ready when CurrentSong?.Kind == SongKind.Online && PreviewState != PreviewState.Ready:
                    // L'anteprima non c'è ancora: si resta su "Pronti?" (niente prenotazioni
                    // prima di aver sentito qualcosa) e si parte appena arriva.
                    _playWhenReady = PreviewState == PreviewState.Loading;
                    break;

                case GameStatus.Ready:
                    Status = GameStatus.Listening;
                    StartPlayingLocked();
                    break;

                case GameStatus.Listening:
                case GameStatus.Revealed:
                    if (ListenOver)
                        RewindLocked();
                    StartPlayingLocked();
                    break;
            }
        }

        Notify();
    }

    /// <summary>Mette in pausa la musica. Le prenotazioni restano aperte.</summary>
    public void Pause()
    {
        lock (_lock)
            StopPlayingLocked();

        Notify();
    }

    /// <summary>Fa ripartire la canzone da capo.</summary>
    public void Replay()
    {
        lock (_lock)
        {
            if (Status is not (GameStatus.Listening or GameStatus.Revealed))
                return;

            RewindLocked();
            StartPlayingLocked();
        }

        Notify();
    }

    /// <summary>Melodie: fa sentire qualche nota in più e riparte da capo.</summary>
    public void MoreNotes(int extra = 2)
    {
        lock (_lock)
        {
            if (CurrentSong is not { Kind: SongKind.Melody } song || NoteLimit <= 0 || Status == GameStatus.Buzzed)
                return;

            NoteLimit = Math.Min(song.Notes.Count, NoteLimit + extra);
            if (NoteLimit >= song.Notes.Count)
                NoteLimit = 0;

            if (Status == GameStatus.Ready)
                Status = GameStatus.Listening;

            RewindLocked();
            StartPlayingLocked();
        }

        Notify();
    }

    /// <summary>Esito di una prenotazione.</summary>
    public enum BuzzResult
    {
        /// <summary>Prenotazione accettata: la squadra risponde.</summary>
        Accepted,

        /// <summary>Qualcun altro è stato più veloce.</summary>
        TooLate,

        /// <summary>La squadra ha già sbagliato questa canzone.</summary>
        Locked,

        /// <summary>Non c'è niente da prenotare adesso.</summary>
        Closed,

        /// <summary>La squadra non esiste.</summary>
        NoSuchTeam
    }

    /// <summary>Una squadra si prenota. Vince la prima che arriva.</summary>
    public BuzzResult Buzz(int team)
    {
        BuzzResult result;

        lock (_lock)
        {
            if (team < 0 || team >= _teams.Length)
                result = BuzzResult.NoSuchTeam;
            else if (Status == GameStatus.Buzzed)
                result = BuzzResult.TooLate;
            else if (Status != GameStatus.Listening)
                result = BuzzResult.Closed;
            else if (_locked[team])
                result = BuzzResult.Locked;
            else
            {
                StopPlayingLocked();
                BuzzedTeam = team;
                Status = GameStatus.Buzzed;
                _answerClock.Restart();
                _answerTimeUpCued = false;
                CueLocked(SoundCue.Buzz, team);
                result = BuzzResult.Accepted;
            }
        }

        if (result == BuzzResult.Accepted)
            Notify();

        return result;
    }

    /// <summary>La squadra prenotata ha indovinato: punti, titolo svelato.</summary>
    public void JudgeCorrect()
    {
        lock (_lock)
        {
            if (Status != GameStatus.Buzzed || BuzzedTeam is not { } team)
                return;

            AddScoreLocked(team, PointsCorrect);
            Winner = team;
            RevealLocked(SoundCue.Correct);
        }

        Notify();
    }

    /// <summary>
    /// La squadra prenotata ha sbagliato: eventuale penalità, esclusa da questa canzone,
    /// prenotazioni di nuovo aperte alle altre.
    /// </summary>
    public void JudgeWrong()
    {
        lock (_lock)
        {
            if (Status != GameStatus.Buzzed || BuzzedTeam is not { } team)
                return;

            AddScoreLocked(team, -PenaltyWrong);
            var locked = (bool[])_locked.Clone();
            locked[team] = true;
            _locked = locked;
            BuzzedTeam = null;
            _answerClock.Reset();
            Status = GameStatus.Listening;
            CueLocked(SoundCue.Wrong, team);

            if (ResumeAfterWrong && !ListenOver && AnyoneCanBuzz)
                StartPlayingLocked();
        }

        Notify();
    }

    /// <summary>Annulla una prenotazione partita per sbaglio: nessuna penalità, nessuna esclusione.</summary>
    public void CancelBuzz()
    {
        lock (_lock)
        {
            if (Status != GameStatus.Buzzed)
                return;

            BuzzedTeam = null;
            _answerClock.Reset();
            Status = GameStatus.Listening;
        }

        Notify();
    }

    /// <summary>Svela il titolo senza vincitori.</summary>
    public void Reveal()
    {
        lock (_lock)
        {
            if (Status is not (GameStatus.Ready or GameStatus.Listening or GameStatus.Buzzed))
                return;

            Winner = null;
            RevealLocked(SoundCue.Reveal);
        }

        Notify();
    }

    private void RevealLocked(SoundCue cue)
    {
        var song = CurrentSong!;

        BuzzedTeam = null;
        _answerClock.Reset();
        Status = GameStatus.Revealed;
        CueLocked(cue, Winner ?? 0);

        var winner = Winner is { } w ? _teams[w] : null;
        _history = _history.Prepend(new SongRecord(song.Label, winner?.Name, winner?.Color)).ToArray();

        StopPlayingLocked();

        if (PlayOnReveal)
        {
            // Si riascolta in libertà: il limite di ascolto non vale più.
            _unlimited = true;
            if (ListenOver)
                RewindLocked();
            ListenOver = false;
            StartPlayingLocked();
        }
    }

    /// <summary>Toglie la canzone e torna alla preparazione. I punti restano.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            RewindLocked();
            Status = GameStatus.Idle;
            CurrentSong = null;
            BuzzedTeam = null;
            Winner = null;
            _answerClock.Reset();
            _locked = new bool[_teams.Length];
            IssueCommandLocked();
        }

        Notify();
    }

    /// <summary>Svuota lo storico e rimette il contatore delle canzoni a zero.</summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _history = Array.Empty<SongRecord>();
            SongNumber = CurrentSong is null ? 0 : 1;
        }

        Notify();
    }

    // ============================================================
    //  Notizie dai lettori
    // ============================================================

    /// <summary>Un lettore è arrivato in fondo al brano.</summary>
    public void ReportEnded(int seq)
    {
        lock (_lock)
        {
            if (seq != Command.Seq || !IsPlaying)
                return;

            StopPlayingLocked();
            ListenOver = true;
        }

        Notify();
    }

    /// <summary>Un lettore non riesce a suonare il brano.</summary>
    public void ReportError(int seq, string message)
    {
        lock (_lock)
        {
            if (seq != Command.Seq)
                return;

            PlayerError = message;

            // Se il brano non suona, il tempo di ascolto non deve scorrere a vuoto.
            StopPlayingLocked();
        }

        Notify();
    }

    private readonly ConcurrentDictionary<Guid, (string Name, bool AudioReady)> _outputs = new();

    /// <summary>Le pagine che possono suonare, con il loro stato audio.</summary>
    public IReadOnlyCollection<(string Name, bool AudioReady)> Outputs => _outputs.Values.ToArray();

    /// <summary>Una pagina con il lettore si presenta (o aggiorna il suo stato audio).</summary>
    public void SetOutput(Guid id, string name, bool audioReady)
    {
        _outputs[id] = (name, audioReady);
        Notify();
    }

    /// <summary>Una pagina con il lettore si chiude.</summary>
    public void RemoveOutput(Guid id)
    {
        if (_outputs.TryRemove(id, out _))
            Notify();
    }

    // ============================================================
    //  Orologio
    // ============================================================

    private bool _answerTimeUpCued;

    private void OnTick()
    {
        bool changed = false;

        lock (_lock)
        {
            if (IsPlaying && ListenLimit is { } limit && Listened >= limit)
            {
                StopPlayingLocked();
                ListenOver = true;
                changed = true;
            }

            if (Status == GameStatus.Buzzed && AnswerSeconds > 0 && !_answerTimeUpCued
                && _answerClock.Elapsed.TotalSeconds >= AnswerSeconds)
            {
                _answerTimeUpCued = true;
                CueLocked(SoundCue.AnswerTimeUp, BuzzedTeam ?? 0);
                changed = true;
            }

            // Le pagine si aggiornano mezzo secondo alla volta mentre qualcosa scorre.
            if (IsPlaying || Status == GameStatus.Buzzed)
            {
                int tick = (int)((IsPlaying ? Listened : _answerClock.Elapsed.TotalSeconds) * 2);
                if (tick != _lastNotifiedTick)
                {
                    _lastNotifiedTick = tick;
                    changed = true;
                }
            }
        }

        if (changed)
            Notify();
    }

    private static T[] Replace<T>(T[] source, int index, T value)
    {
        var copy = (T[])source.Clone();
        copy[index] = value;
        return copy;
    }

    private void Notify() => OnChange?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        _ticker.Stop();
        _ticker.Dispose();
    }
}

/// <summary>Gli effetti sonori dello schermo.</summary>
public enum SoundCue
{
    /// <summary>Nessuno.</summary>
    None,

    /// <summary>Una nuova canzone è pronta.</summary>
    NewSong,

    /// <summary>Una squadra si è prenotata.</summary>
    Buzz,

    /// <summary>Risposta giusta.</summary>
    Correct,

    /// <summary>Risposta sbagliata.</summary>
    Wrong,

    /// <summary>Titolo svelato senza vincitori.</summary>
    Reveal,

    /// <summary>Tempo per rispondere finito.</summary>
    AnswerTimeUp
}
