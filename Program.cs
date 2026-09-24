using Microsoft.AspNetCore.StaticFiles;
using Sarabanda.Components;
using Sarabanda.Models;
using Sarabanda.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server: l'interfaccia vive sul server e arriva al browser via SignalR,
// così tutte le pagine aperte restano sincronizzate senza scrivere una riga di API.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Una sola partita per processo, condivisa da regia, schermo e telefoni.
builder.Services.AddSingleton<GameService>();

// In ascolto su tutte le interfacce di rete, così gli altri dispositivi della LAN
// possono aprire /display o /remote puntando all'IP di questo PC.
// La porta si cambia da appsettings.json ("Server:Port") o con la variabile
// d'ambiente Server__Port, senza ricompilare.
int port = builder.Configuration.GetValue("Server:Port", 5110);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Niente redirect a HTTPS: l'app gira in HTTP semplice sulla rete locale, dove un
// certificato non sarebbe verificabile dagli altri dispositivi.
app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

MapAudio(app);
MapGameApi(app);

app.Logger.LogInformation("Sarabanda è in ascolto sulla porta {Port}.", port);
app.Run();

// ================================================================
//  Brani audio
// ================================================================

// Serve i file della cartella canzoni/ per identificatore, mai per percorso: così dal
// browser non si può chiedere un file qualsiasi del disco. Le richieste parziali
// (Range) sono attive, altrimenti il browser non potrebbe partire da metà brano.
static void MapAudio(WebApplication app)
{
    var types = new FileExtensionContentTypeProvider();
    types.Mappings[".m4a"] = "audio/mp4";
    types.Mappings[".opus"] = "audio/ogg";
    types.Mappings[".oga"] = "audio/ogg";
    types.Mappings[".flac"] = "audio/flac";

    app.MapGet("/audio/{id}", (string id, GameService game) =>
    {
        if (game.FindSong(id) is not { Kind: SongKind.Audio } song || !File.Exists(song.FilePath))
            return Results.NotFound();

        if (!types.TryGetContentType(song.FilePath, out var contentType))
            contentType = "application/octet-stream";

        return Results.File(song.FilePath, contentType, enableRangeProcessing: true);
    });
}

// ================================================================
//  API di gioco
// ================================================================

// Per prenotarsi da qualcosa che non sia una pagina web: una pulsantiera, un ESP32,
// un tasto macro. Gli endpoint sono SENZA AUTENTICAZIONE, come negli altri giochi della
// serie: vanno bene su una rete locale di cui si ha il controllo, non su Internet.
static void MapGameApi(WebApplication app)
{
    var api = app.MapGroup("/api").DisableAntiforgery();

    // GET|POST /api/prenota/{squadra} — la squadra (da 1) si prenota.
    api.MapMethods("/prenota/{squadra:int}", new[] { "GET", "POST" }, (int squadra, GameService game) =>
    {
        var result = game.Buzz(squadra - 1);

        return result == GameService.BuzzResult.NoSuchTeam
            ? Results.BadRequest(new { ok = false, esito = "squadra inesistente" })
            : Results.Json(new
            {
                ok = result == GameService.BuzzResult.Accepted,
                esito = result switch
                {
                    GameService.BuzzResult.Accepted => "prenotata",
                    GameService.BuzzResult.TooLate => "troppo tardi",
                    GameService.BuzzResult.Locked => "esclusa da questa canzone",
                    _ => "prenotazioni chiuse"
                }
            });
    });

    // GET /api/stato — fotografia della partita. Il titolo non c'è finché non è svelato.
    api.MapGet("/stato", (GameService game) => Results.Json(new
    {
        stato = game.Status.ToString(),
        canzone = game.IsActive ? game.SongNumber : (int?)null,
        titolo = game.Status == GameStatus.Revealed ? game.CurrentSong?.Label : null,
        suona = game.IsPlaying,
        prenotata = game.BuzzedTeam is { } b ? game.Teams[b].Name : null,
        squadre = game.Teams.Select(t => new { numero = t.Number, nome = t.Name, punti = t.Score, esclusa = game.IsLocked(t.Index) })
    }));
}
