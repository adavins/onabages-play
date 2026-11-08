// @ts-check
// Rutes comunes de les eines de captures del manual (D-139, D-151).
// Tot el que es genera (àudios, dades del perfil de mostra, captures) va a una carpeta temporal de Windows,
// fora del repositori i fora de les dades de l'usuari.
import os from 'node:os';
import path from 'node:path';

export const HERE = import.meta.dirname;
export const REPO = path.resolve(HERE, '..', '..');
export const APP = path.join(REPO, 'app');
export const MANUAL = path.join(REPO, 'docs', 'manual-usuari.html');

export const WORK = path.join(os.tmpdir(), 'OnaBagesCaptures');
export const AUDIO = path.join(WORK, 'audios');
export const DATA = path.join(WORK, 'dades');
export const SHOTS = path.join(WORK, 'captures');

export const sleep = (/** @type {number} */ ms) => new Promise((r) => setTimeout(r, ms));
