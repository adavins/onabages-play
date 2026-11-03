// @ts-check
// Com es mostra cada estat: sempre color + icona + text (CLAUDE.md).
// Per canviar l'aspecte d'un estat, n'hi ha prou de tocar aquesta taula
// (els colors són a css/app.css, classes .state-*).

/** @typedef {{ text: string, icon: string, cls: string }} StateLook */

/** @type {Record<string, StateLook>} */
export const PLAYER_STATES = {
  empty:   { text: 'Buit',     icon: 'bi-dash-circle',              cls: 'state-idle' },
  ready:   { text: 'Preparat', icon: 'bi-check-circle',             cls: 'state-ready' },
  playing: { text: 'Sonant',   icon: 'bi-play-fill',                cls: 'state-playing' },
  paused:  { text: 'En pausa', icon: 'bi-pause-fill',               cls: 'state-paused' },
  ended:   { text: 'Acabat',   icon: 'bi-stop-circle',              cls: 'state-idle' },
  error:   { text: 'Error',    icon: 'bi-exclamation-triangle-fill', cls: 'state-error' },
};

/** @type {Record<string, StateLook>} */
export const RECORDER_STATES = {
  idle:      { text: 'Inactiu',      icon: 'bi-circle',                    cls: 'state-idle' },
  recording: { text: 'Enregistrant', icon: 'bi-record-fill',               cls: 'state-recording' },
  pending:   { text: 'Pendent',      icon: 'bi-hourglass-split',           cls: 'state-paused' },
  error:     { text: 'Error',        icon: 'bi-exclamation-triangle-fill', cls: 'state-error' },
};

/** Etiqueta d'estat (color + icona + text) com a HTML. */
export function stateBadge(/** @type {StateLook | undefined} */ look) {
  const s = look ?? { text: '?', icon: 'bi-question-circle', cls: 'state-idle' };
  return `<span class="state ${s.cls}"><i class="bi ${s.icon}"></i> ${s.text}</span>`;
}
