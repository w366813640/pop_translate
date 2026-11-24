using System.Collections.Frozen;
using System.Text.RegularExpressions;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Language;

public sealed partial class LocalLanguageDetector : ILanguageDetector
{
    private static readonly FrozenSet<char> TraditionalExclusiveChars = new HashSet<char>(
        "這體學國為個來點會機時經與實說關開門動發譯簡語偵測驗證書畫電網軟統資檔視設選鍵盤螢圖貼顯隱權啟錯誤訊號處應從對進還過當樣問頭見長無現於將業種義氣產變戰場軍隊邊際環節構計館習練專領觀結標準規範雜難決執維護優調試確認檢記錄儲讀寫閉刪詢篩報評議饋驚險註帳碼郵聯溝協團價貢獻響趨預務稅約條聲願競運營滿創鏈輸銷廣傳覽講訓課績畢職麼們讓給沒裡歡樂買賣錢銀飛車馬鳥魚龍華漢萬億兩幾歲鐘離歸聽醫藥師勝敗總級終續斷絕絲線織紙紅綠藍風陽陰陸隨雙雞雖")
        .ToFrozenSet();

    private static readonly FrozenSet<char> SimplifiedExclusiveChars = new HashSet<char>(
        "这体学国为个来点会机时经与实说关开门动发译简语侦测验证书画电网软统资档视设选键盘萤图贴显隐权启错误讯号处应从对进还过当样问头见长无现于将业种义气产变战场军队边际环节构计馆习练专领观结标准规范杂难决执维护优调试确认检记录储读写闭删询筛报评议馈惊险注帐码邮联沟协团价贡献响趋预务税约条声愿竞运营满创链输销广传览讲训课绩毕职么们让给没里欢乐买卖钱银飞车马鸟鱼龙华汉万亿两几岁钟离归听医药师胜败总级终续断绝丝线织纸红绿蓝风阳阴陆随双鸡虽")
        .ToFrozenSet();

    private static readonly FrozenSet<char> JapaneseExclusiveChars = new HashSet<char>(
        "々〆駅込働畑枠戻払訳読経続発円沢渋図観権様変実気楽対転伝広応価検証険験録錬鉄鉱銭収処拠択抜拡捜掲揺摂撃斎暁栄桜桟歓歩歯歴毎浄涙渇済満滝営焼犠猟獣畳県砕稲穂穏竜粋粛絵継総緑縁縄縦繊聴脳臓舗蔵薬蛍覚覧譲讃豊弐売専軽辺遅酔釈関闘陥雑霊頼顕駆騒髄髪黙斉齢峠辻匂凧凪雫躾樫栃笹糀鋲鰯鱈")
        .ToFrozenSet();

    private static readonly FrozenSet<char> ChineseOrdinalDeterminers = new HashSet<char>(
        "第本往历歷上下首每一二三四五六七八九十百千万萬零0123456789")
        .ToFrozenSet();

    private static readonly string[] JapaneseKanjiCompounds =
    [
        "割引", "株式会社", "有限会社", "合同会社", "株式", "削除", "受付", "仕入",
        "口座", "残高", "取引", "納期", "弁当", "日本語", "都道府県", "市町村",
        "丁目", "届出", "申込", "税込", "税抜", "支払", "手続", "振込",
    ];

