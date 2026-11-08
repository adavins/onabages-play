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

/** Emissor d'streaming (fase 5, pas 31d; docs/protocol.md, 4.16). @type {Record<string, StateLook>} */
export const STREAM_STATES = {
  idle:         { text: 'Aturat',       icon: 'bi-circle',                    cls: 'state-idle' },
  connecting:   { text: 'Connectant',   icon: 'bi-hourglass-split',           cls: 'state-paused' },
  streaming:    { text: 'Emetent',      icon: 'bi-broadcast',                 cls: 'state-streaming' },
  reconnecting: { text: 'Reconnectant', icon: 'bi-arrow-repeat',              cls: 'state-paused' },
  error:        { text: 'Error',        icon: 'bi-exclamation-triangle-fill', cls: 'state-error' },
};

/** Etiqueta d'estat (color + icona + text) com a HTML. */
export function stateBadge(/** @type {StateLook | undefined} */ look) {
  const s = look ?? { text: '?', icon: 'bi-question-circle', cls: 'state-idle' };
  return `<span class="state ${s.cls}"><i class="bi ${s.icon}"></i> ${s.text}</span>`;
}
