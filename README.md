# DiagScope 2.0

Kod źródłowy aplikacji diagnostycznej DiagScope 2.0 oraz prototypu lokalnego agenta analizy logów.

- `DiagScope2-review-20261110/` — źródła programu WPF i modułów analizy (wersja przeglądowa z 11.11.2026).
- `Agent-Prototyp-20261115/` — prototyp agenta offline, bez komunikacji z samochodem.
- `docs/` — informacje o pochodzeniu tej kopii i uruchomieniu.

## Bezpieczeństwo danych

Repozytorium jest obecnie **PUBLICZNE**. Nie umieszczać tutaj logów z samochodów, prywatnych numerów VIN, raportów Auto-Scan, danych osobowych, kluczy, haseł, plików sesji ani zbudowanych programów. Publikowany jest wyłącznie kod źródłowy, dokumentacja i zasoby interfejsu.

## Budowanie

Wymagane Windows i .NET SDK 10. Otwórz `DiagScope2-review-20261110/DiagScope2.csproj`.
Prototyp agenta kompiluje się jako osobny program konsolowy, odnoszący się do silnika programu.

Ta kopia nie nadpisuje bieżącej instalacji programu na komputerze. Testy integracji z VCDS/LPGTECH przy aucie wymagają odrębnej weryfikacji.
