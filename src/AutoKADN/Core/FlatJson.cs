using System.Text;
using System.Text.RegularExpressions;

namespace AutoKADN.Core;

/// <summary>
/// Lector de un objeto JSON plano con valores de texto (datos_proyecto.json). Sin librerías: sirve igual
/// en el plugin de 2022 (.NET Framework) y en el de 2027.
/// </summary>
public static class FlatJson
{
    private static readonly Regex Pair = new Regex("\"(?<k>[^\"\\\\]+)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"");

    public static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Pair.Matches(json ?? string.Empty))
            result[match.Groups["k"].Value] = Unescape(match.Groups["v"].Value);
        return result;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length) { builder.Append(c); continue; }
            char next = value[++i];
            switch (next)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'u' when i + 4 < value.Length && int.TryParse(value.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code):
                    builder.Append((char)code);
                    i += 4;
                    break;
                default: builder.Append(next); break; // \" \\ \/
            }
        }
        return builder.ToString();
    }
}
