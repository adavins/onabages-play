// @ts-check
// Menú de perfils de la barra superior (fase 4, pas 1b; D-058).
// Un perfil és la configuració de treball d'un programa: reproductors, enregistradors, sortides,
// entrades, llistes... Canviar de perfil reinicia el motor (ho fa app.js amb onSwitch).

import { showContextMenu } from './context-menu.js';
import { choose, askText, pickOne } from './dialogs.js';

/** @typedef {{ id: string, name: string, active: boolean }} Profile */

/**
 * @param {{
 *   button: HTMLElement,
 *   request: (cmd: string, params?: object) => Promise<any>,
 *   onSettings: (settings: any) => void,
 *   onSwitch: (profile: { id: string, name: string }) => Promise<void>,
 * }} options
 *   - onSettings: el motor ha tornat ajustos nous (p. ex. en canviar el nom del perfil actiu)
 *   - onSwitch: el motor ha acceptat el canvi i s'està aturant; cal tornar-lo a arrencar
 */
export function setupProfileMenu({ button, request, onSettings, onSwitch }) {
  button.addEventListener('click', openMenu);

  async function openMenu() {
    /** @type {Profile[]} */
    let profiles;
    try {
      profiles = (await request('profile.list')).profiles;
    } catch (err) {
      await showError('Perfils', err);
      return;
    }
    const active = profiles.find((p) => p.active);

    // Sota el botó (també si s'ha obert amb el teclat).
    const rect = button.getBoundingClientRect();
    const where = new MouseEvent('click', { clientX: rect.left, clientY: rect.bottom + 2 });

    showContextMenu(where, [
      ...profiles.map((p) => ({
        label: p.name,
        icon: p.active ? 'bi-check-lg' : 'bi-person',
        active: p.active,
        action: p.active ? undefined : () => switchTo(p),
      })),
      { separator: true },
      { label: 'Perfil nou…', icon: 'bi-plus-lg', action: () => create(null) },
      { label: `Duplicar «${active?.name ?? ''}»…`, icon: 'bi-files', action: () => create(active ?? null) },
      { label: `Canviar el nom de «${active?.name ?? ''}»…`, icon: 'bi-pencil', action: () => rename(active) },
      { label: 'Eliminar un perfil…', icon: 'bi-trash', action: () => remove(profiles) },
    ]);
  }

  /** Perfil nou: buit, o còpia de `from`. Després pregunta si s'hi vol canviar. @param {Profile | null} from */
  async function create(from) {
    const name = await askText({
      title: from ? `Duplicar «${from.name}»` : 'Perfil nou',
      icon: from ? 'bi-files' : 'bi-plus-lg',
      label: 'Nom del perfil (p. ex. el del programa)',
      value: from ? `${from.name} (còpia)` : '',
      okLabel: 'Crear',
    });
    if (name === null) return;

    let created;
    try {
      created = (await request('profile.create', from ? { name, copyFrom: from.id } : { name })).profile;
    } catch (err) {
      await showError('No s\'ha pogut crear el perfil', err);
      return;
    }

    const message = from
      ? `S'ha creat «${created.name}», amb la mateixa configuració que «${from.name}».`
      : `S'ha creat «${created.name}». És buit: hi hauràs de triar les sortides i les entrades.`;
    const choice = await choose({
      title: 'Perfil creat',
      icon: 'bi-check-circle',
      message: `${message}\nVols canviar-hi ara?`,
      buttons: [
        { label: 'Ara no', value: 'no' },
        { label: 'Canviar-hi', value: 'switch', style: 'primary' },
      ],
    });
    if (choice === 'switch') await switchTo({ ...created, active: false }, { confirmed: true });
  }

  /** @param {Profile | undefined} profile */
  async function rename(profile) {
    if (!profile) return;
    const name = await askText({
      title: 'Canviar el nom del perfil',
      icon: 'bi-pencil',
      label: 'Nom del perfil',
      value: profile.name,
      okLabel: 'Desar',
    });
    if (name === null || name === profile.name) return;
    try {
      const result = await request('profile.rename', { profile: profile.id, name });
      onSettings(result.settings);
    } catch (err) {
      await showError('No s\'ha pogut canviar el nom', err);
    }
  }

  /** @param {Profile[]} profiles */
  async function remove(profiles) {
    const others = profiles.filter((p) => !p.active);
    if (others.length === 0) {
      await choose({
        title: 'Eliminar un perfil',
        icon: 'bi-info-circle',
        message: 'Només hi ha el perfil actiu, i el perfil actiu no es pot eliminar.',
        buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
      });
      return;
    }

    const id = await pickOne({
      title: 'Eliminar un perfil',
      icon: 'bi-trash',
      label: 'Perfil',
      options: others.map((p) => ({ value: p.id, label: p.name })),
      message: 'Se n\'esborrarà la configuració i les últimes llistes dels reproductors.\n'
        + 'Els fitxers d\'àudio i els enregistraments no es toquen. El perfil actiu no es pot eliminar.',
      okLabel: 'Eliminar…',
      okStyle: 'danger',
    });
    if (id === null) return;
    const profile = others.find((p) => p.id === id);

    const choice = await choose({
      title: 'Eliminar el perfil',
      icon: 'bi-exclamation-triangle-fill',
      message: `Segur que vols eliminar «${profile?.name}»? No es pot desfer.`,
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Eliminar', value: 'delete', style: 'danger' },
      ],
    });
    if (choice !== 'delete') return;
    try {
      await request('profile.delete', { profile: id });
    } catch (err) {
      await showError('No s\'ha pogut eliminar el perfil', err);
    }
  }

  /**
   * Canvia de perfil: el motor s'atura i se'n torna a arrencar un amb el perfil nou.
   * @param {Profile} profile
   * @param {{ confirmed?: boolean }} [options]
   */
  async function switchTo(profile, { confirmed = false } = {}) {
    if (!confirmed) {
      const choice = await choose({
        title: 'Canviar de perfil',
        icon: 'bi-person-badge',
        message: `Canviar al perfil «${profile.name}»?\n`
          + 'El motor es reiniciarà (uns segons) i la pantalla es tornarà a muntar amb els reproductors, '
          + 'els enregistradors i les llistes d\'aquest perfil.',
        buttons: [
          { label: 'Cancel·lar', value: 'cancel' },
          { label: 'Canviar de perfil', value: 'switch', style: 'primary' },
        ],
      });
      if (choice !== 'switch') return;
    }

    try {
      await request('profile.switch', { profile: profile.id });
    } catch (err) {
      // P. ex. hi ha un enregistrament en curs: el motor diu què cal aturar.
      await showError('Ara no es pot canviar de perfil', err);
      return;
    }
    await onSwitch(profile);
  }
}

/** @param {string} title @param {unknown} err */
function showError(title, err) {
  return choose({
    title,
    icon: 'bi-exclamation-triangle-fill',
    message: /** @type {Error} */ (err).message,
    buttons: [{ label: 'D\'acord', value: 'ok', style: 'primary' }],
  });
}
