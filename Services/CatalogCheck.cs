using Microsoft.Extensions.Logging.Abstractions;
using Sarabanda.Models;

namespace Sarabanda.Services;

/// <summary>
/// La verifica del catalogo da riga di comando: <c>Sarabanda.exe --verifica-catalogo</c>.
/// Cerca l'anteprima di ogni brano dei file in <c>catalogo/</c> ed elenca quelli che non si
/// trovano, così chi aggiunge canzoni scopre subito un titolo scritto male.
/// </summary>
public static class CatalogCheck
{
    /// <summary>Quante ricerche in parallelo: abbastanza per fare presto, poche per non disturbare i servizi.</summary>
    private const int Parallelism = 4;

    /// <summary>Esegue la verifica e restituisce il codice d'uscita: 0 se è tutto a posto.</summary>
    /// <param name="showAll">Se elencare anche i brani trovati, con il titolo e l'artista del risultato.</param>
    public static async Task<int> RunAsync(bool showAll)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var root = FindRoot();
        var catalogPath = Path.Combine(root, "catalogo");

        if (!Directory.Exists(catalogPath))
        {
            Console.WriteLine($"Cartella del catalogo non trovata: {catalogPath}");
            return 2;
        }

        var library = new SongLibrary(Path.Combine(root, "canzoni"), Path.Combine(root, "melodie"), catalogPath, NullLogger.Instance);
        var previews = new PreviewService(NullLogger<PreviewService>.Instance);

        foreach (var line in library.Rejected.Where(r => File.Exists(Path.Combine(catalogPath, r.Split(',')[0]))))
            Console.WriteLine($"RIGA SCARTATA  {line}");

        var playlists = library.Playlists.Where(p => p.Kind == SongKind.Online).ToList();
        int total = 0, missing = 0, offline = 0, notOriginal = 0;

        Console.WriteLine($"Verifica di {playlists.Sum(p => p.Count)} brani in {playlists.Count} playlist…");
        Console.WriteLine();

        foreach (var playlist in playlists)
        {
            var songs = library.GetSongs(new[] { playlist.Id });
            var results = new (Song Song, PreviewStatus Status, string? Source, string? Title, string? Artist, bool Original)[songs.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, songs.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
                async (i, _) =>
                {
                    var (status, source, title, artist, original) = await previews.CheckAsync(songs[i]);
                    results[i] = (songs[i], status, source, title, artist, original);
                });

            int found = results.Count(r => r.Status == PreviewStatus.Found);
            Console.WriteLine($"== {playlist.Name}: {found}/{songs.Count}");

            foreach (var r in results)
            {
                total++;

                switch (r.Status)
                {
                    case PreviewStatus.Found when !r.Original:
                        notOriginal++;
                        Console.WriteLine($"   VERSIONE NON ORIGINALE  {r.Song.Label}  →  {r.Title} — {r.Artist} [{r.Source}]");
                        break;

                    case PreviewStatus.Found when showAll:
                        Console.WriteLine($"   ok  {r.Song.Label}  →  {r.Title} — {r.Artist} [{r.Source}]");
                        break;

                    case PreviewStatus.NotFound:
                        missing++;
                        Console.WriteLine($"   NON TROVATA  {r.Song.Label}");
                        break;

                    case PreviewStatus.Offline:
                        offline++;
                        Console.WriteLine($"   SENZA RISPOSTA  {r.Song.Label}");
                        break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Totale: {total} brani, {missing} non trovati, {notOriginal} solo in versione non originale, {offline} senza risposta dai servizi.");

        if (offline == total && total > 0)
            Console.WriteLine("Nessuna risposta: controlla la connessione a Internet.");

        return missing + offline == 0 ? 0 : 1;
    }

    /// <summary>La cartella del programma: quella corrente se contiene il catalogo, altrimenti quella dell'eseguibile.</summary>
    private static string FindRoot()
    {
        var current = Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(current, "catalogo")) ? current : AppContext.BaseDirectory;
    }
}
