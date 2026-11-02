# libmp3lame.dll

Codificador MP3 **LAME 3.100** (64 bits), que fa servir l'enregistrador per escriure MP3 directament.

- **Projecte:** https://lame.sourceforge.io/ — © The L.A.M.E. Team.
- **Llicència:** GNU LGPL 2.0 (`COPYING-LGPL-2.0.txt`). L'aplicació l'enllaça **dinàmicament** (es carrega en temps d'execució); no se n'ha modificat res. El codi font de LAME és al web del projecte.
- **Procedència del binari:** paquet «LAME 3.100 x64» de RareWares (https://www.rarewares.org/mp3-lame-libraries.php). És idèntic al que distribueix el paquet NuGet NAudio.Lame 2.1.0.
- **SHA-256:** `71DBA147B6C16D8D0F012B8E657D52E12CB8796A659E1151200D0A6C8B04BCE3`
- **Dependències:** només `KERNEL32.dll` (no cal cap «Visual C++ Redistributable»).
- **Funcions que es fan servir:** vegeu `../LameEncoder.cs`.

Per substituir-lo per una altra compilació de LAME 3.100 de 64 bits, n'hi ha prou amb reemplaçar el fitxer i actualitzar aquí el hash i la procedència.
