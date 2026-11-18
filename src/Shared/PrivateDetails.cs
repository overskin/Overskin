using System.Text;
using System.Text.RegularExpressions;

namespace Overskin.Shared;

/// <summary>What auto-hide looks for. Each can be switched off.</summary>
[Flags]
public enum PrivateKinds
{
    None = 0,
    Emails = 1,
    PhoneNumbers = 2,
    /// <summary>Card numbers, and with them the expiry date and security code.</summary>
    Cards = 4,
    IpAddresses = 8,
    Keys = 16,
    FolderPaths = 32,
    StreetAddresses = 64,
    /// <summary>Cities, states and postcodes.</summary>
    Places = 128,
    Countries = 256,

    Default = Emails | PhoneNumbers | Cards | IpAddresses | Keys | FolderPaths | StreetAddresses | Places,
    All = Default | Countries,
}

/// <summary>A word the text reader found, and where (picture pixels).</summary>
public readonly record struct TextWord(string Text, double X, double Y, double Width, double Height);

/// <summary>A found detail: the area to hide and what it is.</summary>
public readonly record struct PrivateDetail(double X, double Y, double Width, double Height, string Kind);

/// <summary>
/// Finds private details in text read off a picture: emails, phone numbers, card numbers (and
/// their expiry date and security code), IP addresses, keys, folder paths, street addresses,
/// cities, states, postcodes and countries. Shared by the Windows and Linux apps.
/// </summary>
public static class PrivateDetails
{
    /// <summary>The menu entries, in order.</summary>
    public static readonly (PrivateKinds Kind, string Label)[] Choices =
    {
        (PrivateKinds.Emails, "Email addresses"),
        (PrivateKinds.PhoneNumbers, "Phone numbers"),
        (PrivateKinds.Cards, "Card numbers, expiry dates and security codes"),
        (PrivateKinds.IpAddresses, "IP addresses (IPv4 and IPv6)"),
        (PrivateKinds.Keys, "Keys and tokens"),
        (PrivateKinds.FolderPaths, "Folder paths"),
        (PrivateKinds.StreetAddresses, "Street addresses"),
        (PrivateKinds.Places, "Cities, states and postcodes"),
        (PrivateKinds.Countries, "Countries"),
    };

