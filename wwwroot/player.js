// Il lettore di Sarabanda: suona i brani audio, le melodie al pianoforte e gli
// effetti sonori.
//
// Il server non manda l'audio: manda dei comandi numerati ("suona dal secondo X",
// "pausa", "stop") e ogni pagina che suona li esegue. Per sapere da che punto del
// file partire il server dice quanto si è già ascoltato; il punto d'inizio (inizio,
// secondo fisso o punto a caso) lo calcola qui il lettore, con dati uguali per tutti.
//
// I browser bloccano l'audio finché l'utente non ha interagito con la pagina. Il
// primo clic o tocco lo sblocca; se un comando "suona" arriva prima, compare un
// grande pulsante "Tocca per attivare l'audio" (stile in app.css).
(function () {
    let ctx = null;
    let dotnet = null;
    let lastCmd = null;
    let overlay = null;

    const el = new Audio();
    el.preload = 'auto';
    let loadedSongId = '';

    let melodyBus = null;

    // ---------------------------------------------------------------
    //  Contesto audio e sblocco
    // ---------------------------------------------------------------

    function context() {
        if (!ctx) {
            const Ctor = window.AudioContext || window.webkitAudioContext;
            if (Ctor) {
                ctx = new Ctor();
            }
        }
        return ctx;
    }

    function audioReady() {
        const c = context();
        return !!c && c.state === 'running';
    }

    function report(method, ...args) {
        if (dotnet) {
            dotnet.invokeMethodAsync(method, ...args).catch(() => { });
        }
    }

    async function unlock() {
        const c = context();
        if (c && c.state !== 'running') {
            try {
                await c.resume();
            } catch {
                /* ci si riprova al prossimo gesto */
            }
        }
        const ready = audioReady();
        if (ready) {
            hideOverlay();
        }
        report('OnAudioState', ready);
        return ready;
    }

    function showOverlay() {
        if (overlay || !document.body) {
            return;
        }
        overlay = document.createElement('button');
        overlay.type = 'button';
        overlay.className = 'audio-unlock-overlay';
        overlay.innerHTML = '<span class="aul-icon">🔊</span><span class="aul-text">Tocca qui per attivare l\'audio</span>'
            + '<span class="aul-sub">Il browser fa partire la musica solo dopo un clic su questa pagina</span>';
        overlay.addEventListener('click', async () => {
            await unlock();
            hideOverlay();
            if (lastCmd) {
                apply(lastCmd, true);
            }
        });
        document.body.appendChild(overlay);
    }

    function hideOverlay() {
        if (overlay) {
            overlay.remove();
            overlay = null;
        }
    }

    ['pointerdown', 'keydown', 'touchstart'].forEach(event =>
        window.addEventListener(event, () => { unlock(); }, { passive: true }));

    // ---------------------------------------------------------------
    //  Brani audio
    // ---------------------------------------------------------------

    el.addEventListener('ended', () => {
        if (lastCmd && lastCmd.action === 'play' && lastCmd.kind === 'audio') {
            report('OnEnded', lastCmd.seq);
        }
    });

    el.addEventListener('error', () => {
        if (lastCmd && lastCmd.kind === 'audio' && lastCmd.url) {
            report('OnPlayerError', lastCmd.seq,
                'Il brano non si riesce a riprodurre: formato non supportato dal browser o file danneggiato.');
        }
    });

    function waitForMetadata() {
        if ((el.readyState >= 1 && isFinite(el.duration)) || el.error) {
            return Promise.resolve();
        }
        return new Promise((resolve) => {
            const done = () => {
                el.removeEventListener('loadedmetadata', done);
                el.removeEventListener('error', done);
                resolve();
            };
            el.addEventListener('loadedmetadata', done);
            el.addEventListener('error', done);
            setTimeout(done, 8000);
        });
    }

    /** Il secondo del file da cui parte la canzone (prima di contare l'ascolto). */
    function startOffset(cmd) {
        const duration = isFinite(el.duration) ? el.duration : 0;
        if (duration <= 0) {
            return 0;
        }
        if (cmd.startMode === 'fixed') {
            return Math.max(0, Math.min(cmd.startSeconds, duration - 5));
        }
        if (cmd.startMode === 'random') {
            // Un punto a caso fra il decimo secondo e quanto basta per ascoltare fino in fondo.
            const listen = cmd.maxListen > 0 ? cmd.maxListen : 30;
            const latest = Math.max(0, duration - listen - 3);
            const earliest = Math.min(10, latest);
            return earliest + cmd.startFraction * (latest - earliest);
        }
        return 0;
    }

    async function applyAudio(cmd) {
        stopMelody();

        if (loadedSongId !== cmd.songId) {
            loadedSongId = cmd.songId;
            el.pause();
            el.src = cmd.url;
            el.load();
        }

        if (cmd.action !== 'play') {
            el.pause();
            return;
        }

        await waitForMetadata();
        if (lastCmd !== cmd) {
            return; // nel frattempo è arrivato un altro comando
        }

        if (el.error) {
            report('OnPlayerError', cmd.seq,
                'Il brano non si riesce a riprodurre: formato non supportato dal browser o file danneggiato.');
            return;
        }

        const target = startOffset(cmd) + cmd.listened;
        if (isFinite(el.duration) && target >= el.duration - 0.2) {
            report('OnEnded', cmd.seq);
            return;
        }

        try {
            if (Math.abs(el.currentTime - target) > 0.25) {
                el.currentTime = target;
            }
            await el.play();
            hideOverlay();
            report('OnAudioState', true);
        } catch (err) {
            if (err && err.name === 'NotAllowedError') {
                report('OnAudioState', false);
                showOverlay();
            } else if (err && err.name !== 'AbortError') {
                report('OnPlayerError', cmd.seq, 'Il brano non parte: ' + (err.message || err.name));
            }
        }
    }

    // ---------------------------------------------------------------
    //  Melodie al pianoforte
    // ---------------------------------------------------------------

    function stopMelody() {
        if (melodyBus && ctx) {
            const bus = melodyBus;
            melodyBus = null;
            bus.gain.cancelScheduledValues(ctx.currentTime);
            bus.gain.setTargetAtTime(0, ctx.currentTime, 0.03);
            setTimeout(() => bus.disconnect(), 400);
        }
    }

    /** Una nota di pianoforte: tre armoniche con attacco rapido e smorzamento. */
    function pianoNote(bus, midi, when, seconds) {
        const freq = 440 * Math.pow(2, (midi - 69) / 12);
        const amp = ctx.createGain();
        const filter = ctx.createBiquadFilter();
        const decay = Math.min(2.8, seconds * 1.3 + 0.35);

        filter.type = 'lowpass';
        filter.frequency.setValueAtTime(Math.min(8000, freq * 6), when);
        filter.frequency.exponentialRampToValueAtTime(Math.max(400, freq * 1.5), when + decay);

        amp.gain.setValueAtTime(0.0001, when);
        amp.gain.exponentialRampToValueAtTime(0.32, when + 0.006);
        amp.gain.exponentialRampToValueAtTime(0.12, when + 0.25);
        amp.gain.exponentialRampToValueAtTime(0.0001, when + decay);

        [[1, 'triangle', 1], [2, 'sine', 0.35], [3, 'sine', 0.12]].forEach(([mult, type, level]) => {
            const osc = ctx.createOscillator();
            const g = ctx.createGain();
            osc.type = type;
            osc.frequency.value = freq * mult;
            g.gain.value = level;
            osc.connect(g).connect(filter);
            osc.start(when);
            osc.stop(when + decay + 0.05);
        });

        filter.connect(amp).connect(bus);
    }

    async function applyMelody(cmd) {
        el.pause();
        stopMelody();

        if (cmd.action !== 'play') {
            return;
        }

        const c = context();
        if (!c) {
            report('OnPlayerError', cmd.seq, 'Questo browser non sa suonare le melodie (manca la Web Audio API).');
            return;
        }
        if (c.state !== 'running') {
            try {
                await c.resume();
            } catch {
                /* niente */
            }
        }
        if (c.state !== 'running') {
            report('OnAudioState', false);
            showOverlay();
            return;
        }
        if (lastCmd !== cmd) {
            return;
        }

        hideOverlay();
        report('OnAudioState', true);

        melodyBus = c.createGain();
        melodyBus.gain.value = 0.9;
        melodyBus.connect(c.destination);

        const beat = 60 / cmd.bpm;
        const now = c.currentTime + 0.05;
        let t = 0;

        for (const [midi, beats] of cmd.notes) {
            const length = beats * beat;
            const end = t + length;
            if (midi >= 0 && end > cmd.listened + 0.01) {
                const offset = Math.max(0, t - cmd.listened);
                const remaining = end - Math.max(t, cmd.listened);
                pianoNote(melodyBus, midi, now + offset, remaining * 0.95);
            }
            t = end;
        }
    }

    // ---------------------------------------------------------------
    //  Comandi dal server
    // ---------------------------------------------------------------

    function stopAll() {
        stopMelody();
        el.pause();
    }

    /** Esegue un comando del server. */
    function apply(cmd, force) {
        if (!force && lastCmd && lastCmd.seq === cmd.seq) {
            return;
        }
        lastCmd = cmd;

        if (cmd.action === 'stop' || !cmd.songId) {
            stopAll();
            return;
        }

        if (cmd.kind === 'melody') {
            applyMelody(cmd);
        } else if (!cmd.url) {
            // Brano del catalogo con l'anteprima non ancora arrivata: si aspetta il
            // prossimo comando, che avrà l'indirizzo.
            stopAll();
        } else {
            applyAudio(cmd);
        }
    }

    /** Questa pagina smette di suonare (es. la musica ora esce da un'altra pagina). */
    function silence() {
        lastCmd = null;
        stopAll();
        hideOverlay();
    }

    // ---------------------------------------------------------------
    //  Effetti sonori
    // ---------------------------------------------------------------

    function tone(freq, start, duration, type = 'square', gain = 0.12, slideTo = null) {
        const c = context();
        const osc = c.createOscillator();
        const amp = c.createGain();
        const t0 = c.currentTime + start;

        osc.type = type;
        osc.frequency.setValueAtTime(freq, t0);
        if (slideTo) {
            osc.frequency.exponentialRampToValueAtTime(slideTo, t0 + duration);
        }
        amp.gain.setValueAtTime(0.0001, t0);
        amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.01);
        amp.gain.setValueAtTime(gain, t0 + duration * 0.7);
        amp.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);
        osc.connect(amp).connect(c.destination);
        osc.start(t0);
        osc.stop(t0 + duration + 0.05);
    }

    // Ogni squadra ha il suo campanello, di altezza diversa.
    const BUZZ_PITCH = [392, 494, 587, 659, 740, 880, 988, 1175];

    const effects = {
        NewSong() {
            tone(523, 0, 0.12, 'triangle', 0.15);
            tone(784, 0.1, 0.25, 'triangle', 0.15);
        },
        Buzz(team) {
            const f = BUZZ_PITCH[team % BUZZ_PITCH.length];
            tone(f, 0, 0.45, 'square', 0.14);
            tone(f * 1.5, 0, 0.45, 'sawtooth', 0.05);
        },
        Correct() {
            [523, 659, 784, 1046].forEach((f, i) => tone(f, i * 0.09, 0.3, 'triangle', 0.18));
            tone(1318, 0.36, 0.6, 'triangle', 0.16);
        },
        Wrong() {
            tone(180, 0, 0.28, 'sawtooth', 0.14);
            tone(150, 0.3, 0.45, 'sawtooth', 0.14);
        },
        Reveal() {
            tone(392, 0, 0.5, 'triangle', 0.12, 784);
        },
        AnswerTimeUp() {
            tone(880, 0, 0.12, 'square', 0.1);
            tone(880, 0.18, 0.12, 'square', 0.1);
            tone(660, 0.36, 0.3, 'square', 0.1);
        }
    };

    function sfx(name, team) {
        if (!audioReady() || !effects[name]) {
            return;
        }
        try {
            effects[name](team || 0);
        } catch {
            /* un effetto perso non ferma il gioco */
        }
    }

    // ---------------------------------------------------------------
    //  Interfaccia per Blazor
    // ---------------------------------------------------------------

    window.sarabanda = {
        /** Collega la pagina: da qui in poi riceve fine brano, errori e stato audio. */
        attach(ref) {
            dotnet = ref;
            report('OnAudioState', audioReady());
        },
        detach() {
            dotnet = null;
            silence();
        },
        apply,
        silence,
        sfx,
        unlock,
        vibrate(ms) {
            if (navigator.vibrate) {
                navigator.vibrate(ms || 120);
            }
        },
        /** Fotografia del lettore, utile per capire dalla console cosa sta succedendo. */
        state() {
            return {
                comando: lastCmd ? { seq: lastCmd.seq, azione: lastCmd.action, tipo: lastCmd.kind } : null,
                audioAttivo: audioReady(),
                file: el.currentSrc || null,
                secondo: Math.round(el.currentTime * 10) / 10,
                durata: isFinite(el.duration) ? Math.round(el.duration) : null,
                inPausa: el.paused,
                melodia: !!melodyBus
            };
        }
    };
})();
