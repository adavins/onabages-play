// @ts-check
// Estat del motor dins de la interfície.
//
// El motor és la font de veritat: aquí només se'n guarda una còpia, que s'omple amb
// state.get en connectar-se i s'actualitza amb els esdeveniments (docs/protocol.md).
// Els panells no parlen amb el motor per llegir l'estat: escolten aquest objecte.
//
// Avisos que emet (addEventListener):
//   'reset'            s'ha tornat a llegir tot l'estat (connexió nova)
//   'player'           ha canviat un reproductor        (detail: id)
//   'recorder'         ha canviat un enregistrador      (detail: id)
//   'playlist'         ha canviat la llista d'un reproductor (detail: id del reproductor)
//   'recordings'       ha canviat la llista d'enregistraments acabats (pendents i recents)
//   'devices'          ha canviat la llista de dispositius
//   'cue'              ha canviat la preescolta (estat, pista, sortida, volum)
//   'cart'             ha canviat la botonera (botons, sortida, què sona; fase 5, pas 28c)
//   'cartPosition'     posició del que sona a la botonera (10 per segon)
//   'stream'           ha canviat l'emissor d'streaming (estat, connexió, servidors, ajustos; fase 5, pas 31d)
//   'streamProgress'   temps, bytes i reconnexions de l'emissió (1 per segon, mentre s'emet)
//   'cuePosition'      posició nova de la preescolta (10 per segon, mentre sona)
//   'meters'           nivells nous dels mesuradors (25 per segon)
//   'explorerDurations' durades noves d'àudios de l'explorador (detail: { ruta: segons | null }; fase 5, pas 15)

/** @typedef {import('./engine-client.js').EngineClient} EngineClient */

export class Store extends EventTarget {
  /** @type {Map<string, any>} */ players = new Map();
  /** @type {Map<string, any>} */ recorders = new Map();
  /** Llista de cada reproductor (clau: id del reproductor). @type {Map<string, any>} */
  playlists = new Map();
  /** Enregistraments acabats, el més nou primer (docs/protocol.md, 4.6). @type {any[]} */
  recordings = [];
  /** Preescolta (docs/protocol.md, 4.8). @type {any} */
  cue = null;
  /** Botonera (docs/protocol.md, 4.15). @type {any} */
  cart = null;
  /** Emissor d'streaming (docs/protocol.md, 4.16). @type {any} */
  stream = null;
  /** @type {any[]} */ devices = [];
  /** @type {any} */ hardware = null;
  /** Ajustos (settings.get, docs/protocol.md 4.9). @type {any} */
  settings = null;
  /** @type {any} */ engine = null;
  /** Últims nivells dels mesuradors (esdeveniment meters). @type {any} */
  meters = { players: {}, recorders: {} };

  /** @type {EngineClient | null} */
  #client = null;

