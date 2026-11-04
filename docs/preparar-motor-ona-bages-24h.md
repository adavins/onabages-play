# Preparar el motor d'Ona Bages Play per a Ona Bages 24h

*Encàrrec per a Claude Code · 4 de novembre de 2026*

## Context

Ona Bages 24h és una aplicació d'**emissió continuada les 24 hores**. Substituirà RadioDJ a l'ordinador de continuïtat de l'emissora. Tindrà la mateixa arquitectura que Ona Bages Play: Electron + JS pur + Bootstrap, i un motor en C# / .NET 10 amb NAudio, comunicats per WebSocket.

Encara no es comença a desenvolupar: **Ona Bages Play és prioritari i s'ha d'acabar primer.** Ara només es vol que el motor de Play quedi preparat per poder-lo reutilitzar després, sense desviar-se de la fase actual ni posar en risc el que ja funciona.

Llegeix primer `CLAUDE.md`. Totes les seves regles continuen vigents, sobretot aquestes:
- **Abast:** implementa només el que diu aquest document i la fase actual. La resta, anota-la.
- **Passos petits**, cadascun amb la seva prova.
- **Decisions** a `docs/decisions.md`.
- **Protocol** a `docs/protocol.md`.

## Què necessitarà Ona Bages 24h del motor

Aquest apartat és només context, per orientar les decisions. **No s'ha d'implementar ara.**

1. **Mesclador intern:** l'ordinador de continuïtat té una sola sortida estèreo. Música, falques, senyals horaris, sweepers i streams surten tots per la mateixa sortida, barrejats dins del motor i amb un processador d'àudio senzill (compressor i volum) a la sortida.
2. **Solapament entre pistes:** com RadioDJ, cada pista té tres punts calculats automàticament quan s'importa:
   - **inici:** primer punt on el nivell supera **−25 dB**;
   - **mescla:** últim punt on el nivell és per sobre de **−15 dB**; aquí comença la pista següent;
   - **final:** últim punt on el nivell és per sobre de **−30 dB**.

   Entre la mescla i el final, la pista que acaba fa un fade de sortida mentre la següent ja sona. A la biblioteca actual el solapament és d'**1,2 s de mitjana** (en 8 de cada 10 pistes, entre 0,2 i 3,8 s).
3. **Inici a l'hora exacta:** els senyals horaris i els esdeveniments han de començar a l'hora en punt, amb menys de 50 ms d'error. Per aconseguir-ho, el motor prepara la pista uns 30 s abans i la dispara en el moment just.
4. **Fitxers que es substitueixen:** l'Automatitzador substitueix fitxers de programes, promos i falques. El motor no els ha de bloquejar, i ha de tornar a calcular la durada i els punts si el fitxer ha canviat.
5. **Streams HTTP** (butlletins d'altres emissores), amb durada màxima i pista de reserva si la connexió falla.
6. **Supervisió desatesa:** detectar una pista encallada, silenci a la sortida o el motor penjat, i enviar avisos per correu.

## Encàrrec

Fes els passos en ordre. **Abans de començar, proposa un pla breu i espera que l'aprovi.**

### Pas 1 — Revisió de la frontera de `OnaBages.Audio`

Revisa si `OnaBages.Audio` es pot fer servir des d'un altre motor sense canvis, i fes un informe breu (sense tocar codi) que respongui aquestes preguntes:
- Depèn de `OnaBages.Engine`, de la configuració, del protocol o de rutes de `%APPDATA%`?
- Quines classes són reutilitzables tal qual, quines estan pensades només per a Play, i què caldria separar?
- Com es podria compartir la biblioteca entre els dos repositoris? Compara submòdul de Git, paquet NuGet local i còpia, amb avantatges i inconvenients per a un sol desenvolupador.

Afegeix una decisió a `docs/decisions.md`: «`OnaBages.Audio` es manté independent del motor perquè es pugui reutilitzar a Ona Bages 24h». Si la revisió troba dependències que s'haurien de treure, anota-les com a tasques i **no les facis** sense que s'aprovin.

### Pas 2 — Fitxers que no es bloquegen

Comprova amb una prova automàtica què passa si un fitxer se substitueix mentre `AudioFileSource` el té obert: un altre procés l'esborra o el reanomena i en posa un de nou amb el mateix nom.
- Si `MediaFoundationReader` bloqueja el fitxer, documenta-ho i proposa una solució. Per exemple: obrir-lo amb `FileShare.ReadWrite | FileShare.Delete` a través d'un `Stream`, o carregar a memòria els fitxers curts.
- **No canviïs el comportament de Play** sense aprovació. N'hi ha prou amb la prova i l'informe.

Això també és útil per a Play: avui un fitxer d'una llista podria quedar bloquejat mentre sona.

### Pas 3 — Punts de la pista (només si el solapament de Play ja s'ha fet o s'està fent)

Si ja s'està treballant en el solapament entre pistes de Play, fes que es basi en un model compartit:
- `TrackCuePoints` (a `OnaBages.Audio`) amb `Start`, `Mix` i `End` en segons.
- Un analitzador que els calculi amb llindars configurables. Per defecte, **−25 / −15 / −30 dB**, com RadioDJ.
- Proves amb àudio sintètic: un to amb silenci al principi i un fade de sortida al final, comprovant que els punts surten on toca (amb una tolerància de pocs mil·lisegons).

L'analitzador ha d'anar en un fil de fons, mai al fil d'àudio, com el `TrackAnalyzer` actual. Si a Play encara no s'ha començat el solapament, **no facis aquest pas**: només anota'l.

### Pas 4 — Notes de disseny per al mesclador i l'inici a l'hora exacta (només document)

Escriu `docs/notes-ona-bages-24h.md` amb una proposta breu, **sense implementar-la**, que expliqui:
- Com passaria el `Player` actual de tenir una sortida pròpia a enviar el so a un **mesclador compartit** (per exemple, `MixingSampleProvider` amb una entrada per reproductor i una sola sortida WASAPI), sense que Play deixi de funcionar com ara.
- Com es podria **preparar una pista i fer-la sonar en un instant concret**, amb menys de 50 ms d'error. Ara `Play` espera fins a 1 s que hi hagi 0,5 s preparats (D-020). Quina latència afegeix WASAPI en mode compartit (100 ms), i com es compensaria?
- Com es faria el **solapament** quan dues pistes de la mateixa llista se superposen, ara que la lectura encadena les pistes al mateix fil (D-031).
- Quins riscos té cada proposta per a la regla «el fil d'àudio no pot bloquejar ni crear objectes».

## Què no s'ha de fer ara

- Cap canvi al protocol, a la interfície ni a la configuració de Play que no sigui necessari per als passos anteriors.
- Ni base de dades, ni AutoDJ, ni planificador, ni streams, ni avisos per correu: tot això és d'Ona Bages 24h.
- Cap dependència nova.

## Lliurament

Al final de cada pas, dona:
1. un resum en llenguatge planer del que has fet i per què;
2. com ho pot provar el desenvolupador;
3. un missatge de commit en català.
