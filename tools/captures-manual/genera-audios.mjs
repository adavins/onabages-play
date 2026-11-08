// @ts-check
// Pas 1: àudios sintètics per a les captures del manual, amb noms neutres i les durades de D-139.
// WAV de 22,05 kHz, 16 bits, estèreo (uns 160 MB en total, a la carpeta temporal).
// «Crònica esportiva» només té so pel canal esquerre: així hi surt la marca MONO.
import fs from 'node:fs';
import path from 'node:path';
import { AUDIO } from './comu.mjs';

const RATE = 22050;

/** [carpeta, nom, segons, opcions] */
const FILES = [
  ['Musica', 'Artista A - Cançó del matí', 222],
  ['Musica', 'Artista B - Carretera', 244],
  ['Musica', 'Artista C - Sol de tarda', 198],
  ['Musica', "Grup D - Nit d'estiu", 177],
  ['Talls', "Sintonia d'entrada", 28],
  ['Talls', 'Cortineta notícies', 9],
  ['Talls', 'Falca agenda', 31],
  ['Talls', 'Sintonia de sortida', 45],
  ['Programa', 'Entrevista', 750],
  ['Programa', 'Crònica esportiva', 135, { oneChannel: true }],
  ['Programa', 'Previsió del temps', 64],
];

for (const [folder, name, seconds, opts = {}] of /** @type {[string, string, number, any][]} */ (FILES)) {
  const dir = path.join(AUDIO, folder);
  fs.mkdirSync(dir, { recursive: true });
  const frames = seconds * RATE;
  const data = Buffer.alloc(frames * 4);
  const freq = 220 + (name.length * 37) % 400;
  for (let i = 0; i < frames; i++) {
    // Un to amb el volum que va pujant i baixant (perquè la forma d'ona no sigui plana).
    const envelope = 0.5 + 0.5 * Math.sin(2 * Math.PI * i / (RATE * 1.7));
    const v = Math.round(9000 * envelope * Math.sin(2 * Math.PI * freq * i / RATE));
    data.writeInt16LE(v, i * 4);
    data.writeInt16LE(opts.oneChannel ? 0 : v, i * 4 + 2);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0); header.writeUInt32LE(36 + data.length, 4); header.write('WAVE', 8);
  header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(2, 22);
  header.writeUInt32LE(RATE, 24); header.writeUInt32LE(RATE * 4, 28); header.writeUInt16LE(4, 32); header.writeUInt16LE(16, 34);
  header.write('data', 36); header.writeUInt32LE(data.length, 40);
  fs.writeFileSync(path.join(dir, name + '.wav'), Buffer.concat([header, data]));
}
console.log(`Fet: ${FILES.length} àudios a ${AUDIO}`);
