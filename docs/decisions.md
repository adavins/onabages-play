# Registre de decisions

Una línia per decisió. Les decisions noves van al final. Si una decisió en substitueix una altra, indica-ho.

| # | Data | Decisió | Motiu |
|---|---|---|---|
| D-001 | 2026-11-01 | Aplicació per a Windows, només àudio, per a programes en directe amb operador (no 24/7) | Ús real de l'emissora |
| D-002 | 2026-11-01 | Motor en C# / .NET 10 amb NAudio (substitueix C++ de la v1.0) | Un sol desenvolupador amb poca experiència en C++; menys risc i depuració més fàcil |
| D-003 | 2026-11-01 | Interfície Electron + HTML + Bootstrap 5 + JavaScript pur (substitueix React + TypeScript) | Tecnologia coneguda pel desenvolupador, sense compilació |
| D-004 | 2026-11-01 | Comunicació per WebSocket local (127.0.0.1 + testimoni), missatges JSON versionats | La interfície es pot reiniciar sense aturar l'àudio; es pot provar des del navegador |
| D-005 | 2026-11-01 | Configuració en JSON amb `schemaVersion` i un sol mòdul d'accés a dades; SQLite només si cal | Simplicitat ara, migració fàcil després |
| D-006 | 2026-11-01 | Enregistrament directe a MP3 320 kbps CBR, un sol fitxer, bolcat al disc cada 2 s, marcador `.rec` | Estalvi de temps i disc; fitxer vàlid en tot moment |
| D-007 | 2026-11-01 | Mode compartit de WASAPI; dispositius estèreo separats; sense ASIO ni mode exclusiu | Maquinari de consum (Sound Blaster, integrat, USB) |
| D-008 | 2026-11-01 | Preescolta amb sortida pròpia dins del MVP | Substitueix l'ús d'una instància de Winamp |
| D-009 | 2026-11-01 | Usuaris, EQ/compressor/limitador, perfils i panells mòbils fora del MVP | Abast assumible per a una sola persona |
| D-010 | 2026-11-01 | Desenvolupament amb VS Code + Claude Code; regles a `CLAUDE.md` | Eina de treball del projecte |
| D-011 | 2026-11-01 | Motor amb ASP.NET Core (Kestrel) mínim per al WebSocket, sense paquets NuGet externs | Inclòs a .NET; robust i ben documentat |
| D-012 | 2026-11-01 | Proves amb xUnit al projecte `OnaBages.Tests` | Estàndard a .NET, integrat amb `dotnet test` |
| D-013 | 2026-11-02 | Electron fixat a `^45.2.1` (abans `>=38.0.0`) i `app/package-lock.json` al repositori | Evita que una versió major nova d'Electron entri sense voler; tots els ordinadors instal·len les mateixes versions |
| D-014 | 2026-11-02 | Escala de la interfície configurable amb el zoom d'Electron (fase 2) | Adaptar l'aplicació a pantalles de baixa resolució sense redissenyar-la; sense dependències noves |
