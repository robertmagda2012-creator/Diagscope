# DiagScope: lokalna pamięć przypadków (prototyp)

To osobny moduł w Pythonie 3, korzystający wyłącznie z biblioteki standardowej (`sqlite3`, `tkinter`). **Nie trenuje modelu LLM**. Zapisuje historię analiz i umożliwia korzystanie z potwierdzonych przez użytkownika przypadków.

## Na laptopie Lenovo

- `C:\Dev\DiagScope-Lokalne\DiagScopeCaseMemory.py` — moduł pamięci
- `C:\Dev\DiagScope-Lokalne\Uruchom-Agenta-Laptop.ps1` — uruchamiacz, który po analizie logu zapisuje raport do bazy
- `C:\DiagScope\BazaWiedzy\diag_cases.sqlite3` — **prywatna baza** (nie wysyłać na publiczny GitHub)
- `C:\DiagScope\Analizy-Agenta` — raporty z analiz

Na pulpicie są dwa skróty: `DiagScope - Agent analizy logow` i `DiagScope - Baza wiedzy`.

## Zasady

- Kolejny log i raport agenta tworzą lub aktualizują przypadek w bazie; ponowny import tego samego raportu nie tworzy duplikatu.
- Surowe wyniki i hipotezy **nie są traktowane jako potwierdzone naprawy**.
- Wyłącznie operator może w interfejsie zatwierdzić przyczynę i sposób sprawdzenia skuteczności naprawy.
- Podobne potwierdzone przypadki są wyszukiwane prostym dopasowaniem słów w ręcznie podanym opisie pojazdu i objawów. To mechanizm wyszukiwawczy, nie automatyczne uczenie modelu.
- Nie ma jeszcze automatycznej synchronizacji SQLite między komputerami.

## Test

```powershell
python .\DiagScopeCaseMemory.py selftest
python .\DiagScopeCaseMemory.py stats
python .\DiagScopeCaseMemory.py gui
```

Trzymaj pliki logów, VIN, wyniki analiz i bazę SQLite wyłącznie lokalnie.
