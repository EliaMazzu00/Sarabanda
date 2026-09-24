# 🎵 Sarabanda

**Indovina la canzone**, da giocare in casa con più schermi: un PC conduce, la TV suona
la musica e mostra chi si è prenotato, **i telefoni delle squadre sono i pulsanti di
prenotazione**. Tutto si aggiorna in tempo reale, senza installare niente sugli altri
dispositivi.

Tre fonti di canzoni, da usare anche insieme:

- 🌐 **il catalogo online**: **695 canzoni vere in 22 playlist**, dalle hit di oggi ai
  classici di sempre (pop, rock, rap, dance, disco, latino, cantautori, Sanremo, anni
  60-70-80-90, colonne sonore, sigle dei cartoni, Natale…). Si sentono le **anteprime
  ufficiali di 30 secondi** e, quando si svela il titolo, la **copertina dell'album**;
- 🎹 **30 melodie al pianoforte** già incluse, che funzionano anche senza Internet, perfette
  per il gioco delle *sette note*;
- 🎵 **le tue canzoni** (mp3, m4a, ogg, wav…), suonate per intero.

Scritto in **Blazor Server (.NET 10)**. Nessun database, nessun account. Internet serve
solo al PC della regia e solo per il catalogo online: melodie e brani propri funzionano su
una rete locale qualsiasi.

---

## Indice

