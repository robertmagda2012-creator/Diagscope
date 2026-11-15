using System.Text.Json;

namespace DiagScope2;

public sealed record VehicleProfile(Vehicle Vehicle)
{
    public string Label => string.Join(" · ", new[] { (Vehicle.Make + " " + Vehicle.Model).Trim(), Vehicle.Vin }
        .Where(v => v.Length > 0).DefaultIfEmpty("Pojazd"));
}

public static class VehicleProfiles
{
    static string DirectoryPath => Path.Combine(Store.Root, "VehicleProfiles");

    public static List<VehicleProfile> Read()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var profiles = new List<VehicleProfile>();
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            var vehicle = JsonSerializer.Deserialize<Vehicle>(File.ReadAllText(file));
            if (vehicle is not null) profiles.Add(new(vehicle));
        }
        return profiles.OrderBy(v => v.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Save(Vehicle vehicle)
    {
        if (vehicle.Vin.Length == 0 && vehicle.Make.Length == 0 && vehicle.Model.Length == 0) return;
        if (vehicle.Vin.Length > 0)
        {
            var sameVin = Read().FirstOrDefault(p => p.Vehicle.Vin == vehicle.Vin);
            if (sameVin is not null) vehicle.ProfileId = sameVin.Vehicle.ProfileId;
        }
        if (!Guid.TryParseExact(vehicle.ProfileId, "N", out _)) vehicle.ProfileId = Guid.NewGuid().ToString("N");
        Store.WriteAtomic(Path.Combine(DirectoryPath, vehicle.ProfileId + ".json"), JsonSerializer.Serialize(vehicle, Store.Options));
    }
}