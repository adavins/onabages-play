// @ts-check
// Mesurador de nivell estèreo en dBFS (com mp3DirectCut): dues barres horitzontals,
// retenció del pic i indicador de saturació que queda encès fins que s'hi clica.
//
// El motor envia el pic de cada element 25 vegades per segon (esdeveniment meters).
// Aquí només es fa la part visual: caiguda suau, retenció i saturació.
// Colors i mides: variables --obp-meter-* a css/app.css.

/** Escala: de MIN_DB (a l'esquerra) a 0 dBFS (a la dreta). */
const MIN_DB = -60;

/** Marques de l'escala. */
const TICKS = [-48, -36, -24, -18, -12, -6, -3, 0];

/** Velocitat de caiguda de la barra (dB per segon). */
const FALL_DB_PER_SECOND = 24;

/** Quant temps es queda la marca del pic. */
const HOLD_MS = 1500;

/** A partir d'aquí es considera saturació (pràcticament 0 dBFS). */
const CLIP_DB = -0.1;

/** L'indicador de saturació s'apaga sol al cap d'aquest temps sense saturar (D-033). */
const CLIP_HOLD_MS = 15000;

/** Posició (0..100 %) d'un valor en dB dins l'escala. */
const percent = (/** @type {number} */ db) => Math.min(100, Math.max(0, ((db - MIN_DB) / -MIN_DB) * 100));

export class LevelMeter {
  /** @type {HTMLElement} */ element;
  /** @type {{ mask: HTMLElement, hold: HTMLElement, shown: number, holdDb: number, holdAt: number }[]} */
  #channels = [];
  /** @type {HTMLElement} */ #clip;
  /** Quan ha saturat per última vegada (0 si l'indicador és apagat). */
  #clipAt = 0;
  #lastUpdate = performance.now();

  constructor() {
    this.element = document.createElement('div');
    this.element.className = 'meter';
    this.element.innerHTML = `
      <div class="meter-channels">
        <div class="meter-bar"><div class="meter-mask"></div><div class="meter-hold"></div></div>
        <div class="meter-bar"><div class="meter-mask"></div><div class="meter-hold"></div></div>
        <div class="meter-scale"></div>
      </div>
      <button class="meter-clip" type="button" title="Saturació. Clica per apagar l'indicador.">CLIP</button>`;

    // Marques de l'escala. (La posició es posa amb .style: la política de seguretat
    // de la pàgina no deixa posar style="..." dins de l'HTML.)
    const scale = /** @type {HTMLElement} */ (this.element.querySelector('.meter-scale'));
    for (const db of TICKS) {
      const tick = document.createElement('span');
      tick.textContent = String(db);
      tick.style.left = `${percent(db)}%`;
      scale.appendChild(tick);
    }

    for (const bar of this.element.querySelectorAll('.meter-bar')) {
      this.#channels.push({
        mask: /** @type {HTMLElement} */ (bar.querySelector('.meter-mask')),
        hold: /** @type {HTMLElement} */ (bar.querySelector('.meter-hold')),
        shown: MIN_DB,
        holdDb: MIN_DB,
        holdAt: 0,
      });
    }

    this.#clip = /** @type {HTMLElement} */ (this.element.querySelector('.meter-clip'));
    this.#clip.addEventListener('click', () => this.resetClip());
    this.#draw();
  }

  /**
   * Nous nivells del motor.
   * @param {number[]} levels  [esquerre, dret] en dBFS
   */
  update(levels) {
    const now = performance.now();
    const fall = (FALL_DB_PER_SECOND * (now - this.#lastUpdate)) / 1000;
    this.#lastUpdate = now;

    this.#channels.forEach((ch, i) => {
      const db = levels[i] ?? MIN_DB;
      // Puja de cop, baixa a poc a poc (més fàcil de llegir).
      ch.shown = Math.max(db, ch.shown - fall);
      if (db >= ch.holdDb || now - ch.holdAt > HOLD_MS) {
        ch.holdDb = db;
        ch.holdAt = now;
      }
      if (db >= CLIP_DB) {
        this.#clipAt = now;
        this.#clip.classList.add('on');
      }
    });
    if (this.#clipAt > 0 && now - this.#clipAt > CLIP_HOLD_MS) this.resetClip();
    this.#draw();
  }

  /** Apaga l'indicador de saturació (clic, canvi de pista o 15 s sense saturar). */
  resetClip() {
    this.#clipAt = 0;
    this.#clip.classList.remove('on');
  }

  /** Mesurador apagat (p. ex. l'entrada no està disponible). */
  setActive(/** @type {boolean} */ active) {
    this.element.classList.toggle('inactive', !active);
    if (!active) this.update([MIN_DB, MIN_DB]);
  }

  #draw() {
    for (const ch of this.#channels) {
      // La barra té els colors de fons (verd, groc, vermell) i una màscara fosca tapa la part apagada.
      ch.mask.style.width = `${100 - percent(ch.shown)}%`;
      ch.hold.style.left = `${percent(ch.holdDb)}%`;
      ch.hold.classList.toggle('d-none', ch.holdDb <= MIN_DB);
    }
  }
}