- [Come si avvia](#come-si-avvia)
- [Le regole](#le-regole)
- [Le quattro schermate](#le-quattro-schermate)
- [Giocare su più schermi](#giocare-su-più-schermi)
- [Come si conduce una partita](#come-si-conduce-una-partita)
- [Le impostazioni](#le-impostazioni)
- [Scorciatoie da tastiera](#scorciatoie-da-tastiera)
- [Il catalogo online](#il-catalogo-online)
- [Le tue canzoni](#le-tue-canzoni)
- [Le melodie](#le-melodie)
- [Prenotarsi da fuori](#prenotarsi-da-fuori)
- [Configurazione](#configurazione)
- [Com'è fatto il progetto](#comè-fatto-il-progetto)
- [Nota sulla sicurezza](#nota-sulla-sicurezza)

---

## Come si avvia

### Con l'eseguibile pronto (consigliato per giocare)

Scarica lo `.zip` dalla pagina [Releases](../../releases), estrailo dove vuoi e fai
doppio clic su **`Sarabanda.exe`**. Non serve installare .NET.

Poi apri il browser su **<http://localhost:5110>**.

### Dai sorgenti (per chi sviluppa)

Serve l'[SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet run
```

> ⚠️ Alla prima esecuzione Windows chiede di **autorizzare l'app nel firewall**:
> consenti l'accesso alle **reti private**, altrimenti gli altri dispositivi non
> riescono a collegarsi.

---

## Le regole

1. Parte una canzone (o una melodia al pianoforte).
2. La prima squadra che **preme il pulsante** si prenota: la musica si ferma e la squadra
   ha qualche secondo per dire il titolo.
3. **Giusto** → punti alla squadra, sullo schermo compaiono titolo e artista e la canzone
   continua a suonare.
4. **Sbagliato** → la squadra resta fuori per questa canzone (con eventuale penalità), la
   musica riparte e le altre squadre possono prenotarsi.
5. Se nessuno la indovina, la regia svela il titolo.

Punti, penalità, tempi e il resto si decidono in regia.

---

## Le quattro schermate

| Schermata | Indirizzo | A cosa serve | Dove aprirla |
|---|---|---|---|
| **Menu** | `/` | Le regole in breve, i link e gli indirizzi di rete già pronti | dove capita |
| **Regia** | `/admin` | Squadre, canzoni, musica, giudizio, punteggi | sul PC che conduce |
| **Schermo** | `/display` | La musica, chi si è prenotato, il titolo svelato, la classifica | TV o proiettore con le casse |
| **Pulsante** | `/remote` | Il pulsante di prenotazione di una squadra | un telefono per squadra |

Il titolo lo vede **solo la regia** finché non viene svelato.

---

## Giocare su più schermi

1. Avvia l'app sul PC che conduce e apri **`/admin`**.
2. Il **menu** (`/`) mostra già gli indirizzi da usare, tipo
   `http://192.168.1.50:5110/display`.
3. Sulla TV apri quell'indirizzo, metti il browser a schermo intero con **F11** e
   **clicca una volta sulla pagina**: i browser fanno partire la musica solo dopo un
   clic. In regia, nel riquadro *Da dove esce la musica*, vedi subito se lo schermo è
   collegato e ha l'audio attivo.
4. Ogni squadra apre `/remote` sul telefono e tocca il proprio nome (oppure va dritta a
   `/remote?squadra=2`, indirizzo che la regia mostra accanto a ogni squadra).

Se un dispositivo perde la rete per un momento si ricollega da solo: **la partita vive
nel server**, non nel browser.

**Senza telefoni?** Si gioca lo stesso: le squadre gridano e in regia premi il loro
numero (tasti `1`–`8`) o il bottone *Prenota a mano*.

---

## Come si conduce una partita

La regia ha una **barra delle fasi** (Pronti → Ascolto → Risposta → Titolo) e un riquadro
**"Prossimo passo"** che dice sempre cosa fare, con il bottone giusto (o **Invio**).

1. **▶ Prepara la canzone** → sullo schermo "Canzone n. 1 — Pronti?".
2. **▶ Fai partire la musica** → da questo momento le squadre possono prenotarsi.
3. Una squadra si prenota: la musica si ferma, lo schermo si colora della squadra e parte
   il conto alla rovescia per rispondere.
4. **✓ Giusto** (`G`) o **✗ Sbagliato** (`S`). Una prenotazione partita per sbaglio si
   annulla senza penalità.
5. Finito il tempo di ascolto la musica si ferma, ma le prenotazioni restano aperte: dai
   qualche secondo e poi **👁 Svela il titolo** (`R`).
6. **⏭ Prossima canzone** (`N`).

Durante l'ascolto puoi mettere in pausa (**Spazio**), ripartire **da capo** e, per le
melodie, far sentire **due note in più**.

---

## Le impostazioni

Tutte nel pannello **Preparazione** della regia, valgono appena le cambi:

| Impostazione | Cosa fa |
|---|---|
| Squadre | Da 1 a 8, con il nome che volete e un colore ciascuna |
| Da dove esce la musica | Dallo schermo, dalla regia o da entrambi |
| Punti a chi indovina / tolti a chi sbaglia | Qualsiasi numero; il punteggio non scende sotto zero |
| Secondi per rispondere | Conto alla rovescia dopo la prenotazione (0 = nessuno) |
| Secondi di ascolto | Quanto si sente di ogni canzone (0 = tutta) |
| Da che punto parte un tuo brano | Dall'inizio, da un secondo preciso (per saltare l'intro) o da un punto a caso. I brani del catalogo partono dall'inizio dell'anteprima |
| Note delle melodie | Il gioco delle *sette note*: quante note far sentire (0 = tutte) |
| Dopo un errore la musica riparte | Sì/no |
| Quando si svela il titolo la canzone continua | Sì/no |
| Ordine casuale, playlist sullo schermo, effetti sonori | Sì/no |

---

## Scorciatoie da tastiera

| Tasto | Cosa fa |
|---|---|
| `Invio` | il **prossimo passo** (il bottone grande) |
| `Spazio` | fai suonare / metti in pausa |
| `1` – `8` | prenota quella squadra |
| `G` / `S` | risposta giusta / sbagliata |
| `R` | svela il titolo |
| `N` | prossima canzone |

Non valgono mentre stai scrivendo in un campo di testo.

---

## Il catalogo online

Nella cartella [`catalogo/`](catalogo) ci sono **695 canzoni in 22 playlist**, scelte per
coprire i generi principali con le hit degli ultimi 25 anni e i grandi classici:

| Playlist | Brani | | Playlist | Brani |
|---|---:|---|---|---:|
| Pop italiano dal 2000 | 45 | | Anni 80 italiani e italo disco | 24 |
| Pop internazionale dal 2000 | 58 | | Anni 90 internazionali | 33 |
| Rap e trap italiano | 25 | | Anni 90 italiani | 21 |
| Hip hop e R&B | 37 | | Dance ed elettronica | 34 |
| Rock italiano | 29 | | Disco e funk | 27 |
| Rock internazionale | 49 | | Latino e reggaeton | 31 |
| Cantautori italiani | 40 | | Tormentoni estivi italiani | 20 |
| Sanremo, vincitori e canzoni storiche | 40 | | Colonne sonore e canzoni dei film | 23 |
| Classici internazionali anni 50, 60 e 70 | 42 | | Sigle dei cartoni animati | 19 |
| Classici italiani anni 50, 60 e 70 | 33 | | Soul, jazz e swing | 16 |
| Anni 80 internazionali | 36 | | Canzoni di Natale | 13 |

**Come funziona.** Il catalogo contiene solo *titolo, artista e anno*: nessun file audio
viene distribuito con il programma. Quando una canzone va in gioco, il PC della regia cerca
la sua **anteprima ufficiale di 30 secondi** su Deezer (e, se serve, su iTunes), la scarica
insieme alla copertina e la passa allo schermo attraverso la rete locale. Quindi:

- **Internet serve solo al PC della regia**, non alla TV né ai telefoni;
- mentre si gioca una canzone, la successiva viene già scaricata: non si aspetta;
- in regia, sotto il titolo, compare *«Anteprima da Deezer: titolo — artista»*, per
  controllare al volo che sia il brano giusto;
- quando si svela il titolo, sullo schermo compaiono **copertina, artista e anno**;
- senza Internet la regia lo dice chiaramente e suggerisce di passare alle melodie o ai
  propri brani.

Il programma accetta un risultato solo se **artista e titolo corrispondono** e scarta live,
remix, karaoke, cover e tributi: meglio nessuna anteprima che quella sbagliata.

### Aggiungere canzoni al catalogo

Basta una riga in un file `.txt` di `catalogo/` (o un file nuovo, che diventa una playlist):

```
# TEMA: La mia playlist

Albachiara | Vasco Rossi | 1979
Bohemian Rhapsody | Queen | 1975
```

I campi sono `TITOLO | ARTISTA | ANNO` (l'anno è facoltativo). Per controllare che ogni
canzone abbia un'anteprima, dalla cartella del programma:

```powershell
.\Sarabanda.exe --verifica-catalogo           # elenca i brani non trovati
.\Sarabanda.exe --verifica-catalogo --tutti   # mostra anche cosa trova per ciascuno
```

---

## Le tue canzoni

Metti i tuoi brani nella cartella **`canzoni/`** accanto all'eseguibile:

```
canzoni/
  Anni 80/
    Lucio Dalla - Caruso.mp3
    01 - Vasco Rossi - Albachiara.mp3
  Cartoni animati/
    Cristina D'Avena - Occhi di gatto.m4a
```

- **Il nome del file è la risposta**: `Artista - Titolo`. I numeri di traccia in testa
  vengono tolti da soli.
- **Ogni sottocartella è una playlist** da spuntare in regia.
- Formati: mp3, m4a, aac, ogg, opus, wav, webm, flac.
- Dopo aver aggiunto file, in regia premi **🔄 Rileggi la cartella**.

In alternativa, dalla regia **carica i file** direttamente: finiscono nella playlist
*Caricate*. La cartella `canzoni/` non viene mai pubblicata su GitHub: i brani sono tuoi.

---

## Le melodie

Nella cartella [`melodie/`](melodie) ci sono **30 melodie** in tre temi: *Classica e
lirica*, *Tradizionali e per bambini*, *Feste e inni*. Sono brani liberi da diritti,
suonati dal browser con un pianoforte sintetizzato.

Se ne aggiungono scrivendo una riga in un file `.txt`:

```
# TEMA: I miei classici

Inno alla gioia | Ludwig van Beethoven | 120 | E4 E4 F4 G4 G4 F4 E4 D4 C4 C4 D4 E4 E4:1.5 D4:0.5 D4:2
```

- i campi sono `TITOLO | AUTORE | BPM | NOTE`;
- le note sono separate da spazi: lettera, eventuale `#` o `b`, ottava (4 = quella del
  Do centrale). Valgono anche i nomi italiani: `DO4 RE4 MI4 FA#4 SOL4 LA4 SIb4`;
- dopo i due punti la **durata in battiti** (`:0.5` una croma, `:2` una minima), 1 se manca;
- `R` è una pausa (`R:0.5`);
- le righe sbagliate vengono elencate in regia con il motivo.

---

## Prenotarsi da fuori

Per una pulsantiera vera (un ESP32, un tasto macro, uno script):

| Richiesta | Cosa fa |
|---|---|
| `GET`/`POST` `/api/prenota/{squadra}` | la squadra (da 1) si prenota |
| `GET` `/api/stato` | fotografia della partita |

```bash
curl http://192.168.1.50:5110/api/prenota/2
# {"ok":true,"esito":"prenotata"}
# {"ok":false,"esito":"troppo tardi"}
```

`/api/stato` **non restituisce il titolo** finché la regia non l'ha svelato.

---

## Configurazione

La porta si cambia in [`appsettings.json`](appsettings.json) (`"Server": { "Port": 5110 }`)
oppure con una variabile d'ambiente:

```powershell
$env:Server__Port = "8080"; .\Sarabanda.exe
```

La 5110 è scelta per convivere con gli altri giochi della serata: *L'Intesa Vincente*
(5080), *La Ruota della Fortuna* (5090) e *La Ghigliottina* (5100).

---

## Com'è fatto il progetto

```
Models/                   tipi di dati: Song, Team, PlayerCommand, enum
Services/
  SongLibrary.cs          trova i brani audio, legge melodie e catalogo
  PreviewService.cs       cerca e scarica le anteprime del catalogo (Deezer, iTunes)
  CatalogCheck.cs         la verifica del catalogo da riga di comando
  GameService.cs          lo stato della partita, condiviso da tutte le pagine
  NetworkInfo.cs          trova gli indirizzi di rete da suggerire nel menu
Components/
  Pages/                  Home, Admin, Display, Remote (+ CSS e JS accanto)
  Shared/PlayerHost       collega una pagina al lettore audio
  Shared/Equalizer        le barre al neon
wwwroot/
  app.css                 colori, tipografia e spaziature in un posto solo
  player.js               lettore dei file, pianoforte sintetizzato, effetti sonori
catalogo/                 le 695 canzoni del catalogo online (solo titolo, artista, anno)
melodie/                  le 30 melodie incluse
canzoni/                  i tuoi brani (non versionati)
```

Le idee portanti:

1. **`GameService` è un singleton** con lo stato della partita; ogni pagina si iscrive a
   `OnChange` e si ridisegna. Le scritture passano da un lock: le prenotazioni arrivano
   insieme da più telefoni e **vince la prima**.
2. **La musica suona nel browser, non nel server.** Il server manda ai lettori comandi
   numerati ("suona", "pausa", "stop") e tiene il conto di **quanto si è ascoltato** con
   un cronometro monotono, per fermare la musica allo scadere. Il punto di partenza nel
   file lo calcola ogni lettore con dati uguali per tutti, così due schermi suonano lo
   stesso punto.
3. **I file audio si servono per identificatore**, mai per percorso, con le richieste
   parziali attive (servono per partire da metà brano). Le anteprime del catalogo le
   scarica il server e le serve allo stesso modo (`/audio/online/{id}`, `/cover/{id}`).
4. **Niente framework CSS, font o suoni da Internet.** L'unica cosa che viene da fuori
   sono le anteprime del catalogo, e solo se lo si usa.

```bash
dotnet build                      # compilazione (deve restare a 0 warning)
dotnet run                        # avvia in locale
dotnet publish -c Release         # eseguibile autonomo per Windows x64
```

---

## Nota sulla sicurezza

L'app **non ha autenticazione**: chiunque sia sulla stessa rete può aprire `/admin`,
caricare file nella cartella `canzoni/Caricate` e usare `/api/*`. È voluto — in casa non
serve — ma vuol dire che va usata su una **rete locale di cui hai il controllo** e **non
va esposta su Internet**.

---

## Licenza

Progetto personale, per giocare in famiglia. Il format televisivo e il nome appartengono
ai rispettivi proprietari; questo è un gioco fatto in casa, senza alcun legame con la
trasmissione. Le melodie incluse sono di pubblico dominio. Il catalogo contiene solo titoli,
artisti e anni: l'audio sono le anteprime ufficiali messe a disposizione da Deezer e iTunes,
ascoltate al momento e mai salvate su disco. I brani che aggiungi restano tuoi e non fanno
parte del progetto.