    private static readonly FrozenSet<string> FrenchStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "le", "la", "les", "des", "une", "un", "du", "de", "est", "sont", "ceci", "cela", "cette",
        "dans", "pour", "avec", "sur", "pas", "que", "qui", "mais", "bonjour", "monde", "très",
        "être", "avoir", "nous", "vous", "tout", "plus", "traduction", "aux", "chez", "donc",
        "elle", "elles", "ils", "par", "sans", "sous", "entre", "aussi", "comme", "fait", "bien",
        "merci", "oui", "non", "peut", "même", "encore", "toujours", "jamais", "alors", "quand",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GermanStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "der", "die", "das", "und", "ist", "ein", "eine", "einen", "einem", "einer", "nicht",
        "mit", "für", "auf", "den", "dem", "des", "ich", "sie", "wir", "ihr", "von", "zu", "zum",
        "zur", "hallo", "welt", "aber", "auch", "oder", "werden", "haben", "sind", "über", "wie",
        "wenn", "noch", "nur", "schon", "sehr", "kann", "muss", "wird", "nach", "aus", "bei",
        "durch", "gegen", "ohne", "unter", "zwischen", "danke", "bitte", "übersetzung", "übersetzungstest",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SpanishStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "una", "uno", "unos", "unas", "es", "son", "esta", "esto",
        "este", "estos", "estas", "para", "por", "con", "del", "que", "como", "pero", "hola",
        "mundo", "todos", "todas", "muy", "más", "también", "prueba", "traducción", "gracias",
        "buenos", "sobre", "entre", "desde", "hasta", "donde", "cuando", "porque", "puede",
        "tiene", "hacer", "todo", "bien", "aquí", "ahora", "siempre", "nunca", "según", "sin",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ItalianStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "il", "lo", "gli", "della", "delle", "degli", "dello", "che", "non", "per", "con", "una",
        "uno", "sono", "questo", "questa", "questi", "queste", "ciao", "mondo", "anche", "come",
        "tutto", "tutti", "molto", "grazie", "traduzione", "essere", "nel", "nella", "nei", "nelle",
        "dalla", "dalle", "dal", "dai", "sul", "sulla", "tra", "fra", "dove", "quando", "perché",
        "più", "può", "sempre", "bene", "qui", "ora", "buongiorno",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PortugueseStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "não", "uma", "um", "uns", "umas", "com", "para", "por", "dos", "das", "isso", "isto",
        "esta", "este", "estas", "estes", "olá", "mundo", "muito", "mais", "também", "você",
        "vocês", "obrigado", "obrigada", "tradução", "está", "estão", "são", "como", "pelo",
        "pela", "pelos", "pelas", "teste", "tudo", "todos", "todas", "quando", "onde", "porque",
        "pode", "fazer", "aqui", "agora", "sempre", "nunca", "entre", "sobre", "sem", "mas",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EnglishStopwords = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "and", "is", "are", "was", "were", "this", "that", "these", "those", "with", "for",
        "from", "have", "has", "had", "not", "you", "your", "hello", "world", "test", "translation",
        "can", "will", "would", "should", "could", "about", "which", "there", "their", "what",
        "when", "where", "who", "why", "how", "into", "over", "after", "before", "under", "between",
        "they", "them", "been", "being", "does", "did", "some", "more", "very", "just", "only",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StrongFrenchMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "bonjour", "merci", "traduction",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StrongGermanMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "hallo", "danke", "bitte", "übersetzung", "übersetzungstest",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StrongSpanishMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "hola", "gracias", "traducción", "buenos",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StrongItalianMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "ciao", "grazie", "buongiorno", "traduzione",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> StrongPortugueseMarkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "olá", "obrigado", "obrigada", "tradução", "você", "vocês",
    }.ToFrozenSet(StringComparer.Ordinal);

    public LanguageDetectionResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LanguageDetectionResult(LanguageCode.Auto, 0.0, IsReliable: false);
        }

        var stripped = StripNonProseArtifacts(text);
        var result = DetectCore(stripped, forceUnreliable: false);
        if (!result.Language.IsAuto)
        {
            return result;
        }

        if (!string.Equals(stripped, text, StringComparison.Ordinal))
        {
            return DetectCore(text, forceUnreliable: true);
        }

        return result;
    }

    internal static string StripNonProseArtifacts(string text)
    {
        var cleaned = UrlRegex().Replace(text, " ");
        cleaned = EmailRegex().Replace(cleaned, " ");
        cleaned = FilePathRegex().Replace(cleaned, " ");
        return cleaned;
    }

    private static LanguageDetectionResult DetectCore(string text, bool forceUnreliable)
    {
        int cjk = 0;
        int hiragana = 0;
        int katakana = 0;
        int hangul = 0;
        int cyrillic = 0;
        int arabic = 0;
        int latin = 0;
        int tradHits = 0;
        int simpHits = 0;
        int jpHits = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (IsHiragana(ch))
            {
                hiragana++;
            }
            else if (IsKatakana(ch))
            {
                katakana++;
            }
            else if (IsHangul(ch))
            {
                hangul++;
            }
            else if (IsCjkIdeographOrIteration(ch))
            {
                cjk++;
                if (JapaneseExclusiveChars.Contains(ch))
                {
                    jpHits++;
                }
                else if (ch == '届' && (i == 0 || !ChineseOrdinalDeterminers.Contains(text[i - 1])))
                {
                    jpHits++;
                }
                else if (TraditionalExclusiveChars.Contains(ch))
                {
                    tradHits++;
                }
                else if (SimplifiedExclusiveChars.Contains(ch))
                {
                    simpHits++;
                }
            }
            else if (IsCyrillic(ch))
            {
                cyrillic++;
            }
            else if (IsArabic(ch))
            {
                arabic++;
            }
            else if (IsLatinLetter(ch))
            {
                latin++;
            }
        }

        var kana = hiragana + katakana;
        var total = kana + hangul + cjk + cyrillic + arabic + latin;
        if (total == 0)
        {
            return new LanguageDetectionResult(LanguageCode.Auto, 0.0, IsReliable: false);
        }

        if (cjk >= 2)
        {
            foreach (var compound in JapaneseKanjiCompounds)
            {
                if (text.Contains(compound, StringComparison.Ordinal))
                {
                    jpHits += 2;
                }
            }
        }

        var kanaRatio = (double)kana / total;
        if (kana >= 2 ||
            (kana > 0 && kanaRatio > 0.08) ||
            (hiragana >= 1 && cjk >= 1 && simpHits <= jpHits + 2))
        {
            var japaneseRatio = (double)(kana + cjk) / total;
            var confidence = Math.Clamp(0.65 + (0.34 * japaneseRatio), 0.5, 0.99);
            return new LanguageDetectionResult(LanguageCode.Japanese, confidence, IsReliable: !forceUnreliable);
        }

        var hangulRatio = (double)hangul / total;
        if (hangulRatio > 0.20)
        {
            var confidence = Math.Clamp(0.60 + (0.39 * hangulRatio), 0.5, 0.99);
            return new LanguageDetectionResult(LanguageCode.Korean, confidence, IsReliable: !forceUnreliable);
        }

        var cyrillicRatio = (double)cyrillic / total;
        if (cyrillicRatio > 0.30)
        {
            var confidence = Math.Clamp(0.60 + (0.39 * cyrillicRatio), 0.5, 0.99);
            return new LanguageDetectionResult(LanguageCode.Russian, confidence, IsReliable: !forceUnreliable);
        }

        var arabicRatio = (double)arabic / total;
        if (arabicRatio > 0.30)
        {
            var confidence = Math.Clamp(0.60 + (0.39 * arabicRatio), 0.5, 0.99);
            return new LanguageDetectionResult(LanguageCode.Arabic, confidence, IsReliable: !forceUnreliable);
        }

        var cjkRatio = (double)cjk / total;
        if (cjkRatio > 0.15)
        {
            if (jpHits >= 1 && jpHits * 12 >= cjk)
            {
                var jpConfidence = Math.Clamp(0.64 + (0.34 * cjkRatio), 0.55, 0.98);
                var jpReliable = !forceUnreliable && (cjk >= 2 || cjkRatio >= 0.5);
                return new LanguageDetectionResult(LanguageCode.Japanese, jpConfidence, jpReliable);
            }

            var language = tradHits > simpHits && tradHits >= 1
                ? LanguageCode.ChineseTraditional
                : LanguageCode.ChineseSimplified;

            var confidence = Math.Clamp(0.60 + (0.38 * cjkRatio) + (tradHits != simpHits ? 0.01 : 0.0), 0.5, 0.99);
            var isReliable = !forceUnreliable && (cjk >= 2 || cjkRatio >= 0.5);
            return new LanguageDetectionResult(language, confidence, isReliable);
        }

        if (latin > 0)
        {
            var latinResult = DetectLatinLanguage(text, latin);
            return forceUnreliable ? latinResult with { IsReliable = false } : latinResult;
        }

        return new LanguageDetectionResult(LanguageCode.Auto, 0.0, IsReliable: false);
    }

    private static LanguageDetectionResult DetectLatinLanguage(string text, int latinCount)
    {
        double fr = 0;
        double de = 0;
        double es = 0;
        double it = 0;
        double pt = 0;
        double en = 0;

        foreach (var rawChar in text)
        {
            var ch = char.ToLowerInvariant(rawChar);
            switch (ch)
            {
                case 'ß':
                    de += 3.0;
                    break;
                case 'ä' or 'ö':
                    de += 1.5;
                    break;
                case 'ü':
                    de += 1.2;
                    es += 0.3;
                    break;
                case 'ñ' or '¿' or '¡':
                    es += 3.0;
                    break;
                case 'ã' or 'õ':
                    pt += 3.0;
                    break;
                case 'œ' or 'æ':
                    fr += 2.5;
                    break;
                case 'ç':
                    fr += 1.2;
                    pt += 1.2;
                    break;
                case 'ê' or 'â' or 'ô' or 'û':
                    fr += 1.0;
                    pt += 0.9;
                    break;
                case 'è' or 'ù':
                    fr += 0.9;
                    it += 0.9;
                    break;
                case 'é':
                    fr += 0.7;
                    es += 0.6;
                    pt += 0.6;
                    it += 0.4;
                    break;
                case 'à':
                    fr += 0.7;
                    it += 0.7;
                    pt += 0.5;
                    break;
                case 'á' or 'í' or 'ó' or 'ú':
                    es += 0.7;
                    pt += 0.6;
                    break;
                case 'ì' or 'ò':
                    it += 1.2;
                    break;
            }
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("l'", StringComparison.Ordinal) ||
            lower.Contains("d'", StringComparison.Ordinal) ||
            lower.Contains("c'", StringComparison.Ordinal) ||
            lower.Contains("qu'", StringComparison.Ordinal))
        {
            fr += 1.5;
        }

        var wordCount = 0;
        foreach (var word in EnumerateLatinWords(lower))
        {
            wordCount++;
            if (FrenchStopwords.Contains(word)) fr += StrongFrenchMarkers.Contains(word) ? 2.2 : 1.2;
            if (GermanStopwords.Contains(word)) de += StrongGermanMarkers.Contains(word) ? 2.2 : 1.2;
            if (SpanishStopwords.Contains(word)) es += StrongSpanishMarkers.Contains(word) ? 2.2 : 1.2;
            if (ItalianStopwords.Contains(word)) it += StrongItalianMarkers.Contains(word) ? 2.2 : 1.2;
            if (PortugueseStopwords.Contains(word)) pt += StrongPortugueseMarkers.Contains(word) ? 2.2 : 1.2;
            if (EnglishStopwords.Contains(word)) en += 1.2;
        }

        var candidates = new (LanguageCode Code, double Score)[]
        {
            (LanguageCode.French, fr),
            (LanguageCode.German, de),
            (LanguageCode.Spanish, es),
            (LanguageCode.Italian, it),
            (LanguageCode.Portuguese, pt),
        };

        var bestNonEnglish = candidates[0];
        for (var i = 1; i < candidates.Length; i++)
        {
            if (candidates[i].Score > bestNonEnglish.Score)
            {
                bestNonEnglish = candidates[i];
            }
        }

        if (bestNonEnglish.Score >= 2.0 && bestNonEnglish.Score > en + 0.5)
        {
            var confidence = Math.Clamp(0.65 + (0.05 * Math.Min(bestNonEnglish.Score - en, 6.0)), 0.60, 0.96);
            return new LanguageDetectionResult(bestNonEnglish.Code, confidence, IsReliable: true);
        }

        var enConfidence = en > 0
            ? Math.Clamp(0.70 + (0.04 * Math.Min(en, 6.0)), 0.65, 0.96)
            : (wordCount >= 1 && latinCount >= 2 ? 0.65 : 0.55);

        return new LanguageDetectionResult(LanguageCode.English, enConfidence, IsReliable: latinCount >= 2);
    }

    private static IEnumerable<string> EnumerateLatinWords(string lowerText)
    {
        var start = -1;
        for (var i = 0; i < lowerText.Length; i++)
        {
            if (IsLatinLetter(lowerText[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                yield return lowerText[start..i];
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return lowerText[start..];
        }
    }

    private static bool IsHiragana(char ch) => ch is >= '\u3040' and <= '\u309F';

    private static bool IsKatakana(char ch) =>
        ch is (>= '\u30A0' and <= '\u30FF') or (>= '\u31F0' and <= '\u31FF') or (>= '\uFF66' and <= '\uFF9F');

    private static bool IsHangul(char ch) =>
        ch is (>= '\uAC00' and <= '\uD7AF') or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F');

    private static bool IsCjkIdeographOrIteration(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or '\u3005'
            or '\u3006'
            or '\u3007';

    private static bool IsCyrillic(char ch) =>
        ch is (>= '\u0400' and <= '\u04FF') or (>= '\u0500' and <= '\u052F');

    private static bool IsArabic(char ch) =>
        ch is (>= '\u0600' and <= '\u06FF') or (>= '\u0750' and <= '\u077F') or (>= '\u08A0' and <= '\u08FF');

    private static bool IsLatinLetter(char ch) =>
        ch is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '\u00C0' and <= '\u00D6')
            or (>= '\u00D8' and <= '\u00F6')
            or (>= '\u00F8' and <= '\u024F');

    [GeneratedRegex(
        @"(?:https?|ftp|file|ws|wss)://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+|www\.[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        @"(?:[A-Za-z]:[\\/]|\\\\[A-Za-z0-9._\-]+[\\/])[A-Za-z0-9._\-\\/]+|(?<![A-Za-z0-9:])(?:\.{1,2}/|/)(?:[A-Za-z0-9._\-]+/)+[A-Za-z0-9._\-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex FilePathRegex();
}
