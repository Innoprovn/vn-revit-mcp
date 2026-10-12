using System.Text;
using System.Text.RegularExpressions;

namespace InnoPro.Revit.Commands.ModelingCad.Common;

/// <summary>
/// Chuyển chữ tiếng Việt bảng mã cũ trong bản vẽ CAD (VNI-Windows: font VNI-*, TCVN3/ABC: font .Vn*) sang Unicode.
/// Nhận bảng mã theo font, không rõ font thì theo nội dung; chữ đã là Unicode tiếng Việt không bị đụng tới.
/// </summary>
public static class VnLegacy
{
    public enum Encoding { Unicode, Vni, Tcvn3 }

    /// <summary>Nhận bảng mã từ tên font/file font (TextStyle) hoặc mã \f trong MTEXT.</summary>
    public static Encoding Detect(string? font)
    {
        if (string.IsNullOrWhiteSpace(font)) return Encoding.Unicode;
        var f = font.Trim().ToLowerInvariant();
        if (f.Contains("vni")) return Encoding.Vni;
        if (f.StartsWith(".vn") || f.Contains("\\.vn") || f.Contains("/.vn") || f.StartsWith("vn") && f.Contains("time")) return Encoding.Tcvn3;
        return Encoding.Unicode;
    }

    /// <summary>
    /// Font không cho biết (vd kiểu chữ tên "VN") thì đoán theo nội dung: chỉ khi chữ chưa có ký tự Unicode tiếng Việt
    /// nào; VNI nhận qua dấu đứng ngay sau nguyên âm ("SAØN"), TCVN3 qua ít nhất hai ký tự đặc trưng.
    /// </summary>
    public static Encoding Detect(string? font, string text)
    {
        var byFont = Detect(font);
        if (byFont != Encoding.Unicode || text.Any(c => c >= 'Ā')) return byFont;
        if (VniPattern.IsMatch(text) || text.StartsWith('Ñ') || text.Contains(" Ñ")) return Encoding.Vni;
        return text.Count(c => Tcvn3From.IndexOf(c) >= 0) >= 2 ? Encoding.Tcvn3 : Encoding.Unicode;
    }

    private static readonly Regex VniPattern = new("[AEIOUYaeiouy][ùøûõïâáàåãäêéèúüëÙØÛÕÏÂÁÀÅÃÄÊÉÈÚÜË]", RegexOptions.Compiled);

    public static string Decode(string text, Encoding encoding) => encoding switch
    {
        Encoding.Vni => DecodeVni(text),
        Encoding.Tcvn3 => DecodeTcvn3(text),
        _ => text,
    };

    // VNI: nguyên âm gốc + ký tự dấu đứng sau; vài chữ là một ký tự riêng.
    private static readonly Dictionary<char, string> VniMarks = new()
    {
        ['ù'] = "́", ['Ù'] = "́", ['ø'] = "̀", ['Ø'] = "̀", ['û'] = "̉", ['Û'] = "̉",
        ['õ'] = "̃", ['Õ'] = "̃", ['ï'] = "̣", ['Ï'] = "̣",
        ['â'] = "̂", ['Â'] = "̂", ['á'] = "̂́", ['Á'] = "̂́", ['à'] = "̂̀", ['À'] = "̂̀",
        ['å'] = "̂̉", ['Å'] = "̂̉", ['ã'] = "̂̃", ['Ã'] = "̂̃", ['ä'] = "̣̂", ['Ä'] = "̣̂",
        ['ê'] = "̆", ['Ê'] = "̆", ['é'] = "̆́", ['É'] = "̆́", ['è'] = "̆̀", ['È'] = "̆̀",
        ['ú'] = "̆̉", ['Ú'] = "̆̉", ['ü'] = "̆̃", ['Ü'] = "̆̃", ['ë'] = "̣̆", ['Ë'] = "̣̆",
    };

    private static readonly Dictionary<char, string> VniLetters = new()
    {
        ['ô'] = "ơ", ['Ô'] = "Ơ", ['ö'] = "ư", ['Ö'] = "Ư", ['ñ'] = "đ", ['Ñ'] = "Đ",
        ['í'] = "í", ['Í'] = "Í", ['ì'] = "ì", ['Ì'] = "Ì", ['æ'] = "ỉ", ['Æ'] = "Ỉ", ['ó'] = "ĩ", ['Ó'] = "Ĩ", ['ò'] = "ị", ['Ò'] = "Ị",
        ['î'] = "ỵ", ['Î'] = "Ỵ",
    };

    private static string DecodeVni(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var last = sb.Length > 0 ? sb[^1] : '\0';
            if (VniMarks.TryGetValue(c, out var mark) && IsVowel(last)) sb.Append(mark);
            else if (VniLetters.TryGetValue(c, out var letter)) sb.Append(letter);
            else sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsVowel(char c) =>
        "aeiouyAEIOUYơƠưƯ".IndexOf(c) >= 0 || c is >= '̀' and <= '̣';

    // TCVN3 (ABC): mỗi chữ có dấu là một ký tự; chữ hoa có dấu dùng font .VnTimeH với cùng mã chữ thường.
    private const string Tcvn3From = "µ¸¶·¹¨»¾¼½Æ©ÇÊÈÉË®ÌÐÎÏÑªÒÕÓÔÖ×ÝØÜÞßãáâä«åèæçé¬êíëìîïóñòô­õøö÷ùúýûüþ¡¢§£¤¥¦";
    private const string Tcvn3To = "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵĂÂĐÊÔƠƯ";

    private static string DecodeTcvn3(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var i = Tcvn3From.IndexOf(c);
            sb.Append(i >= 0 ? Tcvn3To[i] : c);
        }
        return sb.ToString();
    }
}
