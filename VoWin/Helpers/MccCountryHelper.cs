namespace VoWin.Helpers
{
    public record CountryMeta(string Code, string Name, string Flag, string[] Mccs)
    {
        public string DisplayTitle => $"{Flag} {Name} ({Code})";
        public string MccSummary => $"MCC: {string.Join(", ", Mccs)}";
        public string SearchKey => $"{Name} {Code} {Flag}".ToLowerInvariant();
    }

    public static class MccCountryHelper
    {
        private static readonly Dictionary<string, CountryMeta> MccToCountry = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<CountryMeta> AllCountries = new();

        static MccCountryHelper()
        {
            Register("CN", "中国", "🇨🇳", new[] { "460" });
            Register("HK", "中国香港", "🇭🇰", new[] { "454" });
            Register("MO", "中国澳门", "🇲🇴", new[] { "455" });
            Register("TW", "中国台湾", "🇹🇼", new[] { "466" });
            Register("US", "美国", "🇺🇸", new[] { "310", "311", "312", "313", "314", "315", "316" });
            Register("GB", "英国", "🇬🇧", new[] { "234", "235" });
            Register("JP", "日本", "🇯🇵", new[] { "440", "441" });
            Register("KR", "韩国", "🇰🇷", new[] { "450" });
            Register("SG", "新加坡", "🇸🇬", new[] { "525" });
            Register("DE", "德国", "🇩🇪", new[] { "262" });
            Register("FR", "法国", "🇫🇷", new[] { "208" });
            Register("AU", "澳大利亚", "🇦🇺", new[] { "505" });
            Register("CA", "加拿大", "🇨🇦", new[] { "302" });
            Register("MY", "马来西亚", "🇲🇾", new[] { "502" });
            Register("TH", "泰国", "🇹🇭", new[] { "520" });
            Register("PH", "菲律宾", "🇵🇭", new[] { "515" });
            Register("VN", "越南", "🇻🇳", new[] { "452" });
            Register("ID", "印度尼西亚", "🇮🇩", new[] { "510" });
            Register("IN", "印度", "🇮🇳", new[] { "404", "405" });
            Register("NL", "荷兰", "🇳🇱", new[] { "204" });
            Register("RU", "俄罗斯", "🇷🇺", new[] { "250" });
            Register("IT", "意大利", "🇮🇹", new[] { "222" });
            Register("ES", "西班牙", "🇪🇸", new[] { "214" });
            Register("CH", "瑞士", "🇨🇭", new[] { "228" });
            Register("SE", "瑞典", "🇸🇪", new[] { "240" });
            Register("NO", "挪威", "🇳🇴", new[] { "242" });
            Register("FI", "芬兰", "🇫🇮", new[] { "244" });
            Register("DK", "丹麦", "🇩🇰", new[] { "238" });
            Register("IE", "爱尔兰", "🇮🇪", new[] { "272" });
            Register("BE", "比利时", "🇧🇪", new[] { "206" });
            Register("AT", "奥地利", "🇦🇹", new[] { "232" });
            Register("PL", "波兰", "🇵🇱", new[] { "260" });
            Register("NZ", "新西兰", "🇳🇿", new[] { "530" });
            Register("AE", "阿联酋", "🇦🇪", new[] { "424" });
            Register("SA", "沙特阿拉伯", "🇸🇦", new[] { "420" });
            Register("TR", "土耳其", "🇹🇷", new[] { "286" });
            Register("IL", "以色列", "🇮🇱", new[] { "425" });
            Register("BR", "巴西", "🇧🇷", new[] { "724" });
            Register("MX", "墨西哥", "🇲🇽", new[] { "334" });
            Register("ZA", "南非", "🇿🇦", new[] { "655" });
        }

        private static void Register(string code, string name, string flag, string[] mccs)
        {
            var meta = new CountryMeta(code, name, flag, mccs);
            AllCountries.Add(meta);
            foreach (var mcc in mccs)
            {
                MccToCountry[mcc] = meta;
                MccToCountry[mcc.TrimStart('0')] = meta;
            }
        }

        public static IReadOnlyList<CountryMeta> GetAllKnownCountries() => AllCountries;

        public static CountryMeta? FindByMcc(string? mcc)
        {
            if (string.IsNullOrWhiteSpace(mcc)) return null;
            var clean = mcc.Trim();
            if (MccToCountry.TryGetValue(clean, out var meta)) return meta;
            if (clean.Length >= 3 && MccToCountry.TryGetValue(clean[..3], out var meta3)) return meta3;
            return null;
        }

        public static CountryMeta? FindByCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return AllCountries.FirstOrDefault(c => string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
