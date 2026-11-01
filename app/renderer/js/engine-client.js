// Client WebSocket del motor.
// Envia ordres (amb resposta) i rep esdeveniments. Es reconnecta sol si la connexió cau.
//
// Ús:
//   const client = new EngineClient(port, token);
//   client.addEventListener('status', (e) => console.log(e.detail.state)); // connecting | connected | disconnected
//   client.addEventListener('ev:engine.status', (e) => console.log(e.detail));
//   client.connect();
//   const hello = await client.request('engine.hello');

export class EngineError extends Error {
  constructor(error) {
    super(error?.message ?? 'Error del motor');
    this.code = error?.code ?? 'unknown';
  }
}

export class EngineClient extends EventTarget {
  #url;
  #socket = null;
  #nextId = 1;
  #pending = new Map();
  #reconnectDelay = 1000;
  #reconnectTimer = null;
  #closedByUser = false;

  constructor(port, token) {
    super();
    this.#url = `ws://127.0.0.1:${port}/ws?token=${encodeURIComponent(token)}`;
  }

  get connected() {
    return this.#socket !== null && this.#socket.readyState === WebSocket.OPEN;
  }

  connect() {
    this.#closedByUser = false;
    this.#emit('status', { state: 'connecting' });

    const socket = new WebSocket(this.#url);
    this.#socket = socket;

    socket.addEventListener('open', () => {
      this.#reconnectDelay = 1000;
      this.#emit('status', { state: 'connected' });
    });

    socket.addEventListener('message', (e) => this.#onMessage(e.data));

    socket.addEventListener('close', () => {
      if (this.#socket !== socket) return; // una connexió antiga
      this.#rejectAll('S\'ha perdut la connexió amb el motor');
      this.#emit('status', { state: 'disconnected' });
      if (!this.#closedByUser) this.#scheduleReconnect();
    });
  }

  /** Tanca la connexió i deixa de reconnectar. */
  close() {
    this.#closedByUser = true;
    clearTimeout(this.#reconnectTimer);
    this.#socket?.close();
  }

  /**
   * Envia una ordre al motor i espera la resposta.
   * @param {string} cmd  nom de l'ordre (vegeu docs/protocol.md)
   * @param {object} params  paràmetres addicionals
   * @param {number} timeoutMs  temps màxim d'espera
   */
  request(cmd, params = {}, timeoutMs = 5000) {
    const socket = this.#socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error('El motor no està connectat'));
    }

    const id = this.#nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`El motor no ha respost a «${cmd}»`));
      }, timeoutMs);

      this.#pending.set(id, { resolve, reject, timer });
      socket.send(JSON.stringify({ v: 1, id, cmd, ...params }));
    });
  }

  #onMessage(data) {
    let msg;
    try {
      msg = JSON.parse(data);
    } catch {
      return;
    }

    // Esdeveniment
    if (typeof msg.ev === 'string') {
      this.#emit('event', msg);
      this.#emit(`ev:${msg.ev}`, msg);
      return;
    }

    // Resposta a una ordre
    const pending = this.#pending.get(msg.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.#pending.delete(msg.id);

    if (msg.ok) pending.resolve(msg.result ?? {});
    else pending.reject(new EngineError(msg.error));
  }

  #rejectAll(reason) {
    for (const { reject, timer } of this.#pending.values()) {
      clearTimeout(timer);
      reject(new Error(reason));
    }
    this.#pending.clear();
  }

  #scheduleReconnect() {
    clearTimeout(this.#reconnectTimer);
    this.#reconnectTimer = setTimeout(() => this.connect(), this.#reconnectDelay);
    this.#reconnectDelay = Math.min(this.#reconnectDelay * 2, 5000);
  }

  #emit(type, detail) {
    this.dispatchEvent(new CustomEvent(type, { detail }));
  }
}
