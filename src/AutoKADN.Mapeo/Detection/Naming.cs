using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoKADN.Mapeo.Detection;

// Convierte los rótulos impresos en nombres cortos y estables ("NÚMERO DE ANILLOS" -> "numero_de_anillos").
public static class Naming
{
    public static string Slug(string text)
    {
        string s = text.Replace("Ø", " d ").Replace("ø", " d ").Replace("³", "3").Replace("²", "2")
            .Replace("½", " 1_2 ").Replace("¾", " 3_4 ").Replace("¼", " 1_4 ").Replace("⅜", " 3_8 ").Replace("⅝", " 5_8 ");
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : ' ');
        }
        string slug = Regex.Replace(sb.ToString().Trim(), @"\s+", "_");
        slug = Regex.Replace(slug, "_+", "_").Trim('_');
        return slug.Length > 48 ? slug[..48].TrimEnd('_') : slug;
    }

    public static string Join(IEnumerable<string> labels, string separator = ".") =>
        string.Join(separator, labels.Select(Slug).Where(s => s.Length > 0));

    // Si varios comparten el mismo id, los numera en el orden dado: nombre_1, nombre_2...
    public static void MakeUnique<T>(IList<T> items, Func<T, string> getId, Action<T, string> setId)
    {
        var groups = items.GroupBy(getId).Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            int n = 1;
            foreach (T item in group) setId(item, $"{getId(item)}_{n++}");
        }
        // Un numerado puede chocar con un id ya existente: se repite hasta que no queden iguales.
        var seen = new HashSet<string>();
        foreach (T item in items)
        {
            string id = getId(item);
            string candidate = id;
            int extra = 2;
            while (!seen.Add(candidate)) candidate = $"{id}_{extra++}";
            if (candidate != id) setId(item, candidate);
        }
    }

    // Errores frecuentes del OCR en los rótulos de estos formatos.
    private static readonly (string Pattern, string Replacement)[] OcrFixes =
    {
        (@"\bPRUEB S\b", "PRUEBAS"),
        (@"\bDIRECCI N\b", "DIRECCIÓN"),
        (@"\bPOLIVALVCJLAS\b", "POLIVALVULAS"),
        (@"\bPOLIVALVCJLAS\b", "POLIVALVULAS"),
    };

    public static string Clean(string text)
    {
        string clean = Regex.Replace(text.Trim(), @"\s+", " ");
        foreach (var (pattern, replacement) in OcrFixes) clean = Regex.Replace(clean, pattern, replacement);
        return clean;
    }
}
