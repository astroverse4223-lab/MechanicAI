namespace MechanicAI.Application.Search;

/// <summary>
/// Make/model vocabulary for parsing free text ("2017 Silverado 5.3"). This only maps
/// well-known model names to their manufacturer; vehicle details are always confirmed
/// by VIN decode or the technician, never inferred from this table.
/// </summary>
public static class VehicleLexicon
{
    /// <summary>Canonical make names keyed by lowercase alias.</summary>
    public static readonly IReadOnlyDictionary<string, string> MakeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["acura"] = "Acura", ["alfa romeo"] = "Alfa Romeo", ["alfa"] = "Alfa Romeo", ["audi"] = "Audi",
        ["bmw"] = "BMW", ["buick"] = "Buick", ["cadillac"] = "Cadillac", ["caddy"] = "Cadillac",
        ["chevrolet"] = "Chevrolet", ["chevy"] = "Chevrolet", ["chrysler"] = "Chrysler", ["dodge"] = "Dodge",
        ["fiat"] = "FIAT", ["ford"] = "Ford", ["genesis"] = "Genesis", ["gmc"] = "GMC", ["honda"] = "Honda",
        ["hyundai"] = "Hyundai", ["infiniti"] = "Infiniti", ["jaguar"] = "Jaguar", ["jeep"] = "Jeep",
        ["kia"] = "Kia", ["land rover"] = "Land Rover", ["range rover"] = "Land Rover", ["lexus"] = "Lexus",
        ["lincoln"] = "Lincoln", ["mazda"] = "Mazda", ["mercedes-benz"] = "Mercedes-Benz", ["mercedes"] = "Mercedes-Benz",
        ["benz"] = "Mercedes-Benz", ["mini"] = "MINI", ["mitsubishi"] = "Mitsubishi", ["nissan"] = "Nissan",
        ["porsche"] = "Porsche", ["ram"] = "Ram", ["subaru"] = "Subaru", ["tesla"] = "Tesla", ["toyota"] = "Toyota",
        ["volkswagen"] = "Volkswagen", ["vw"] = "Volkswagen", ["volvo"] = "Volvo", ["pontiac"] = "Pontiac",
        ["saturn"] = "Saturn", ["scion"] = "Scion", ["mercury"] = "Mercury", ["hummer"] = "Hummer",
        ["saab"] = "Saab", ["suzuki"] = "Suzuki", ["isuzu"] = "Isuzu", ["polestar"] = "Polestar",
        ["rivian"] = "Rivian", ["lucid"] = "Lucid", ["maserati"] = "Maserati",
    };

    /// <summary>Model name (lowercase, as typed) → (canonical model, make).</summary>
    public static readonly IReadOnlyDictionary<string, (string Model, string Make)> Models = BuildModels();

    private static Dictionary<string, (string, string)> BuildModels()
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Add(string make, params string[] models)
        {
            foreach (var m in models)
            {
                d[m.ToLowerInvariant()] = (m, make);
                var compact = m.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
                if (!compact.Equals(m, StringComparison.OrdinalIgnoreCase)) d.TryAdd(compact.ToLowerInvariant(), (m, make));
            }
        }

        Add("Chevrolet", "Silverado", "Silverado 1500", "Silverado 2500HD", "Silverado 3500HD", "Tahoe", "Suburban", "Colorado",
            "Malibu", "Impala", "Cruze", "Equinox", "Traverse", "Trax", "Blazer", "Camaro", "Corvette", "Sonic", "Spark",
            "Volt", "Bolt", "Avalanche", "TrailBlazer", "Express", "Cobalt", "HHR", "S-10");
        Add("GMC", "Sierra", "Sierra 1500", "Sierra 2500HD", "Yukon", "Yukon XL", "Canyon", "Acadia", "Terrain", "Envoy", "Savana");
        Add("Ford", "F-150", "F-250", "F-350", "F-450", "Ranger", "Maverick", "Explorer", "Expedition", "Escape", "Edge",
            "Bronco", "Bronco Sport", "Mustang", "Mustang Mach-E", "Fusion", "Focus", "Fiesta", "Taurus", "Flex",
            "Transit", "Transit Connect", "E-350", "EcoSport", "Crown Victoria", "Excursion");
        Add("Ram", "Ram 1500", "Ram 2500", "Ram 3500", "ProMaster", "ProMaster City");
        Add("Dodge", "Charger", "Challenger", "Durango", "Journey", "Grand Caravan", "Dart", "Dakota", "Avenger", "Caliber", "Nitro", "Hornet");
        Add("Jeep", "Wrangler", "Grand Cherokee", "Cherokee", "Compass", "Renegade", "Gladiator", "Liberty", "Patriot", "Commander", "Wagoneer", "Grand Wagoneer");
        Add("Chrysler", "Pacifica", "300", "Town & Country", "200", "Voyager", "Sebring", "PT Cruiser");
        Add("Toyota", "Camry", "Corolla", "RAV4", "Tacoma", "Tundra", "Highlander", "4Runner", "Prius", "Sienna", "Sequoia",
            "Avalon", "Yaris", "Venza", "C-HR", "Land Cruiser", "FJ Cruiser", "Corolla Cross", "Matrix", "Supra", "GR86", "bZ4X");
        Add("Honda", "Civic", "Accord", "CR-V", "Pilot", "Odyssey", "HR-V", "Ridgeline", "Fit", "Passport", "Insight", "Element", "Prologue");
        Add("Nissan", "Altima", "Sentra", "Rogue", "Pathfinder", "Frontier", "Titan", "Murano", "Maxima", "Versa", "Kicks",
            "Armada", "Leaf", "Juke", "Xterra", "Quest", "370Z", "Ariya");
        Add("Hyundai", "Elantra", "Sonata", "Tucson", "Santa Fe", "Palisade", "Kona", "Accent", "Venue", "Ioniq", "Ioniq 5", "Veloster", "Santa Cruz");
        Add("Kia", "Optima", "K5", "Sorento", "Sportage", "Soul", "Telluride", "Forte", "Rio", "Seltos", "Carnival", "Sedona", "Niro", "Stinger", "EV6");
        Add("Subaru", "Outback", "Forester", "Impreza", "Crosstrek", "Legacy", "Ascent", "WRX", "BRZ", "Solterra");
        Add("Volkswagen", "Jetta", "Passat", "Golf", "GTI", "Tiguan", "Atlas", "Beetle", "Taos", "ID.4");
        Add("Mazda", "Mazda3", "Mazda6", "CX-5", "CX-9", "CX-30", "CX-50", "CX-90", "MX-5 Miata", "Miata");
        Add("Lexus", "RX 350", "ES 350", "IS 250", "IS 350", "GX 460", "NX 200t", "NX 300", "LS 460", "RX 450h");
        Add("Acura", "MDX", "RDX", "TLX", "TL", "TSX", "ILX", "Integra");
        Add("Infiniti", "Q50", "QX60", "QX80", "G35", "G37", "FX35");
        Add("Buick", "Enclave", "Encore", "Envision", "LaCrosse", "Regal", "Verano");
        Add("Cadillac", "Escalade", "CTS", "ATS", "XT5", "XT4", "SRX", "CT6", "XTS");
        Add("Lincoln", "Navigator", "Aviator", "MKZ", "MKX", "Nautilus", "Corsair", "MKC", "Continental");
        Add("Tesla", "Model 3", "Model Y", "Model S", "Model X", "Cybertruck");
        Add("Mitsubishi", "Outlander", "Outlander Sport", "Mirage", "Eclipse Cross", "Lancer");
        Add("Volvo", "XC90", "XC60", "XC40", "S60", "V60");
        Add("BMW", "3 Series", "5 Series", "X3", "X5", "X1", "328i", "335i", "528i", "530i", "M3");
        Add("Mercedes-Benz", "C300", "E350", "GLC 300", "GLE 350", "Sprinter", "ML350");
        Add("Audi", "A4", "A6", "Q5", "Q7", "Q3", "A3");
        Add("MINI", "Cooper", "Countryman");
        return d;
    }

    /// <summary>Resolves the make for a Ram truck: Ram became its own brand for model year 2011.</summary>
    public static string ResolveRamMake(int? year) => year is < 2011 ? "Dodge" : "Ram";

    /// <summary>Engine family names that imply an engine description.</summary>
    public static readonly IReadOnlyDictionary<string, string> EngineFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ecoboost"] = "EcoBoost", ["hemi"] = "HEMI", ["duramax"] = "Duramax diesel", ["powerstroke"] = "Power Stroke diesel",
        ["power stroke"] = "Power Stroke diesel", ["cummins"] = "Cummins diesel", ["tdi"] = "TDI diesel", ["vortec"] = "Vortec",
        ["pentastar"] = "Pentastar", ["coyote"] = "Coyote", ["ecotec"] = "Ecotec", ["skyactiv"] = "Skyactiv",
        ["triton"] = "Triton", ["ecodiesel"] = "EcoDiesel", ["godzilla"] = "7.3L gas (Godzilla)",
    };
}
