// @ts-check
// Fader de volum d'un reproductor, en dB (de −60, silenci, a 0).
// Doble clic: torna a 0 dB.
// Mentre es mou, s'envia al motor com a molt cada 50 ms (no a cada píxel).

const MIN_DB = -60;
const SEND_EVERY_MS = 50;

export class VolumeFader {
  /** @type {HTMLElement} */ element;
  /** @type {HTMLInputElement} */ #input;
  /** @type {HTMLElement} */ #label;
  #onChange;
  #dragging = false;
  /** @type {number | undefined} */ #timer;
  /** @type {number | null} */ #pending = null;

  /** @param {(db: number) => void} onChange  envia el volum nou al motor */
  constructor(onChange) {
    this.#onChange = onChange;
    this.element = document.createElement('div');
    this.element.className = 'fader';
    this.element.innerHTML = `
      <i class="bi bi-volume-up" title="Volum"></i>
      <input type="range" class="form-range" min="${MIN_DB}" max="0" step="0.5" value="0"
             title="Volum (doble clic: 0 dB)" aria-label="Volum">
      <span class="fader-value">0.0 dB</span>`;

    this.#input = /** @type {HTMLInputElement} */ (this.element.querySelector('input'));
    this.#label = /** @type {HTMLElement} */ (this.element.querySelector('.fader-value'));

    this.#input.addEventListener('pointerdown', () => { this.#dragging = true; });
    this.#input.addEventListener('pointerup', () => { this.#dragging = false; });
    this.#input.addEventListener('input', () => this.#changed(Number(this.#input.value)));
    this.#input.addEventListener('dblclick', () => {
      this.#input.value = '0';
      this.#changed(0);
    });
  }

  /** Valor que diu el motor. Mentre l'operador el mou, no es toca (si no, saltaria). */
  update(/** @type {number} */ db) {
    if (this.#dragging || this.#pending !== null) return;
    this.#input.value = String(db);
    this.#showValue(db);
  }

  #changed(/** @type {number} */ db) {
    this.#showValue(db);
    this.#pending = db;
    if (this.#timer === undefined) this.#flush();
  }

  /** Envia el valor pendent i espera una mica abans d'enviar-ne un altre. */
  #flush() {
    if (this.#pending === null) {
      this.#timer = undefined;
      return;
    }
    this.#onChange(this.#pending);
    this.#pending = null;
    this.#timer = window.setTimeout(() => this.#flush(), SEND_EVERY_MS);
  }

  #showValue(/** @type {number} */ db) {
    this.#label.textContent = db <= MIN_DB ? '−∞ dB' : `${db.toFixed(1)} dB`;
    this.element.classList.toggle('muted', db <= MIN_DB);
    this.element.classList.toggle('lowered', db < 0 && db > MIN_DB);
  }
}