  /** Comença a escoltar els esdeveniments d'un client (un de nou a cada reconnexió del motor). */
  attach(/** @type {EngineClient} */ client) {
    this.#client = client;

    client.addEventListener('ev:player.state', (e) => this.#setPlayer(/** @type {any} */ (e).detail.player));
    client.addEventListener('ev:player.position', (e) => {
      for (const p of /** @type {any} */ (e).detail.players) this.#mergePlayer(p);
    });
    client.addEventListener('ev:recorder.state', (e) => this.#setRecorder(/** @type {any} */ (e).detail.recorder));
    client.addEventListener('ev:recorder.progress', (e) => {
      for (const r of /** @type {any} */ (e).detail.recorders) this.#mergeRecorder(r);
    });
    client.addEventListener('ev:devices.changed', () => this.refreshDevices());
    client.addEventListener('ev:playlist.changed', (e) => {
      const playlist = /** @type {any} */ (e).detail.playlist;
      this.playlists.set(playlist.player, playlist);
      this.#emit('playlist', playlist.player);
    });
    client.addEventListener('ev:recordings.changed', (e) => {
      this.recordings = /** @type {any} */ (e).detail.recordings;
      this.#emit('recordings');
    });
    client.addEventListener('ev:cue.state', (e) => {
      this.cue = /** @type {any} */ (e).detail.cue;
      this.#emit('cue');
    });
    client.addEventListener('ev:cue.position', (e) => {
      const { position, duration, format } = /** @type {any} */ (e).detail;
      if (!this.cue) return;
      Object.assign(this.cue, { position, duration, format: format ?? this.cue.format });
      this.#emit('cuePosition');
    });
    // Durades dels àudios de l'explorador (pas 15): no es guarden aquí, les fa servir la pestanya Àudios.
    client.addEventListener('ev:explorer.durations', (e) => this.#emit('explorerDurations', /** @type {any} */ (e).detail.durations));
    client.addEventListener('ev:cart.state', (e) => {
      this.cart = /** @type {any} */ (e).detail.cart;
      this.#emit('cart');
    });
    client.addEventListener('ev:cart.position', (e) => {
      if (!this.cart) return;
      this.cart.playing = /** @type {any} */ (e).detail.playing;
      this.#emit('cartPosition');
    });
    client.addEventListener('ev:stream.state', (e) => {
      this.stream = /** @type {any} */ (e).detail.stream;
      this.#emit('stream');
    });
    client.addEventListener('ev:stream.progress', (e) => {
      if (!this.stream) return;
      this.stream.progress = /** @type {any} */ (e).detail.progress;
      this.#emit('streamProgress');
    });
    client.addEventListener('ev:meters', (e) => {
      this.meters = /** @type {any} */ (e).detail;
      this.#emit('meters');
    });
  }

  /** Torna a llegir tot l'estat del motor. */
  async refresh() {
    if (!this.#client) return;
    const [state, devices] = await Promise.all([
      this.#client.request('state.get'),
      this.#client.request('devices.list', { includeInactive: true }),
    ]);

    this.engine = state.engine;
    this.hardware = state.hardware;
    this.players = new Map(state.players.map((/** @type {any} */ p) => [p.id, p]));
    this.recorders = new Map(state.recorders.map((/** @type {any} */ r) => [r.id, r]));
    this.playlists = new Map(state.playlists.map((/** @type {any} */ l) => [l.player, l]));
    this.recordings = state.recordings ?? [];
    this.cue = state.cue ?? null;
    this.cart = state.cart ?? null;
    this.stream = state.stream ?? null;
    this.devices = devices.devices;
    this.#emit('reset');
  }

  async refreshDevices() {
    if (!this.#client) return;
    try {
      this.devices = (await this.#client.request('devices.list', { includeInactive: true })).devices;
      this.#emit('devices');
    } catch (err) {
      console.warn('No s\'ha pogut llegir la llista de dispositius', err);
    }
  }

  /** Dispositiu amb aquest identificador, o undefined si Windows ja no el té. */
  device(/** @type {string | null} */ id) {
    return id ? this.devices.find((d) => d.id === id) : undefined;
  }

  /** L'enregistrament pendent d'un enregistrador (el que acaba d'aturar), o undefined. */
  pendingRecording(/** @type {string} */ recorderId) {
    const r = this.recorders.get(recorderId);
    if (r?.state !== 'pending' || !r.file) return undefined;
    return this.recordings.find((i) => samePath(i.path, r.file));
  }

  /** És el pendent d'algun enregistrador? (Aquests no surten a «Recents»: es gestionen des del seu panell.) */
  isPending(/** @type {any} */ recording) {
    return [...this.recorders.values()].some((r) => r.state === 'pending' && samePath(r.file, recording.path));
  }

  /** Aquesta pista és la que sona ara a la preescolta? */
  isPreviewing(/** @type {string | null | undefined} */ path) {
    return this.cue?.state === 'playing' && samePath(this.cue.file, path ?? null);
  }

  /**
   * La preescolta surt per la mateixa sortida que algun reproductor (és a dir, per antena)?
   * Es calcula aquí perquè canvia també quan es canvia la sortida d'un reproductor.
   */
  /** La botonera surt per la mateixa sortida que algun reproductor? (Es permet: Windows els barreja.) */
  get cartSharesPlayerOutput() {
    const id = this.cart?.deviceId;
    return !!id && [...this.players.values()].some((p) => p.deviceId === id);
  }

  get cueSharesPlayerOutput() {
    const id = this.cue?.deviceId;
    return !!id && [...this.players.values()].some((p) => p.deviceId === id);
  }

  /** S'està emetent per streaming (també connectant o reconnectant)? */
  get streaming() {
    return ['connecting', 'streaming', 'reconnecting'].includes(this.stream?.state);
  }

  /** Hi ha res sonant, enregistrant o emetent? (per decidir si cal confirmar en tancar) */
  get busy() {
    return {
      playing: [...this.players.values()].filter((p) => p.state === 'playing').map((p) => p.id),
      recording: [...this.recorders.values()].filter((r) => r.state === 'recording').map((r) => r.id),
      streaming: this.streaming,
    };
  }

  #setPlayer(/** @type {any} */ p) {
    this.players.set(p.id, p);
    this.#emit('player', p.id);
  }

  #mergePlayer(/** @type {any} */ p) {
    const current = this.players.get(p.id);
    if (!current) return;
    Object.assign(current, p);
    this.#emit('player', p.id);
  }

  #setRecorder(/** @type {any} */ r) {
    this.recorders.set(r.id, r);
    this.#emit('recorder', r.id);
  }

  #mergeRecorder(/** @type {any} */ r) {
    const current = this.recorders.get(r.id);
    if (!current) return;
    Object.assign(current, r);
    this.#emit('recorder', r.id);
  }

  #emit(/** @type {string} */ type, /** @type {any} */ detail = undefined) {
    this.dispatchEvent(new CustomEvent(type, { detail }));
  }
}

/** Windows no distingeix majúscules de minúscules a les rutes. */
function samePath(/** @type {string | null} */ a, /** @type {string | null} */ b) {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase();
}
