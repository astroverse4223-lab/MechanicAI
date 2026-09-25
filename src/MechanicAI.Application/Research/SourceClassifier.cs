using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Research;

public sealed record SourceClassification(SourceType Type, string? Publisher, string Label, double Weight);

/// <summary>
/// Classifies web sources by authority. A forum post is never treated as equivalent to
/// factory service information: each class carries a ranking weight and a visible label.
/// </summary>
public static class SourceClassifier
{
    private static readonly (string Domain, SourceType Type, string Publisher)[] Known =
    [
        // Manufacturer (OEM) sources
        ("ford.com", SourceType.Manufacturer, "Ford"), ("fordservicecontent.com", SourceType.Manufacturer, "Ford"),
        ("motorcraftservice.com", SourceType.Manufacturer, "Ford Motorcraft Service"), ("fordtechservice.dealerconnection.com", SourceType.Manufacturer, "Ford"),
        ("gm.com", SourceType.Manufacturer, "General Motors"), ("chevrolet.com", SourceType.Manufacturer, "Chevrolet"), ("gmc.com", SourceType.Manufacturer, "GMC"),
        ("cadillac.com", SourceType.Manufacturer, "Cadillac"), ("buick.com", SourceType.Manufacturer, "Buick"), ("acdelcotds.com", SourceType.Manufacturer, "GM ACDelco TDS"),
        ("gmparts.com", SourceType.Manufacturer, "GM Parts"), ("toyota.com", SourceType.Manufacturer, "Toyota"), ("techinfo.toyota.com", SourceType.Manufacturer, "Toyota TIS"),
        ("lexus.com", SourceType.Manufacturer, "Lexus"), ("honda.com", SourceType.Manufacturer, "Honda"), ("techinfo.honda.com", SourceType.Manufacturer, "Honda Service Express"),
        ("acura.com", SourceType.Manufacturer, "Acura"), ("nissanusa.com", SourceType.Manufacturer, "Nissan"), ("nissan-techinfo.com", SourceType.Manufacturer, "Nissan TechInfo"),
        ("infinitiusa.com", SourceType.Manufacturer, "Infiniti"), ("hyundaiusa.com", SourceType.Manufacturer, "Hyundai"), ("hyundaitechinfo.com", SourceType.Manufacturer, "Hyundai Tech Info"),
        ("kia.com", SourceType.Manufacturer, "Kia"), ("kiatechinfo.com", SourceType.Manufacturer, "Kia Tech Info"), ("subaru.com", SourceType.Manufacturer, "Subaru"),
        ("techinfo.subaru.com", SourceType.Manufacturer, "Subaru STIS"), ("mazdausa.com", SourceType.Manufacturer, "Mazda"), ("mazdaserviceinfo.com", SourceType.Manufacturer, "Mazda Service Info"),
        ("vw.com", SourceType.Manufacturer, "Volkswagen"), ("erwin.vw.com", SourceType.Manufacturer, "Volkswagen erWin"), ("audiusa.com", SourceType.Manufacturer, "Audi"),
        ("bmwusa.com", SourceType.Manufacturer, "BMW"), ("mbusa.com", SourceType.Manufacturer, "Mercedes-Benz"), ("mercedes-benz.com", SourceType.Manufacturer, "Mercedes-Benz"),
        ("stellantis.com", SourceType.Manufacturer, "Stellantis"), ("mopar.com", SourceType.Manufacturer, "Mopar"), ("techauthority.com", SourceType.Manufacturer, "Stellantis TechAuthority"),
        ("jeep.com", SourceType.Manufacturer, "Jeep"), ("dodge.com", SourceType.Manufacturer, "Dodge"), ("ramtrucks.com", SourceType.Manufacturer, "Ram"),
        ("chrysler.com", SourceType.Manufacturer, "Chrysler"), ("mitsubishicars.com", SourceType.Manufacturer, "Mitsubishi"), ("volvocars.com", SourceType.Manufacturer, "Volvo"),
        ("tesla.com", SourceType.Manufacturer, "Tesla"), ("porsche.com", SourceType.Manufacturer, "Porsche"), ("genesis.com", SourceType.Manufacturer, "Genesis"),
        ("landroverusa.com", SourceType.Manufacturer, "Land Rover"), ("topix.jaguar.jlrext.com", SourceType.Manufacturer, "JLR TOPIx"),

        // Government sources
        ("nhtsa.gov", SourceType.Government, "NHTSA"), ("nhtsa.dot.gov", SourceType.Government, "NHTSA"), ("safercar.gov", SourceType.Government, "NHTSA"),
        ("epa.gov", SourceType.Government, "US EPA"), ("energy.gov", SourceType.Government, "US DOE"), ("fueleconomy.gov", SourceType.Government, "US DOE/EPA"),
        ("arb.ca.gov", SourceType.Government, "California Air Resources Board"), ("tc.canada.ca", SourceType.Government, "Transport Canada"),
        ("tc.gc.ca", SourceType.Government, "Transport Canada"), ("gov.uk", SourceType.Government, "UK Government"), ("europa.eu", SourceType.Government, "European Union"),

        // Professional technical databases, standards bodies, and component suppliers' technical info
        ("alldata.com", SourceType.ProfessionalDatabase, "ALLDATA"), ("alldatadiy.com", SourceType.ProfessionalDatabase, "ALLDATA DIY"),
        ("mitchell1.com", SourceType.ProfessionalDatabase, "Mitchell 1"), ("prodemand.com", SourceType.ProfessionalDatabase, "Mitchell 1 ProDemand"),
        ("identifix.com", SourceType.ProfessionalDatabase, "Identifix"), ("motor.com", SourceType.ProfessionalDatabase, "MOTOR Information Systems"),
        ("sae.org", SourceType.ProfessionalDatabase, "SAE International"), ("ase.com", SourceType.ProfessionalDatabase, "ASE"),
        ("nastf.org", SourceType.ProfessionalDatabase, "NASTF"), ("haynespro.com", SourceType.ProfessionalDatabase, "HaynesPro"),
        ("autodata-group.com", SourceType.ProfessionalDatabase, "Autodata"), ("picoauto.com", SourceType.ProfessionalDatabase, "Pico Technology"),
        ("boschautoparts.com", SourceType.ProfessionalDatabase, "Bosch"), ("densoautoparts.com", SourceType.ProfessionalDatabase, "DENSO"),
        ("ngksparkplugs.com", SourceType.ProfessionalDatabase, "NGK"), ("delphiautoparts.com", SourceType.ProfessionalDatabase, "Delphi"),
        ("smpcorp.com", SourceType.ProfessionalDatabase, "Standard Motor Products"), ("dorman.com", SourceType.ProfessionalDatabase, "Dorman"),
        ("aeswave.com", SourceType.ProfessionalDatabase, "AESwave"), ("iatn.net", SourceType.ProfessionalDatabase, "iATN"),

        // Reputable automotive technical publications
        ("underhoodservice.com", SourceType.TechnicalPublication, "Underhood Service"), ("brakeandfrontend.com", SourceType.TechnicalPublication, "Brake & Front End"),
        ("autoserviceprofessional.com", SourceType.TechnicalPublication, "Auto Service Professional"), ("vehicleservicepros.com", SourceType.TechnicalPublication, "Vehicle Service Pros"),
        ("searchautoparts.com", SourceType.TechnicalPublication, "Motor Age / Search Autoparts"), ("ratchetandwrench.com", SourceType.TechnicalPublication, "Ratchet+Wrench"),
        ("aa1car.com", SourceType.TechnicalPublication, "AA1Car"), ("motortrend.com", SourceType.TechnicalPublication, "MotorTrend"),
        ("caranddriver.com", SourceType.TechnicalPublication, "Car and Driver"), ("consumerreports.org", SourceType.TechnicalPublication, "Consumer Reports"),
        ("autonews.com", SourceType.TechnicalPublication, "Automotive News"), ("tomorrowstechnician.com", SourceType.TechnicalPublication, "Tomorrow's Technician"),

        // Established technical communities / reference
        ("mechanics.stackexchange.com", SourceType.TechnicalCommunity, "Motor Vehicle Maintenance & Repair Stack Exchange"),
        ("wikipedia.org", SourceType.TechnicalCommunity, "Wikipedia"), ("obd-codes.com", SourceType.TechnicalCommunity, "OBD-Codes"),
        ("repairpal.com", SourceType.TechnicalCommunity, "RepairPal"), ("yourmechanic.com", SourceType.TechnicalCommunity, "YourMechanic"),
        ("autozone.com", SourceType.TechnicalCommunity, "AutoZone"), ("carparts.com", SourceType.TechnicalCommunity, "CarParts.com"),
        ("oreillyauto.com", SourceType.TechnicalCommunity, "O'Reilly"), ("advanceautoparts.com", SourceType.TechnicalCommunity, "Advance Auto Parts"),

        // Forums / Q&A
        ("reddit.com", SourceType.Forum, "Reddit"), ("2carpros.com", SourceType.Forum, "2CarPros"), ("justanswer.com", SourceType.Forum, "JustAnswer"),
        ("quora.com", SourceType.Forum, "Quora"), ("cartalk.com", SourceType.Forum, "Car Talk community"), ("ih8mud.com", SourceType.Forum, "IH8MUD"),
        ("tacomaworld.com", SourceType.Forum, "TacomaWorld"), ("toyotanation.com", SourceType.Forum, "Toyota Nation"), ("bimmerfest.com", SourceType.Forum, "Bimmerfest"),
        ("mbworld.org", SourceType.Forum, "MBWorld"), ("vwvortex.com", SourceType.Forum, "VWVortex"), ("priuschat.com", SourceType.Forum, "PriusChat"),
        ("nasioc.com", SourceType.Forum, "NASIOC"), ("gm-trucks.com", SourceType.Forum, "GM-Trucks"), ("silveradosierra.com", SourceType.Forum, "SilveradoSierra"),
        ("f150forum.com", SourceType.Forum, "F150Forum"), ("ford-trucks.com", SourceType.Forum, "Ford-Trucks"), ("jeepforum.com", SourceType.Forum, "JeepForum"),
        ("wranglerforum.com", SourceType.Forum, "WranglerForum"), ("tundras.com", SourceType.Forum, "Tundras.com"), ("clublexus.com", SourceType.Forum, "ClubLexus"),
        ("dieselplace.com", SourceType.Forum, "DieselPlace"), ("thedieselstop.com", SourceType.Forum, "The Diesel Stop"), ("corvetteforum.com", SourceType.Forum, "CorvetteForum"),

        // Social media / video
        ("youtube.com", SourceType.SocialMedia, "YouTube"), ("youtu.be", SourceType.SocialMedia, "YouTube"), ("facebook.com", SourceType.SocialMedia, "Facebook"),
        ("instagram.com", SourceType.SocialMedia, "Instagram"), ("tiktok.com", SourceType.SocialMedia, "TikTok"), ("x.com", SourceType.SocialMedia, "X"),
        ("twitter.com", SourceType.SocialMedia, "X (Twitter)"), ("pinterest.com", SourceType.SocialMedia, "Pinterest"),
    ];

