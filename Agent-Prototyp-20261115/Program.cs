using System.Text.Json;
using System.Text.Encodings.Web;
using DiagScope2;
using DiagScope2.Analysis;

namespace DiagScope.Agent;

// Prototyp offline: tylko odczyt logu i propozycje testów. Bez sterowania autem i bez modelu sieciowego.
public sealed record TestProposal(
    string Id, string Status, string Title, string Reason, string[] Steps,
    int InformationValue, int TimeMinutesEstimate, int CostPoints, int DifficultyPoints,
    int RiskPoints, double PriorityScore, string[] Constraints);
public sealed record Observation(string Kind, string Status, int Count, string[] Examples);
public sealed record AgentOutput(
    string Schema, string AgentVersion, DateTimeOffset AnalyzedUtc,
    string Source, string Sha256, int Channels, int Rows,
    Observation[] Observations, Finding[] Findings, TestProposal[] NextTests, string[] Limitations);

public static class LocalAgent
{
    public static AgentOutput Run(LogData log, bool lpgInstalled)
    {
        var report = Engine.Analyze(log);
        var observations = report.Findings
            .GroupBy(x => new { x.Kind, x.Status })
            .Select(g => new Observation(g.Key.Kind, g.Key.Status, g.Count(),
                g.Take(3).Select(x => x.Title + ": " + x.Reason).ToArray()))
            .OrderBy(x => x.Kind).ToArray();

        var tests = new List<TestProposal>();
        void Suggest(string id, string title, string reason, string[] steps,
            int info, int minutes, int cost, int difficulty, int risk, params string[] constraints)
        {
            // Jawna, orientacyjna punktacja testu; nie jest to klasyfikator usterek.
            double priority = 20 * info - minutes / 15.0 - 3 * cost - 2 * difficulty - 5 * risk;
            tests.Add(new(id, "DO SPRAWDZENIA", title, reason, steps,
                info, minutes, cost, difficulty, risk, Math.Round(priority, 2), constraints));
        }

        int gaps = report.Findings.Count(f => f.Kind == "gap");
        int issues = report.Findings.Count(f => f.Kind == "hypothesis" &&
            f.Title.Contains("ciągłości", StringComparison.OrdinalIgnoreCase));
        int jumps = report.Findings.Count(f => f.Kind == "jump");
        int states = report.Findings.Count(f => f.Kind == "state");
        bool hasLambda = log.Channels.Any(c =>
            c.Name.Contains("Lambda", StringComparison.OrdinalIgnoreCase) ||
            c.OriginalName.Contains("Lambda", StringComparison.OrdinalIgnoreCase));
        bool hasSwitchMarker = log.Markers.Any(m =>
            m.Label.Contains("LPG", StringComparison.OrdinalIgnoreCase) &&
            !m.Source.Contains("NIEZNANA NIEPEWNOŚĆ", StringComparison.OrdinalIgnoreCase) &&
            double.IsFinite(m.Time) && double.IsFinite(m.Uncertainty) &&
            m.Uncertainty >= 0);

        if (gaps > 0 || issues > 0)
            Suggest("continuity", "Sprawdź ciągłość i źródło zapisu",
                $"W danych znaleziono {gaps} luk i {issues} ostrzeżeń jakości; to nie dowodzi awarii ECU.",
                ["Porównaj przedziały z oryginalnym logiem.", "Zobacz, czy luka dotyczy wszystkich kanałów.", "Przy kolejnej sesji rozdziel źródła: DiagScope, VCDS, łącze."],
                5, 15, 0, 1, 1,
                "Źródła mogą mieć niezależne zegary; nie wolno scalać czasu bez synchronizacji.");

        if (lpgInstalled && !hasSwitchMarker)
            Suggest("fuel_marker", "Dodaj wiarygodny znacznik benzyna/LPG",
                "Brak potwierdzonego znacznika przełączenia paliwa w analizowanym logu.",
                ["Oznacz ręcznie przedział przełączenia i jego niepewność.", "Przy następnym zapisie rejestruj stan paliwa z Gas Setting, jeśli działa odczyt.", "Porównuj wyłącznie zsynchronizowane źródła."],
                5, 10, 0, 1, 1,
                "Nie zgaduj chwili przełączenia na podstawie samej sondy lambda.");

        if (lpgInstalled && hasSwitchMarker)
            Suggest("fuel_windows", "Porównaj okna przed i po zmianie paliwa",
                "Znaleziono znacznik paliwa. Można zbadać okna wokół niego, z uwzględnieniem niepewności.",
                ["Sprawdź lambda, korekty mieszanki, RPM i ciśnienie LPG.", "Odrzuć odcinki ze zmianą obciążenia lub luką w zapisie.", "Zapisz tylko zaobserwowane różnice; nie przypisuj automatycznie przyczyny wtryskiwaczowi."],
                5, 20, 0, 2, 1,
                "Zależność czasowa nie dowodzi związku przyczynowego.");

        if (jumps > 0)
            Suggest("jump_context", "Ustal warunki wokół skoków parametrów",
                $"Wykryto {jumps} statystycznych kandydatów na skok.",
                ["Dla każdego skoku pokaż wartości przed i po oraz RPM, obciążenie i stan paliwa.", "Sprawdź luki i reset czasu w tym samym przedziale.", "Odróżnij naturalną zmianę pracy silnika od błędu pomiaru."],
                4, 20, 0, 2, 1,
                "Próg MAD jest heurystyczny; nie jest normą producenta.");

        if (hasLambda)
            Suggest("lambda_context", "Sprawdź interpretację sygnałów lambda",
                "Log zawiera kanały nazwane lambda. Sama zmiana napięcia nie rozstrzyga o sprawności sondy.",
                ["Zweryfikuj mapowanie banku i numeru sondy.", "Sprawdź temperaturę i warunki pracy oraz korekty mieszanki.", "Porównaj benzynę i LPG dopiero przy wiarygodnych znacznikach."],
                4, 20, 0, 2, 1,
                "Brak automatycznej bazy wartości referencyjnych sterownika.",
                "Nie utożsamiaj nazwy kanału z bankiem bez potwierdzenia.");

        if (states > 0)
            Suggest("state_meaning", "Zweryfikuj zmiany stanów tekstowych",
                $"Zapisano {states} zmian stanu; znaczenie kodów wymaga dokumentacji.",
                ["Odczytaj stany w oryginalnym źródle.", "Porównaj ich przedziały z pozostałymi parametrami."],
                3, 10, 0, 1, 1,
                "Czas zmiany leży pomiędzy próbkami, a nie dokładnie w znaczniku.");

        if (tests.Count == 0)
            Suggest("baseline", "Uzupełnij kontekst badania",
                "Brak przesłanek do zaproponowania testu specyficznego dla kanału.",
                ["Dodaj cel badania, objawy, źródło logu i parametry potrzebne do oceny.", "Nie interpretuj braku alarmów jako potwierdzenia sprawności pojazdu."],
                3, 10, 0, 1, 1,
                "Analiza nie ma wiedzy o przyczynie objawu.");

        return new AgentOutput(
            "diagscope-local-agent-1", "0.1-prototype", DateTimeOffset.UtcNow,
            log.Name, log.Hash, log.Channels.Count, log.Rows,
            observations,
            report.Findings.Take(250).ToArray(),
            tests.OrderByDescending(t => t.PriorityScore).ThenBy(t => t.Id).Take(7).ToArray(),
            report.Limitations.Concat([
                "Analizator nie steruje pojazdem, nie zapisuje konfiguracji i nie korzysta z sieci.",
                "Oceny czasu, kosztu, trudności i ryzyka są orientacyjne; użytkownik podejmuje decyzję o teście.",
                "Wnioski POTWIERDZONE odnoszą się do danych zapisanych w pliku, nie do diagnozy podzespołu.",
                report.Findings.Count > 250 ? "Raport eksportuje tylko pierwsze 250 zdarzeń ze szczegółowymi dowodami." : "Raport obejmuje wszystkie zdarzenia ze szczegółowymi dowodami."
            ]).Distinct().ToArray());
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("Użycie: dotnet run -- <log.csv|log.jsonl|log.json> [--lpg] [--output <raport.json>]");
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var input = args[0];
            bool lpg = args.Contains("--lpg");
            string? output = null;
            int ix = Array.IndexOf(args, "--output");
            if (ix >= 0)
            {
                if (ix + 1 >= args.Length) throw new ArgumentException("Brak ścieżki po --output.");
                output = args[ix + 1];
            }
            if (args.Skip(1).Where(x => x.StartsWith("--")).Any(x => x is not "--lpg" and not "--output"))
                throw new ArgumentException("Nieznana opcja.");
            var log = Importer.Read(input);
            var result = LocalAgent.Run(log, lpg);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            if (output is null) Console.WriteLine(json);
            else
            {
                if (Path.GetFullPath(output) == Path.GetFullPath(input))
                    throw new ArgumentException("Nie wolno nadpisać źródłowego logu.");
                // Plik raportu tworzony tylko na jawne żądanie użytkownika.
                using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(json);
                Console.WriteLine("Utworzono raport: " + output);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Agent nie wykonał analizy: " + ex.Message);
            return 1;
        }
    }
}