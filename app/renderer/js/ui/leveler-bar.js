// @ts-check
// Barra «Anivellador» (fase 5; D-148, D-149): quant puja o baixa ara el NIV d'un enregistrador o de l'streaming.
// - El zero (0 dB) té una marca ben visible; cada 6 dB hi ha una marca suau.
// - Cap a la dreta (blau) puja; cap a l'esquerra (més clar) baixa. Al costat, el número («+6,5 dB»).
// - En pausa (no hi ha veu: silenci o soroll de fons), el NIV manté el guany a propòsit, perquè no pugi el soroll entre
//   frases: la barra s'apaga una mica i surt ⏸ (color + icona + ajuda), perquè no sembli penjada.
// El mesurador continua mostrant l'entrada a l'instant (per ajustar el fader); aquesta barra diu què hi fa el NIV.
// La reben 25 vegades per segon amb l'esdeveniment meters.

/** Escala: de −12 dB (el que pot baixar com a molt) a +20 dB (el màxim que es pot configurar). */
const MIN_DB = -12;
const MAX_DB = 20;
const TICKS_DB = [-6, 6, 12, 18];

/** Posició (%) d'un valor en dB dins de la barra. @param {number} db */
const at = (db) => ((Math.min(MAX_DB, Math.max(MIN_DB, db)) - MIN_DB) / (MAX_DB - MIN_DB)) * 100;
const ZERO = at(0);

const TITLE = 'Quant puja (o baixa) ara l\'anivellador (NIV). La marca blanca és 0 dB; les suaus, cada 6 dB.\n'
  + 'El mesurador de dalt és el que entra, abans del NIV.';

export class LevelerBar {
  /** @type {HTMLElement} */ element;
  /** @type {HTMLElement} */ #fill;
  /** @type {HTMLElement} */ #value;
  #shown = '';

  constructor() {
    this.element = document.createElement('div');
    this.element.className = 'gr-row lev-row d-none';
    this.element.title = TITLE;
    this.element.innerHTML = `
      <span class="gr-caption">Anivellador</span>
      <div class="lev-bar">
        <div class="lev-fill"></div>
        ${TICKS_DB.map((db) => `<div class="lev-tick" data-db="${db}"></div>`).join('')}
        <div class="lev-zero" data-db="0"></div>
      </div>
      <span class="gr-value"><span class="lev-text">—</span></span>`;
    // La posició de les marques es posa des d'aquí: la política de seguretat (CSP) no deixa fer servir style="…" a l'HTML.
    for (const mark of /** @type {NodeListOf<HTMLElement>} */ (this.element.querySelectorAll('[data-db]'))) {
      mark.style.left = `${at(Number(mark.dataset.db))}%`;
    }
    this.#fill = /** @type {HTMLElement} */ (this.element.querySelector('.lev-fill'));
    this.#value = /** @type {HTMLElement} */ (this.element.querySelector('.gr-value'));
  }

  /** Es veu només amb el NIV activat. @param {boolean} visible */
  setVisible(visible) {
    this.element.classList.toggle('d-none', !visible);
  }

  /**
   * Estat d'ara: { gainDb, hold }, o null si encara no treballa (p. ex. sense enregistrar).
   * @param {{ gainDb: number, hold?: boolean } | null | undefined} leveler
   */
  update(leveler) {
    const key = leveler ? `${leveler.gainDb}|${!!leveler.hold}` : '';
    if (key === this.#shown) return;
    this.#shown = key;

    const hold = !!leveler?.hold;
    this.element.classList.toggle('lev-hold', hold);
    this.element.title = hold
      ? 'En pausa: no hi ha veu (silenci o soroll de fons). L\'anivellador manté el guany perquè no pugi el soroll; '
        + 'quan torni la veu, continuarà des d\'aquí.\n' + TITLE
      : TITLE;

    if (!leveler) {
      this.#fill.style.width = '0';
      this.#value.innerHTML = '<span class="lev-text">—</span>';
      return;
    }
    const gainDb = leveler.gainDb;
    const position = at(gainDb);
    this.#fill.style.left = `${Math.min(position, ZERO)}%`;
    this.#fill.style.width = `${Math.abs(position - ZERO)}%`;
    this.#fill.classList.toggle('down', gainDb < 0);
    const text = `${gainDb > 0 ? '+' : gainDb < 0 ? '−' : ''}${Math.abs(gainDb).toFixed(1).replace('.', ',')} dB`;
    this.#value.innerHTML = `${hold ? '<i class="bi bi-pause-fill" aria-label="En pausa"></i>' : ''}<span class="lev-text">${text}</span>`;
  }
}
