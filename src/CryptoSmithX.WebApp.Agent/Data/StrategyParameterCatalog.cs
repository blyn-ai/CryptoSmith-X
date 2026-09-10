using System.Globalization;
using System.Text.Json.Nodes;

namespace CryptoSmithX.WebApp.Agent.Data;

/// <summary>
/// The narrow, intentional Strategy Modeler write surface. Each definition
/// connects one user-facing field to one value inside a worker profile; it is
/// not a generic JSON editor and cannot reach protected worker configuration.
/// </summary>
public static class StrategyParameterCatalog
{
    public static readonly IReadOnlyList<StrategyParameterDefinition> All =
    [
        Decimal("markets", "Trading", "MaxActiveInstruments", "Stebimų kriptovaliutų ar akcijų skaičius", "kriptovaliutų ar akcijų", 1m, 100m, 1m, 0,
            "Kiek aktyviausių kriptovaliutų ar akcijų botas vienu metu tikrina ieškodamas progų.",
            "Daugiau kriptovaliutų ar akcijų = daugiau galimų signalų, bet ir daugiau triukšmo.",
            "Mažiau kriptovaliutų ar akcijų", "Daugiau galimybių",
            "Botas neseka visos biržos. Jis renkasi aktyviausias kriptovaliutas ar akcijas ir tik jose ieško progų.",
            "Ramesnis srautas, mažiau atsitiktinių signalų.",
            "Daugiau progų, bet ir daugiau triukšmo.",
            "Sąrašas perrenkamas kas ciklą pagal 24 val. apyvartą; iškritusiose kriptovaliutose ar akcijose naujos pozicijos nebeatidaromos, jau atviros tvarkomos iki galo."),
        Decimal("volume", "Trading", "StrongMoverMinDailyVolumeEur", "Minimali 24 val. apyvarta", "USD", 1_000m, 100_000_000m, 1_000m, 0,
            "Per ramias ir mažai prekiaujamas kriptovaliutas ar akcijas botas praleidžia.",
            "Didesnis minimumas palieka tik aktyviau prekiaujamas kriptovaliutas ar akcijas.",
            "Daugiau kriptovaliutų ar akcijų", "Likvidesnės kriptovaliutos ar akcijos",
            "Filtras, kuris išmeta kriptovaliutas ar akcijas, kuriose beveik nevyksta prekyba.",
            "Į atranką patenka daugiau, bet ramesnių kriptovaliutų ar akcijų.",
            "Lieka tik likvidžios kriptovaliutos ar akcijos, kuriose lengviau įeiti ir išeiti.",
            "Apyvarta imama iš rinkos duomenų agregato; kriptovaliutos ar akcijos be pakankamos momentų istorijos atmetamos atskira sąlyga."),
        Decimal("longScore", "Strategy", "MinimumLongScore", "LONG signalo stiprumas", "balas", 0.50m, 0.95m, 0.01m, 2,
            "Kiek stiprus turi būti signalas, kad botas atidarytų LONG. Balas — nuo 0 iki 1.",
            "Didesnis skaičius = mažiau LONG sandorių, bet griežtesnė atranka.",
            "Daugiau sandorių", "Griežtesnė atranka",
            "Botas tikrina kelis rinkos signalus ir sujungia juos į bendrą balą nuo 0 iki 1. Šis skaičius — riba, nuo kurios LONG laikomas vertu atidaryti.",
            "Sandorių bus daugiau, tačiau botas priims ir silpnesnius signalus.",
            "Sandorių bus mažiau, tačiau atranka bus griežtesnė.",
            "Balas — svertinė signalų suma. Riba lyginama su galutiniu balu po visų filtrų."),
        Decimal("shortScore", "Shorts", "MinShortScore", "SHORT signalo stiprumas", "balas", 0.50m, 0.95m, 0.01m, 2,
            "Kiek stiprus turi būti signalas, kad botas atidarytų SHORT. Balas — nuo 0 iki 1.",
            "Didesnis skaičius = mažiau SHORT sandorių, bet griežtesnė atranka.",
            "Daugiau sandorių", "Griežtesnė atranka",
            "Tas pats balas nuo 0 iki 1, tik SHORT pusei.",
            "Sandorių bus daugiau, tačiau botas priims ir silpnesnius signalus.",
            "Sandorių bus mažiau, tačiau atranka bus griežtesnė.",
            "SHORT pusėje papildomai tikrinama padėtis dienos diapazone ir atsitraukimas nuo vietinės viršūnės."),
        Decimal("spread", "Strategy", "MaxEntrySpreadPercent", "Maksimalus skirtumas tarp kainų", "%", 0.01m, 0.50m, 0.01m, 2,
            "Neleidžia įeiti, kai pirkimo ir pardavimo kainos per daug skiriasi — tada įėjimas brangesnis.",
            "Mažesnis limitas = pigesni įėjimai, bet daugiau praleistų progų.",
            "Pigesni įėjimai", "Daugiau galimybių",
            "Skirtumas tarp pirkimo ir pardavimo kainos yra kaina, kurią sumoki vien už įėjimą.",
            "Įėjimai pigesni, bet dalis progų bus praleista.",
            "Daugiau progų, bet kiekvienas įėjimas kainuoja brangiau.",
            "Tikrinama iš paskutinio orderbook momento prieš siunčiant orderį; viršijus ribą signalas atmetamas tame pačiame cikle."),
        Decimal("btcDrop", "Regime", "BtcCrashPct", "BTC apsaugos riba", "%", -10m, -0.50m, 0.10m, 1,
            "BTC nukritus daugiau nei ši riba per stebimą laikotarpį, nauji LONG gali būti blokuojami.",
            "Griežtesnė riba, arčiau nulio, sustabdo botą dažniau.",
            "Jautresnė apsauga", "Laisvesnė prekyba",
            "Kai Bitcoin staiga krenta, kartu krenta beveik visa rinka. Tada nauji LONG dažniausiai baigiasi blogai.",
            "Apsauga jautresnė — botas dažniau sustos.",
            "Apsauga laisvesnė — botas prekiaus ir neramesnėje rinkoje.",
            "Lyginama BTC kaina prieš N žvakių su dabartine. Peržengus ribą LONG įėjimai blokuojami, kol sąlyga nebegalioja; SHORT pusė šio filtro neturi.",
            StrategyValueTransform.Negate),
        Decimal("btcBars", "Regime", "BtcCrashLookback", "BTC stebimas laikotarpis", "žvakės", 1m, 24m, 1m, 0,
            "Per kiek paskutinių žvakių skaičiuojamas BTC kritimas.",
            "Prie 15 min. žvakės 4 žvakės = 1 val.",
            "Trumpesnis langas", "Ilgesnis langas",
            "Laikotarpis, per kurį matuojamas BTC kritimas. Jis skaičiuojamas iš strategijos žvakės ilgio.",
            "Reaguojama tik į labai staigius judesius.",
            "Įvertinamas ilgesnis, lėtesnis kritimas.",
            "Efektyvus laikas = žvakės ilgis × žvakių skaičius. Žvakės ilgis ateina iš strategijos profilio metaduomenų."),
        Decimal("stop", "Exits", "StopAtrMult", "Stop loss", "× ATR", 0.50m, 3m, 0.05m, 2,
            "Kiek vietos kainai leidžiama judėti prieš uždarant nuostolingą poziciją. Matuojama rinkos svyravimo dydžiu, ATR, ne fiksuotu procentu.",
            "Daugiau erdvės = rečiau išmuša maži svyravimai, bet galimas didesnis nuostolis.",
            "Greitesnis stop", "Daugiau erdvės kainai",
            "Stop loss atstumas matuojamas ATR — vidutiniu tos kriptovaliutos ar akcijos svyravimu. Ramioje rinkoje stop arčiau, neramioje — toliau.",
            "Nuostoliai mažesni, bet dažniau išmuš atsitiktinis svyravimas.",
            "Rečiau išmuš, bet vienas nesėkmingas sandoris kainuos daugiau.",
            "Galutinis atstumas ribojamas procentine grindų ir lubų reikšme, kad labai ramioje ar išsišokusioje rinkoje stop netaptų beprasmis."),
        Decimal("trail", "Exits", "TrailingActivationRMultiple", "Trailing apsaugos pradžia", "R", 0.30m, 3m, 0.10m, 1,
            "Nuo šio pelno botas pradeda saugoti jau uždirbtą rezultatą. Matuojama pradinės rizikos dalimis, R.",
            "1R yra atstumas nuo įėjimo iki stop loss. Kol riba nepasiekta, trailing dar neveikia.",
            "Anksčiau saugoti", "Duoti daugiau erdvės",
            "Trailing pradeda saugoti rezultatą, kai pelnas pasiekia nustatytą pradinės rizikos dalį. 1R yra atstumas nuo įėjimo iki stop loss.",
            "Rezultatas saugomas anksčiau, bet sandoris dažniau uždaromas nespėjęs išaugti.",
            "Sandoriui duodama daugiau erdvės, bet dalis pelno gali grįžti atgal.",
            "Pasiekus aktyvacijos ribą stop perkeliamas paskui kainą fiksuotu ATR atstumu; atgal jis niekada nejuda.",
            StrategyValueTransform.Identity, "Exits.AtrTrailingRegimeEnabled"),
        Decimal("maxHold", "Exits", "MaxHoldMinutes", "Maksimali sandorio trukmė", "min.", 15m, 1_440m, 15m, 0,
            "Kiek ilgiausiai botas gali laikyti poziciją.",
            "Pasibaigus laikui pozicija gali būti uždaryta net nepasiekus TP ar SL.",
            "Trumpesni sandoriai", "Ilgesni sandoriai",
            "Riba, po kurios pozicija uždaroma vien dėl laiko.",
            "Sandoriai trumpesni, pinigai greičiau grįžta naujoms progoms.",
            "Sandoriams duodama daugiau laiko išsipildyti.",
            "Laikas skaičiuojamas nuo įvykdyto įėjimo; uždarymas vyksta rinkos kaina artimiausiame cikle."),
        Decimal("cooldown", "ExecutionPolicy", "CooldownAfterStopLossSeconds", "Pauzė po stop loss", "min.", 0m, 480m, 15m, 0,
            "Kiek laiko botas palaukia po nuostolingo sandorio prieš bandydamas dar kartą.",
            "Padeda iš karto negrįžti į tą pačią blogą situaciją.",
            "Greitesnis grįžimas", "Ilgesnė pauzė",
            "Po nuostolingo sandorio toje pačioje kriptovaliutoje ar akcijoje botas trumpam sustoja.",
            "Botas grįžta greičiau — daugiau bandymų.",
            "Ilgesnė pauzė — mažiau pakartotinių klaidų toje pačioje situacijoje.",
            "Pauzė galioja tam pačiam instrumentui; kitos kriptovaliutos ar akcijos prekiaujamos toliau.",
            StrategyValueTransform.SecondsToMinutes),
    ];

