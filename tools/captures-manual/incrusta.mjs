// @ts-check
// Pas 4: posa les captures noves dins de docs/manual-usuari.html (el manual és un sol fitxer, amb les imatges en
// base64). Cada captura substitueix la imatge que té aquest text alternatiu (alt): així no importa en quin ordre són.
// Les captures que no hi són (p. ex. si només se n'han fet algunes) es deixen com estaven.
import fs from 'node:fs';
import path from 'node:path';
import { MANUAL, SHOTS } from './comu.mjs';

/** Captura (a la carpeta de captures, sense .jpg) → text alternatiu de la imatge del manual que substitueix. */
const IMAGES = {
  principal: 'Finestra principal amb tres reproductors i dos enregistradors',
  'panell-lateral': 'Finestra principal amb el panell lateral obert a la pestanya Streaming',
  enregistrador: 'Un enregistrador inactiu',
  streaming: 'Pestanya Streaming del panell lateral',
};

let html = fs.readFileSync(MANUAL, 'utf8');
let changed = 0;
for (const [name, alt] of Object.entries(IMAGES)) {
  const file = path.join(SHOTS, name + '.jpg');
  if (!fs.existsSync(file)) { console.log(`(sense captura) ${name}`); continue; }
  // <img src="data:image/jpeg;base64,…"  alt="…">  (entre els dos hi pot haver un salt de línia)
  const pattern = new RegExp(`(<img src="data:image/jpeg;base64,)[^"]+("\\s+alt="${alt.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}")`);
  if (!pattern.test(html)) { console.log(`NO TROBADA al manual: «${alt}»`); continue; }
  const base64 = fs.readFileSync(file).toString('base64');
  html = html.replace(pattern, (_, start, end) => start + base64 + end);
  changed++;
  console.log(`Substituïda: ${alt}`);
}
fs.writeFileSync(MANUAL, html);
console.log(`Fet: ${changed} imatges. Manual: ${(fs.statSync(MANUAL).size / 1048576).toFixed(2)} MB`);