    private const RegexOptions Fast = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions FastIgnoreCase = Fast | RegexOptions.IgnoreCase;

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", Fast);
    private static readonly Regex Card = new(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)", Fast);
    private static readonly Regex Phone = new(@"(?:\+\d{1,3}[ .\-]?)?(?:\(\d{2,4}\)[ .\-]?|\d{2,4}[ .\-])\d{3,4}[ .\-]?\d{3,4}\b", Fast);
    private static readonly Regex Ipv4 = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", Fast);
    // text readers sometimes put a space inside an address ("fe80::1 ff:fe23:4567:890a"), so a single space is allowed between groups
    private static readonly Regex Ipv6 = new(@"(?<![0-9A-Fa-f:])(?=[0-9A-Fa-f]|::)(?:[0-9A-Fa-f]{0,4} ?: ?){2,7}[0-9A-Fa-f]{0,4}(?:%\w+)?(?![0-9A-Fa-f:])", Fast);
    private static readonly Regex[] Keys =
    {
        new(@"\b(?:sk|pk|rk|ghp|gho|ghs|github_pat|glpat|xox[abpr]|AKIA|ASIA|AIza|ya29)[A-Za-z0-9_\-]{12,}\b", Fast),
        new(@"\b(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{32,}\b", Fast),
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}", Fast), // JSON web tokens
    };
    private static readonly Regex[] Paths =
    {
        new(@"(?<![\w])[A-Za-z]:\\(?:[^\\/:*?""<>|\s]+(?: [^\\/:*?""<>|\s]+)*\\?)*", Fast),
        new(@"\\\\[\w.\-]+\\[^\s""<>|]+", Fast),
        new(@"(?<![\w.])(?:~|/(?:home|Users|root|mnt|media|var|etc|opt|usr|srv|tmp|run|Volumes|private|data|storage|sdcard))(?:/[^\s/:*?""<>|]+)+/?", Fast),
    };

    private static readonly Regex Expiry = new(@"(?<!\d)(?:0[1-9]|1[0-2])\s?[/\-]\s?(?:20\d{2}|\d{2})(?!\d)", Fast);
    private static readonly Regex SecurityLabel = new(@"\b(?:CVV2?|CVC2?|CSC|CID|CVN|security\s*code|sec\.?\s*code|card\s*code|verification\s*(?:code|number)|Prüfnummer|Sicherheitscode)\b", FastIgnoreCase);
    private static readonly Regex ShortNumber = new(@"(?<![\d/\-.])\d{3,4}(?![\d/\-.])", Fast);

    private const string StreetTypes =
        "Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Lane|Ln|Drive|Dr|Court|Ct|Way|Place|Pl|Terrace|Ter|Circle|Cir|" +
        "Parkway|Pkwy|Highway|Hwy|Square|Sq|Trail|Trl|Crescent|Cres|Close|Grove|Gardens|Row|Mews|Walk|Hill|Esplanade|Loop|Alley|Plaza|Pike|Route|Rte";
    private static readonly Regex[] Streets =
    {
        // 1600 Pennsylvania Avenue NW, Apt 4B
        new(@"\b\d{1,6}[A-Za-z]?\s+(?:[NSEW]\.?\s+|North\s+|South\s+|East\s+|West\s+)?(?:[A-Z0-9][\w'.\-]*\s+){0,4}(?:" + StreetTypes + @")\b\.?(?:\s+(?:N|S|E|W|NE|NW|SE|SW)\b\.?)?(?:,?\s*(?:Apt|Apartment|Suite|Ste|Unit|Fl|Floor|Room|Rm|#)\.?\s*#?[\w\-]+)?", Fast),
        new(@"\bP\.?\s?O\.?\s+Box\s+\d+\b", FastIgnoreCase),
        // Hauptstraße 12, Rue de Rivoli 99, Via Roma 5 and the like
        new(@"\b[A-ZÄÖÜ][\wäöüß\-]*(?:straße|strasse|str\.|weg|gasse|platz|allee|ring|damm|ufer|chaussee|steig)\s+\d{1,4}\s?[a-z]?\b", Fast),
        new(@"\b(?:Rue|Avenue|Boulevard|Via|Viale|Calle|Avenida|Rua|Piazza|Plaza|Carrer)\s+(?:(?:de|du|des|la|le|del|della|di|da|do|dos|das)\s+)?[A-Z][\w'\-]+(?:\s+[A-Z][\w'\-]+){0,3},?\s+\d{1,4}\b", Fast),
        new(@"\b\d{1,4},?\s+(?:Rue|Avenue|Boulevard|Via|Calle|Avenida|Rua)\s+(?:(?:de|du|des|la|le|del|di|da|do)\s+)?[A-Z][\w'\-]+(?:\s+[A-Z][\w'\-]+){0,3}", Fast),
    };

    private const string StateCodes =
        "AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY|DC|PR|" +
        "ON|QC|BC|AB|MB|SK|NS|NB|NL|PE"; // and Canadian provinces
    private static readonly string[] StateNames =
    {
        "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware", "Florida", "Georgia",
        "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky", "Louisiana", "Maine", "Maryland", "Massachusetts",
        "Michigan", "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire", "New Jersey",
        "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island",
        "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah", "Vermont", "Virginia", "Washington", "West Virginia",
        "Wisconsin", "Wyoming", "Ontario", "Quebec", "British Columbia", "Alberta", "Manitoba", "Saskatchewan", "Nova Scotia",
        "Bavaria", "Bayern", "Berlin", "Hamburg", "Hessen", "Hesse", "Sachsen", "Saxony", "Nordrhein-Westfalen", "Baden-Württemberg",
        "Niedersachsen", "England", "Scotland", "Wales", "Northern Ireland", "New South Wales", "Victoria", "Queensland", "Tasmania",
    };
    private static readonly string[] Cities =
    {
        "New York City", "Los Angeles", "Chicago", "Houston", "Phoenix", "Philadelphia", "San Antonio", "San Diego", "Dallas",
        "San Jose", "Austin", "Jacksonville", "San Francisco", "Columbus", "Fort Worth", "Indianapolis", "Charlotte", "Seattle",
        "Denver", "Boston", "Nashville", "Detroit", "Portland", "Las Vegas", "Memphis", "Louisville", "Baltimore", "Milwaukee",
        "Albuquerque", "Tucson", "Fresno", "Sacramento", "Atlanta", "Miami", "Oakland", "Minneapolis", "Tulsa", "Cleveland",
        "New Orleans", "Honolulu", "Pittsburgh", "Cincinnati", "Orlando", "Tampa", "St. Louis", "Salt Lake City", "Toronto",
        "Montreal", "Vancouver", "Calgary", "Ottawa", "Edmonton", "Mexico City", "London", "Manchester", "Birmingham", "Liverpool",
        "Glasgow", "Edinburgh", "Dublin", "Paris", "Marseille", "Lyon", "Berlin", "Hamburg", "Munich", "München", "Cologne", "Köln",
        "Frankfurt", "Stuttgart", "Düsseldorf", "Dortmund", "Essen", "Leipzig", "Bremen", "Dresden", "Hannover", "Nuremberg",
        "Nürnberg", "Vienna", "Wien", "Zurich", "Zürich", "Geneva", "Genf", "Basel", "Bern", "Amsterdam", "Rotterdam", "Brussels",
        "Antwerp", "Copenhagen", "Stockholm", "Oslo", "Helsinki", "Madrid", "Barcelona", "Valencia", "Seville", "Lisbon", "Porto",
        "Rome", "Milan", "Naples", "Turin", "Florence", "Venice", "Athens", "Warsaw", "Krakow", "Prague", "Budapest", "Bucharest",
        "Istanbul", "Moscow", "Kyiv", "Tokyo", "Osaka", "Kyoto", "Seoul", "Beijing", "Shanghai", "Hong Kong", "Shenzhen", "Taipei",
        "Singapore", "Bangkok", "Jakarta", "Manila", "Kuala Lumpur", "Mumbai", "Delhi", "New Delhi", "Bangalore", "Bengaluru",
        "Chennai", "Kolkata", "Hyderabad", "Karachi", "Lahore", "Dubai", "Abu Dhabi", "Doha", "Riyadh", "Tel Aviv", "Jerusalem",
        "Cairo", "Lagos", "Nairobi", "Johannesburg", "Cape Town", "Sydney", "Melbourne", "Brisbane", "Perth", "Adelaide",
        "Auckland", "Wellington", "Sao Paulo", "São Paulo", "Rio de Janeiro", "Buenos Aires", "Lima", "Bogota", "Bogotá", "Santiago",
    };
    private static readonly string[] Countries =
    {
        "Afghanistan", "Albania", "Algeria", "Andorra", "Angola", "Argentina", "Armenia", "Australia", "Austria", "Azerbaijan",
        "Bahamas", "Bahrain", "Bangladesh", "Barbados", "Belarus", "Belgium", "Belize", "Benin", "Bhutan", "Bolivia",
        "Bosnia and Herzegovina", "Botswana", "Brazil", "Brunei", "Bulgaria", "Burkina Faso", "Burundi", "Cambodia", "Cameroon",
        "Canada", "Cape Verde", "Chad", "Chile", "China", "Colombia", "Comoros", "Congo", "Costa Rica", "Croatia", "Cuba",
        "Cyprus", "Czech Republic", "Czechia", "Denmark", "Djibouti", "Dominica", "Dominican Republic", "Ecuador", "Egypt",
        "El Salvador", "Eritrea", "Estonia", "Eswatini", "Ethiopia", "Fiji", "Finland", "France", "Gabon", "Gambia", "Georgia",
        "Germany", "Deutschland", "Ghana", "Greece", "Grenada", "Guatemala", "Guinea", "Guyana", "Haiti", "Honduras", "Hungary",
        "Iceland", "India", "Indonesia", "Iran", "Iraq", "Ireland", "Israel", "Italy", "Ivory Coast", "Jamaica", "Japan", "Jordan",
        "Kazakhstan", "Kenya", "Kiribati", "Kosovo", "Kuwait", "Kyrgyzstan", "Laos", "Latvia", "Lebanon", "Lesotho", "Liberia",
        "Libya", "Liechtenstein", "Lithuania", "Luxembourg", "Madagascar", "Malawi", "Malaysia", "Maldives", "Mali", "Malta",
        "Mauritania", "Mauritius", "Mexico", "Moldova", "Monaco", "Mongolia", "Montenegro", "Morocco", "Mozambique", "Myanmar",
        "Namibia", "Nepal", "Netherlands", "New Zealand", "Nicaragua", "Niger", "Nigeria", "North Korea", "North Macedonia",
        "Norway", "Oman", "Pakistan", "Palestine", "Panama", "Papua New Guinea", "Paraguay", "Peru", "Philippines", "Poland",
        "Portugal", "Qatar", "Romania", "Russia", "Rwanda", "Saudi Arabia", "Senegal", "Serbia", "Seychelles", "Sierra Leone",
        "Singapore", "Slovakia", "Slovenia", "Somalia", "South Africa", "South Korea", "South Sudan", "Spain", "Sri Lanka", "Sudan",
        "Suriname", "Sweden", "Switzerland", "Schweiz", "Österreich", "Syria", "Taiwan", "Tajikistan", "Tanzania", "Thailand",
        "Togo", "Tonga", "Trinidad and Tobago", "Tunisia", "Turkey", "Türkiye", "Turkmenistan", "Uganda", "Ukraine",
        "United Arab Emirates", "United Kingdom", "United States", "United States of America", "USA", "U.S.A.", "UK", "Uruguay",
        "Uzbekistan", "Vanuatu", "Venezuela", "Vietnam", "Yemen", "Zambia", "Zimbabwe",
    };

    private static readonly Regex CityState = new(@"\b[A-Z][a-zA-Z.'\-]+(?:\s[A-Z][a-zA-Z.'\-]+){0,3},\s*(?:" + StateCodes + @")(?:\s+\d{5}(?:-\d{4})?)?\b", Fast);
    private static readonly Regex StateZip = new(@"\b(?:" + StateCodes + @")\s+\d{5}(?:-\d{4})?\b", Fast);
    private static readonly Regex Postcodes = new(
        @"\b[A-Z]{1,2}\d[A-Z\d]?\s?\d[A-Z]{2}\b" +                              // UK
        @"|\b[A-Z]\d[A-Z]\s?\d[A-Z]\d\b" +                                      // Canada
        @"|(?<![\d.,])\d{4,5}\s+[A-ZÄÖÜ][a-zäöüß]+(?:[\s\-](?:am|an der|im|bei|sur|de)\s[A-Z][a-zäöüß]+)?\b", // 10115 Berlin, 8001 Zürich
        Fast);
    private static readonly Regex StatesAndCities = WordList(StateNames.Concat(Cities));
    private static readonly Regex CountryNames = WordList(Countries);

    private static Regex WordList(IEnumerable<string> words) =>
        new(@"(?<![\w])(?:" + string.Join("|", words.Distinct().OrderByDescending(w => w.Length).Select(Regex.Escape)) + @")(?![\w])", Fast);

    /// <summary>The details to hide in the recognised lines of text.</summary>
    public static List<PrivateDetail> Find(IReadOnlyList<IReadOnlyList<TextWord>> lines, PrivateKinds kinds = PrivateKinds.Default)
    {
        var found = new List<PrivateDetail>();
        var cards = new List<int>(); // lines that hold a card number
        var texts = new List<(string Text, string Digits, List<int> Starts)>();

        foreach (var words in lines)
        {
            var starts = new List<int>();
            var text = new StringBuilder();
            foreach (var w in words)
            {
                if (text.Length > 0) text.Append(' ');
                starts.Add(text.Length);
                text.Append(w.Text);
            }
            var line = text.ToString();
            texts.Add((line, ReadAsDigits(line), starts));
        }

        for (int li = 0; li < lines.Count; li++)
        {
            var words = lines[li];
            var (line, digits, starts) = texts[li];
            var taken = new bool[line.Length];

            void Take(Match m, string kind, string? source = null)
            {
                if (m.Length == 0 || Enumerable.Range(m.Index, m.Length).Any(i => taken[i])) return;
                for (int i = m.Index; i < m.Index + m.Length; i++) taken[i] = true;
                if (Area(words, starts, m.Index, m.Length) is { } area) found.Add(area with { Kind = kind });
            }

            bool On(PrivateKinds k) => (kinds & k) != 0;

            if (On(PrivateKinds.Emails)) foreach (Match m in Email.Matches(line)) Take(m, "email");
            if (On(PrivateKinds.FolderPaths)) foreach (var re in Paths) foreach (Match m in re.Matches(line)) if (m.Length > 3) Take(m, "folder path");
            if (On(PrivateKinds.Keys)) foreach (var re in Keys) foreach (Match m in re.Matches(line)) Take(m, "key");
            if (On(PrivateKinds.Cards))
                foreach (Match m in Card.Matches(digits))
                    if (Luhn(m.Value)) { Take(m, "card number"); cards.Add(li); }
            if (On(PrivateKinds.IpAddresses))
            {
                foreach (Match m in Ipv4.Matches(digits)) Take(m, "IP address");
                foreach (Match m in Ipv6.Matches(line)) if (LooksLikeIpv6(m.Value)) Take(m, "IP address");
            }
            if (On(PrivateKinds.StreetAddresses)) foreach (var re in Streets) foreach (Match m in re.Matches(line)) Take(m, "street address");
            if (On(PrivateKinds.Places))
            {
                foreach (Match m in CityState.Matches(line)) Take(m, "place");
                foreach (Match m in StateZip.Matches(line)) Take(m, "place");
                foreach (Match m in Postcodes.Matches(line)) Take(m, "place");
                foreach (Match m in StatesAndCities.Matches(line)) Take(m, "place");
            }
            if (On(PrivateKinds.Countries)) foreach (Match m in CountryNames.Matches(line)) Take(m, "country");
            if (On(PrivateKinds.PhoneNumbers))
                foreach (Match m in Phone.Matches(digits))
                    if (m.Value.Count(char.IsDigit) >= 7) Take(m, "phone number");
        }

        // the expiry date and security code only give a card away when its number is there too
        if (cards.Count > 0)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                var (line, digits, starts) = texts[li];
                var words = lines[li];
                foreach (Match m in Expiry.Matches(digits)) Add(li, m.Index, m.Length, "expiry date");

                if (!SecurityLabel.IsMatch(line)) continue;
                // the code sits after its label, or on the next line
                var label = SecurityLabel.Match(line);
                var after = ShortNumber.Matches(digits).Where(m => m.Index >= label.Index + label.Length).ToList();
                if (after.Count > 0) Add(li, after[0].Index, after[0].Length, "security code");
                else if (li + 1 < lines.Count && ShortNumber.Match(texts[li + 1].Digits) is { Success: true } next)
                    Add(li + 1, next.Index, next.Length, "security code");
            }
        }
        return found;

        void Add(int li, int index, int length, string kind)
        {
            if (Area(lines[li], texts[li].Starts, index, length) is not { } area) return;
            // skip it if something already covers this spot
            if (found.Any(f => Math.Abs(f.X - area.X) < 2 && Math.Abs(f.Y - area.Y) < 2 && Math.Abs(f.Width - area.Width) < 2)) return;
            found.Add(area with { Kind = kind });
        }
    }

    /// <summary>
    /// The line with the letters a text reader confuses with digits (l, I, |, O, S, B…) read as digits,
    /// inside words that are otherwise numbers, so "4lll 1111" is still a card. Same length as the line.
    /// </summary>
    public static string ReadAsDigits(string line)
    {
        var chars = line.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            if (char.IsWhiteSpace(chars[i])) { i++; continue; }
            int start = i;
            while (i < chars.Length && !char.IsWhiteSpace(chars[i])) i++;
            int digits = 0, confusable = 0, other = 0;
            for (int k = start; k < i; k++)
            {
                if (char.IsDigit(chars[k])) digits++;
                else if (Confusable(chars[k]) != chars[k]) confusable++;
                else if (chars[k] is not ('-' or '/' or '.' or ':' or '(' or ')' or '+')) other++;
            }
            if (digits > 0 && confusable > 0 && other == 0)
                for (int k = start; k < i; k++) chars[k] = Confusable(chars[k]);
        }
        return new string(chars);
    }

    private static char Confusable(char c) => c switch
    {
        'l' or 'I' or '|' or 'i' or '!' => '1',
        'O' or 'o' or 'D' or 'Q' => '0',
        'S' or 's' => '5',
        'B' => '8',
        'Z' or 'z' => '2',
        'G' => '6',
        _ => c,
    };

    private static bool LooksLikeIpv6(string s)
    {
        s = s.Replace(" ", "");
        int colons = s.Count(c => c == ':');
        if (colons < 2 || !s.Any(Uri.IsHexDigit)) return false;
        if (s.Contains(":::")) return false;
        // times such as 12:30:45 have no "::" and only two colons
        return s.Contains("::") ? s.Count(Uri.IsHexDigit) >= 2 : colons >= 5;
    }

    private static PrivateDetail? Area(IReadOnlyList<TextWord> words, List<int> starts, int index, int length)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int i = 0; i < words.Count; i++)
        {
            int s = starts[i], e = s + words[i].Text.Length;
            if (e <= index || s >= index + length) continue;
            var w = words[i];
            x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y);
            x1 = Math.Max(x1, w.X + w.Width); y1 = Math.Max(y1, w.Y + w.Height);
        }
        if (x1 < x0) return null;
        double pad = Math.Max(2, (y1 - y0) * 0.18);
        return new PrivateDetail(x0 - pad, y0 - pad, x1 - x0 + pad * 2, y1 - y0 + pad * 2, "");
    }

    private static bool Luhn(string text)
    {
        var d = text.Where(char.IsDigit).Select(c => c - '0').ToList();
        if (d.Count < 13 || d.Count > 19) return false;
        int sum = 0;
        for (int i = 0; i < d.Count; i++)
        {
            int v = d[d.Count - 1 - i];
            if (i % 2 == 1) { v *= 2; if (v > 9) v -= 9; }
            sum += v;
        }
        return sum % 10 == 0;
    }

    /// <summary>"2 emails, 1 card number and 3 places".</summary>
    public static string Describe(IEnumerable<string> kinds)
    {
        static string Plural(string k) => k.EndsWith("s") ? k + "es" : k.EndsWith("y") && !k.EndsWith("ey") ? k[..^1] + "ies" : k + "s";
        var parts = kinds.GroupBy(k => k).Select(g => g.Count() == 1 ? $"1 {g.Key}" : $"{g.Count()} {Plural(g.Key)}").ToList();
        return parts.Count <= 1 ? parts.FirstOrDefault() ?? "nothing" : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}
