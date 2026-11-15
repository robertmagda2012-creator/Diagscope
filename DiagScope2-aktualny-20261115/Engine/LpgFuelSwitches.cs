namespace DiagScope2;

internal sealed class LpgFuelSwitches
{
    // Wider gaps do not locate a switch reliably in this screen recording.
    const double MaxWindowSeconds = 5;
    (string Fuel, DateTimeOffset At)? lastConfirmed;
    (string Fuel, DateTimeOffset At)? candidate;
    DateTimeOffset? previousFrame;

    public void Reset()
    {
        lastConfirmed = null;
        candidate = null;
        previousFrame = null;
    }

    public Marker? Read(string fuel, DateTimeOffset time, DateTimeOffset origin)
    {
        if (previousFrame is { } previous &&
            (time <= previous || (time - previous).TotalSeconds > MaxWindowSeconds)) Reset();
        previousFrame = time;

        if (fuel.Length == 0) return null;

        if (lastConfirmed is not { } confirmed || fuel == confirmed.Fuel)
        {
            lastConfirmed = (fuel, time);
            candidate = null;
            return null;
        }

        if (candidate is not { } change || change.Fuel != fuel ||
            (time - change.At).TotalSeconds > MaxWindowSeconds)
        {
            candidate = (fuel, time);
            return null;
        }

        lastConfirmed = (fuel, time);
        candidate = null;

        double window = (change.At - confirmed.At).TotalSeconds;
        if (window <= 0 || window > MaxWindowSeconds) return null;
        return new Marker
        {
            IsAutomatic = true,
            Label = $"{confirmed.Fuel} → {fuel}",
            Time = (confirmed.At - origin).TotalSeconds + window / 2,
            Uncertainty = window / 2,
            Source = "Stan paliwa odczytany w Gas Setting; przedział między próbkami."
        };
    }

    public static string Normalize(string fuel) => fuel.Trim().ToLowerInvariant() switch
    {
        "gaz" or "lpg" => "LPG",
        "benzyna" or "pb" => "Benzyna",
        _ => ""
    };

    public static string ChannelName(string key) => key switch
    {
        "rpm" => "Obroty silnika",
        "load" => "Obciążenie",
        "map" => "Ciśnienie w kolektorze (MAP)",
        "pressure" => "Ciśnienie gazu",
        "reducer_temp" => "Temperatura reduktora",
        "gas_temp" => "Temperatura gazu",
        "ecu_temp" => "Temperatura sterownika LPG",
        "lambda1" => "Napięcie Lambda 1 (Gas Setting)",
        "lambda2" => "Napięcie Lambda 2 (Gas Setting)",
        "battery" => "Napięcie akumulatora",
        "gas_level" => "Napięcie czujnika poziomu gazu",
        _ when key.Length == 2 && key[1] is >= '1' and <= '6' && key[0] == 'B' =>
            $"Czas wtrysku benzyny {key}",
        _ when key.Length == 2 && key[1] is >= '1' and <= '6' && key[0] == 'G' =>
            $"Czas wtrysku gazu {key}",
        _ => key
    };
}