using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoKADN.Proyectos.Services;

/// <summary>
/// Catálogo de roles (roles.xlsx): columna A "INTERVENTORES" con el formato "NOMBRE (código)" y
/// columna B "SUPERVISORES". El código es obligatorio en los formatos Excel y DWG; las carpetas de
/// interventor no llevan código.
/// </summary>
public sealed class RoleCatalog
{
    private static readonly Regex CodeSuffix = new(@"\s*\((\d+)\)\s*$");

    public static RoleCatalog Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    private readonly Dictionary<string, string> _interventorByKey;

    private RoleCatalog(IReadOnlyList<string> interventores, IReadOnlyList<string> supervisores)
    {
        Interventores = interventores;
        Supervisores = supervisores;
        _interventorByKey = new Dictionary<string, string>();
        foreach (string full in interventores) _interventorByKey[NameKey(CodeSuffix.Replace(full, string.Empty))] = full;
    }

    /// <summary>Entradas completas, por ejemplo "IVAN MARTINEZ (14236)".</summary>
    public IReadOnlyList<string> Interventores { get; }
    public IReadOnlyList<string> Supervisores { get; }

    public static RoleCatalog Load(string rolesPath)
    {
        var interventores = new List<string>();
        var supervisores = new List<string>();
        List<Dictionary<string, string>> rows = XlsxMini.ReadFirstSheet(rolesPath);
        bool header = true;
        foreach (Dictionary<string, string> row in rows)
        {
            row.TryGetValue("A", out string? a);
            row.TryGetValue("B", out string? b);
            if (header)
            {
                header = false;
                if (string.Equals(a, "INTERVENTORES", StringComparison.OrdinalIgnoreCase) || string.Equals(b, "SUPERVISORES", StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (!string.IsNullOrWhiteSpace(a)) interventores.Add(a.Trim());
            if (!string.IsNullOrWhiteSpace(b)) supervisores.Add(b.Trim());
        }
        return new RoleCatalog(interventores, supervisores);
    }

    /// <summary>Entrada completa (con código) que corresponde a una carpeta de interventor, o null si no está.</summary>
    public string? FindInterventor(string folderName)
    {
        string key = NameKey(folderName);
        if (_interventorByKey.TryGetValue(key, out string? exact)) return exact;
        foreach (KeyValuePair<string, string> pair in _interventorByKey)
            if (pair.Key.Contains(key, StringComparison.Ordinal) || key.Contains(pair.Key, StringComparison.Ordinal)) return pair.Value;
        return null;
    }

    public static string NameKey(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return Regex.Replace(builder.ToString().ToUpperInvariant(), @"\s+", " ").Trim();
    }
}