    public static StrategyParameterDefinition Get(string id) =>
        All.SingleOrDefault(parameter => string.Equals(parameter.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown strategy parameter '{id}'.");

    public static bool IsEnabled(StrategyParameterDefinition definition, JsonObject values)
    {
        if (definition.EnabledWhenPath is null)
        {
            return true;
        }

        var parts = definition.EnabledWhenPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && values[parts[0]]?[parts[1]]?.GetValue<bool>() == true;
    }

    public static decimal Read(StrategyParameterDefinition definition, JsonObject values)
    {
        var raw = values[definition.Section]?[definition.Property]?.GetValue<decimal>()
            ?? throw new InvalidOperationException($"Strategy profile is missing '{definition.Section}.{definition.Property}'.");
        return definition.Transform.ToDisplay(raw);
    }

    public static void Write(StrategyParameterDefinition definition, JsonObject values, decimal displayValue)
    {
        if (!IsEnabled(definition, values))
        {
            throw new InvalidOperationException($"Strategy parameter '{definition.Id}' is not enabled by this profile.");
        }

        if (displayValue < definition.Minimum || displayValue > definition.Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayValue),
                displayValue,
                $"Strategy parameter '{definition.Id}' must be between {definition.Minimum} and {definition.Maximum}.");
        }

        if (definition.DecimalPlaces == 0 && displayValue != decimal.Truncate(displayValue))
        {
            throw new ArgumentException($"Strategy parameter '{definition.Id}' must be a whole number.", nameof(displayValue));
        }

        if (values[definition.Section] is not JsonObject section)
        {
            throw new InvalidOperationException($"Strategy profile is missing '{definition.Section}'.");
        }

        section[definition.Property] = definition.Transform.ToStored(displayValue);
    }