    private static readonly string[] ForumPathMarkers = ["/forum", "/forums/", "/threads/", "/thread/", "/topic/", "/showthread", "/viewtopic", "/community/", "/discussion/", "/t/"];

    public static SourceClassification Classify(string url, IReadOnlyCollection<string>? preferredDomains = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Make(SourceType.Unknown, null, preferred: false);
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var preferred = preferredDomains?.Any(d => HostMatches(host, d.Trim().ToLowerInvariant())) == true;

        foreach (var (domain, type, publisher) in Known.OrderByDescending(k => k.Domain.Length))
        {
            if (HostMatches(host, domain)) return Make(type, publisher, preferred);
        }

        if (host.EndsWith(".gov", StringComparison.Ordinal) || host.EndsWith(".gov.au", StringComparison.Ordinal) ||
            host.EndsWith(".gc.ca", StringComparison.Ordinal) || host.EndsWith(".mil", StringComparison.Ordinal))
        {
            return Make(SourceType.Government, host, preferred);
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (host.Contains("forum", StringComparison.Ordinal) || host.StartsWith("forums.", StringComparison.Ordinal) ||
            ForumPathMarkers.Any(m => path.Contains(m, StringComparison.Ordinal)))
        {
            return Make(SourceType.Forum, host, preferred);
        }

        return Make(SourceType.Unknown, host, preferred);
    }

    public static string Label(SourceType type) => type switch
    {
        SourceType.Manufacturer => "Manufacturer Source",
        SourceType.Government => "Government Source",
        SourceType.ProfessionalDatabase => "Professional Technical Source",
        SourceType.TechnicalPublication => "Technical Publication",
        SourceType.TechnicalCommunity => "Community Source",
        SourceType.Forum => "Forum Discussion",
        SourceType.SocialMedia => "Social Media",
        SourceType.PrivateDocument => "Your Document",
        SourceType.BuiltInReference => "Built-in Reference",
        _ => "General Web Source",
    };

    /// <summary>Ranking weight by authority (manufacturer highest, social media lowest).</summary>
    public static double Weight(SourceType type) => type switch
    {
        SourceType.Manufacturer => 1.0,
        SourceType.Government => 0.95,
        SourceType.ProfessionalDatabase => 0.9,
        SourceType.PrivateDocument => 0.9,
        SourceType.BuiltInReference => 0.85,
        SourceType.TechnicalPublication => 0.8,
        SourceType.TechnicalCommunity => 0.62,
        SourceType.Unknown => 0.55,
        SourceType.Forum => 0.42,
        SourceType.SocialMedia => 0.3,
        _ => 0.5,
    };

    private static SourceClassification Make(SourceType type, string? publisher, bool preferred) =>
        new(type, publisher, Label(type), Math.Min(1.0, Weight(type) + (preferred ? 0.15 : 0)));

    private static bool HostMatches(string host, string domain) =>
        domain.Length > 0 && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
}
