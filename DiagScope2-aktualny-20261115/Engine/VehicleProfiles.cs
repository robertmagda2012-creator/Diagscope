using System.Text.Json;
using System.Globalization;

namespace DiagScope2;

public sealed record VehicleProfile(Vehicle Vehicle)
{
    public string Label
    {
        get
        {
            string capacity = Vehicle.Capacity.Trim();
            if (int.TryParse(capacity, NumberStyles.None, CultureInfo.InvariantCulture, out int cc) && cc > 0)
                capacity += " cm³";
            return string.Join(" · ", new[]
            {
                (Vehicle.Make + " " + Vehicle.Model).Trim(), capacity,
                ProductionLabel(Vehicle), Vehicle.Vin.Trim()
            }.Where(value => !string.IsNullOrWhiteSpace(value)).DefaultIfEmpty("Pojazd"));
        }
    }

    public static string ProductionLabel(Vehicle vehicle) =>
        vehicle.ProductionDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ??
        vehicle.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "";
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
            var matches = Read().Where(p => LogMetadata.ValidVin(p.Vehicle.Vin) == LogMetadata.ValidVin(vehicle.Vin)).ToArray();
            if (matches.All(p => p.Vehicle.ProfileId != vehicle.ProfileId) && matches.Length > 0)
                throw new InvalidOperationException("Ten VIN jest już przypisany do zapisanej karty. Otwórz ją z listy pojazdów.");
        }
        if (!Guid.TryParseExact(vehicle.ProfileId, "N", out _)) vehicle.ProfileId = Guid.NewGuid().ToString("N");
        Store.WriteAtomic(Path.Combine(DirectoryPath, vehicle.ProfileId + ".json"), JsonSerializer.Serialize(vehicle, Store.Options));
    }
}