    private static StrategyParameterDefinition Decimal(
        string id,
        string section,
        string property,
        string label,
        string unit,
        decimal minimum,
        decimal maximum,
        decimal step,
        int decimalPlaces,
        string description,
        string example,
        string lowerCaption,
        string higherCaption,
        string detailIntro,
        string lowerImpact,
        string higherImpact,
        string technical,
        StrategyValueTransform transform = StrategyValueTransform.Identity,
        string? enabledWhenPath = null) =>
        new(id, section, property, label, unit, minimum, maximum, step, decimalPlaces, description, example, lowerCaption, higherCaption, detailIntro, lowerImpact, higherImpact, technical, transform, enabledWhenPath);
}

public sealed record StrategyParameterDefinition(
    string Id,
    string Section,
    string Property,
    string Label,
    string Unit,
    decimal Minimum,
    decimal Maximum,
    decimal Step,
    int DecimalPlaces,
    string Description,
    string Example,
    string LowerCaption,
    string HigherCaption,
    string DetailIntro,
    string LowerImpact,
    string HigherImpact,
    string Technical,
    StrategyValueTransform Transform,
    string? EnabledWhenPath)
{
    public string Format(decimal value) => value.ToString($"F{DecimalPlaces}", CultureInfo.InvariantCulture);
}

public enum StrategyValueTransform
{
    Identity,
    Negate,
    SecondsToMinutes,
}

public static class StrategyValueTransformExtensions
{
    public static decimal ToDisplay(this StrategyValueTransform transform, decimal stored) => transform switch
    {
        StrategyValueTransform.Identity => stored,
        StrategyValueTransform.Negate => -stored,
        StrategyValueTransform.SecondsToMinutes => stored / 60m,
        _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, null),
    };

    public static decimal ToStored(this StrategyValueTransform transform, decimal display) => transform switch
    {
        StrategyValueTransform.Identity => display,
        StrategyValueTransform.Negate => -display,
        StrategyValueTransform.SecondsToMinutes => display * 60m,
        _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, null),
    };
}
