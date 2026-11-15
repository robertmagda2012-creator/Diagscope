# DiagScope — lokalny agent (prototyp 0.1, 15.11.2026)

Nie jest to nowa instalacja DiagScope. To osobny, odwracalny prototyp CLI.
Czyta zapisane logi przez istniejący DiagScope.Engine i ocenia, jaki następny test może dać najwięcej informacji.
Nie łączy się z VCDS, LPGTECH ani z samochodem. Nie ma dostępu do sieci i nie używa AI/LLM.

## Uruchomienie
W folderze prototypu:

dotnet run -c Release -- "C:\Dev\DiagScope2.Tests\fixtures\real-vcds.csv" --lpg

Opcjonalnie:
dotnet run -c Release -- "C:\Dev\DiagScope2.Tests\fixtures\real-vcds.csv" --lpg --output raport.json

Bez --output raport trafia tylko na standardowe wyjście. Agent nie aktualizuje kart pojazdów ani źródłowych plików.

## Weryfikacja przed wdrożeniem
- odczyt VCDS CSV, JSONL lub JSON via Importer.Read
- fakty z logu są odrębne od hipotez diagnostycznych i propozycji testów
- dowody w szczegółowych zdarzeniach są dostępne przez istniejący AnalysisEngine
- heurystyczna ocena kolejnego testu: wartość informacji, czas, koszt, trudność, ryzyko
- brak automatycznych zmian w DiagScope / samochodzie
- nie interpretować połączenia statystycznego jako przyczynowego

## Następne kroki
1. Dodać interfejs integracji z aktualną wersją DiagScope na stacjonarnym.
2. Udostępnić szczegółowe dowody (oryginalne wiersze i kanały) z Engine.Analyze w UI.
3. Dodać analizę przed/po przełączeniu LPG przy zweryfikowanej synchronizacji zegarów.
4. Dopiero potem rozważyć lokalny model językowy do wyjaśniania wyników, z kontrolą halucynacji.