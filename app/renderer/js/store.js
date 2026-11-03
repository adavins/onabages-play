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
//   'devices'          ha canviat la llista de dispositius
//   'meters'           nivells nous dels mesuradors (25 per segon)

/** @typedef {import('./engine-client.js').EngineClient} EngineClient */

export class Store extends EventTarget {
  /** @type {Map<string, any>} */ players = new Map();
  /** @type {Map<string, any>} */ recorders = new Map();
  /** @type {any[]} */ devices = [];
  /** @type {any} */ hardware = null;
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

  /** Hi ha res sonant o enregistrant? (per decidir si cal confirmar en tancar) */
  get busy() {
    return {
      playing: [...this.players.values()].filter((p) => p.state === 'playing').map((p) => p.id),
      recording: [...this.recorders.values()].filter((r) => r.state === 'recording').map((r) => r.id),
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
