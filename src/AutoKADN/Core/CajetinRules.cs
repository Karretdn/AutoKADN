using System.Text.RegularExpressions;

namespace AutoKADN.Core;

/// <summary>
/// Reglas del cajetín del plano, sin dependencias de AutoCAD (así se pueden probar con los textos reales):
/// cómo se reconoce un campo ("\pxql;MUNICIPIO: valor"), qué dato le corresponde y cuál de las tres
/// FECHA: del DETALLE es el gasificado, la prueba inicial y la prueba final.
/// </summary>
public static class CajetinRules
{
    public enum DateKind { Gasificado, PruebaInicial, PruebaFinal }

    private static readonly Regex FieldPattern = new Regex(
        @"^(?<prefix>(?:\\[^;]*;)*)(?<label>MUNICIPIO|OBRA|SECTOR|PROYECTO|INTERVENTOR|PEGADOR|TUBERIA|O\.T\.#|FECHA):[ \t]*(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Reconoce un campo del cajetín. El prefijo conserva el formato del MTEXT (\pxql; \A1; ...).</summary>
    public static bool TryParseField(string contents, out string prefix, out string label, out string value)
    {
        prefix = label = value = string.Empty;
        Match match = FieldPattern.Match(contents ?? string.Empty);
        if (!match.Success) return false;
        prefix = match.Groups["prefix"].Value;
        label = match.Groups["label"].Value;
        value = match.Groups["value"].Value.Trim();
        return true;
    }

    public static string FormatField(string prefix, string label, string value) => $"{prefix}{label}: {value}";

    /// <summary>Dato del proyecto para un campo que no es fecha; null si el campo no se rellena.</summary>
    public static string? ValueFor(string label, IReadOnlyDictionary<string, string> data)
    {
        switch (label.ToUpperInvariant())
        {
            case "MUNICIPIO": return data["municipio"];
            case "OBRA":
            case "SECTOR":
            case "PROYECTO": return data["proyecto"];
            case "INTERVENTOR": return data["interventor"];
            case "PEGADOR": return data["supervisor"];
            case "TUBERIA": return data["tuberia"];
            case "O.T.#": return data["orden"];
            default: return null;
        }
    }

    /// <summary>
    /// DETALLE: la FECHA más alta es el gasificado; de las dos de abajo, cada una va bajo su etiqueta
    /// "PRUEBA INICIAL" / "PRUEBA FINAL" (se elige el reparto que deja ambas más cerca de su etiqueta; sin
    /// etiquetas, izquierda = inicial). Devuelve null si no hay exactamente tres campos.
    /// </summary>
    public static DateKind[]? ClassifyDetalleDates(IReadOnlyList<(double X, double Y)> dates, (double X, double Y)? inicialLabel, (double X, double Y)? finalLabel)
    {
        if (dates.Count != 3) return null;
        int top = 0;
        for (int i = 1; i < dates.Count; i++) if (dates[i].Y > dates[top].Y) top = i;
        int a = top == 0 ? 1 : 0;
        int b = top == 2 ? 1 : 2;

        bool aIsInicial;
        if (inicialLabel.HasValue && finalLabel.HasValue)
        {
            double straight = Distance(dates[a], inicialLabel.Value) + Distance(dates[b], finalLabel.Value);
            double swapped = Distance(dates[a], finalLabel.Value) + Distance(dates[b], inicialLabel.Value);
            aIsInicial = straight <= swapped;
        }
        else aIsInicial = dates[a].X <= dates[b].X;

        var result = new DateKind[3];
        result[top] = DateKind.Gasificado;
        result[a] = aIsInicial ? DateKind.PruebaInicial : DateKind.PruebaFinal;
        result[b] = aIsInicial ? DateKind.PruebaFinal : DateKind.PruebaInicial;
        return result;
    }

    public static string DateValue(DateKind kind, IReadOnlyDictionary<string, string> data)
    {
        switch (kind)
        {
            case DateKind.PruebaInicial: return data["planoPruebaInicial"];
            case DateKind.PruebaFinal: return data["planoPruebaFinal"];
            default: return data["planoGasificado"];
        }
    }

    private static double Distance((double X, double Y) p, (double X, double Y) q)
    {
        double dx = p.X - q.X, dy = p.Y - q.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
