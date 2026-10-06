using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoKADN.Core;

/// <summary>Un texto del plano con su caja en el papel (coordenadas del layout) y su ángulo en radianes.</summary>
public sealed class PlanText
{
    public PlanText(string contents, double minX, double minY, double maxX, double maxY, double rotation)
    {
        Contents = contents ?? string.Empty;
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        Rotation = rotation;
    }

    /// <summary>Tal como lo guarda el dibujo: MTEXT con códigos de formato (\P, {\f...;}), TEXT con %%c, %%u...</summary>
    public string Contents { get; }
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Rotation { get; }
}

/// <summary>
/// Cómo se saca el nombre del contratista del plano, sin dependencias de AutoCAD (así se prueba con textos armados
/// a mano). Está en el cajetín de los planos de detalles, justo arriba del rótulo "PLANO DE DETALLES", y solo
/// interesa hasta el primer punto: "EMPRESA DE EJEMPLO &amp; CIA. LTDA." → "EMPRESA DE EJEMPLO &amp; CIA.".
/// </summary>
public static class ContratistaRules
{
    // "PLANO DE DETALLES" (también "PLANO DE DETALLE"): el nombre del contratista queda justo encima.
    private const string AnchorPhrase = "PLANO DE DETALLE";
    // Un texto más lejos que esto (en veces la altura del texto) ya no está "justo arriba" del rótulo.
    private const double MaxGapFactor = 8.0;

    /// <summary>El nombre del contratista que está sobre "PLANO DE DETALLES"; vacío si no se encuentra.</summary>
    public static string FindContractor(IReadOnlyList<PlanText> texts)
    {
        for (int a = 0; a < texts.Count; a++)
        {
            PlanText anchor = texts[a];
            List<string> lines = Lines(anchor.Contents);
            int at = lines.FindIndex(IsAnchorLine);
            if (at < 0) continue;

            // El nombre va en el mismo texto, en la línea de arriba del rótulo.
            if (at > 0)
            {
                string inside = CompanyName(lines[at - 1]);
                if (inside.Length > 0) return inside;
            }

            // Si no, es otro texto: el más cercano por encima del rótulo y alineado con él (el cajetín puede
            // estar girado: "arriba" es la dirección del texto del rótulo).
            double cos = Math.Cos(anchor.Rotation), sin = Math.Sin(anchor.Rotation);
            double[] box = LocalBox(anchor, cos, sin);
            double tolerance = 0.3 * (box[3] - box[2]);
            string best = string.Empty;
            double bestGap = double.MaxValue;
            for (int c = 0; c < texts.Count; c++)
            {
                if (c == a) continue;
                List<string> candidateLines = Lines(texts[c].Contents);
                if (candidateLines.Count == 0 || candidateLines.Exists(IsAnchorLine)) continue;

                double[] other = LocalBox(texts[c], cos, sin);
                double gap = other[2] - box[3];                                   // > 0: está encima del rótulo
                if (gap < -tolerance) continue;                                   // está al lado o debajo
                if (Math.Min(other[1], box[1]) - Math.Max(other[0], box[0]) <= 0.0) continue;   // no se alinea con el rótulo
                if (gap > MaxGapFactor * Math.Max(box[3] - box[2], other[3] - other[2])) continue;   // queda lejos

                string name = CompanyName(candidateLines[candidateLines.Count - 1]);
                if (name.Length > 0 && gap < bestGap) { bestGap = gap; best = name; }
            }
            if (best.Length > 0) return best;
        }
        return string.Empty;
    }

    /// <summary>true si algún texto trae el rótulo «PLANO DE DETALLES» (para avisar qué falló cuando no se halla el nombre).</summary>
    public static bool HasAnchor(IReadOnlyList<PlanText> texts)
    {
        foreach (PlanText text in texts)
            if (Lines(text.Contents).Exists(IsAnchorLine)) return true;
        return false;
    }

    /// <summary>
    /// De un renglón de texto plano (ya sin formato, ver <see cref="PlainText"/>) al nombre de la empresa: solo hasta
    /// el primer punto y sin "LTDA". Vacío si el renglón no puede ser un nombre (un campo del cajetín, sin letras...).
    /// </summary>
    public static string CompanyName(string line)
    {
        string text = Regex.Replace(line ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length == 0) return string.Empty;

        string prefix, label, value;
        if (CajetinRules.TryParseField(text, out prefix, out label, out value)) return string.Empty;   // "MUNICIPIO: ..." no es el nombre

        int dot = text.IndexOf('.');
        if (dot >= 0) text = text.Substring(0, dot + 1);
        text = Regex.Replace(text, @"[\s,;.-]*\bLTDA\b\.?$", string.Empty, RegexOptions.IgnoreCase).Trim();

        int letters = 0;
        foreach (char c in text) if (char.IsLetter(c)) letters++;
        return letters >= 3 ? text : string.Empty;
    }

    /// <summary>
    /// El texto sin formato: renglones separados por «\n», sin los códigos del MTEXT (\P, \pxql;, \A1;, {\fArial|b1;...},
    /// \H2x;, \S1/2;, \U+00D8...) ni los de TEXT (%%c, %%d, %%p, %%u, %%o, %%nnn).
    /// </summary>
    public static string PlainText(string contents)
    {
        if (string.IsNullOrEmpty(contents)) return string.Empty;
        var sb = new StringBuilder(contents.Length);
        int i = 0, n = contents.Length;
        while (i < n)
        {
            char c = contents[i];
            if (c == '\\' && i + 1 < n)
            {
                char code = contents[i + 1];
                switch (code)
                {
                    case 'P': case 'N': sb.Append('\n'); i += 2; continue;
                    case '~': sb.Append(' '); i += 2; continue;
                    case '\\': case '{': case '}': sb.Append(code); i += 2; continue;
                    case 'L': case 'l': case 'O': case 'o': case 'K': case 'k': case 'X': i += 2; continue;
                    case 'U':
                    {
                        int unicode;
                        if (i + 6 < n && contents[i + 2] == '+'
                            && int.TryParse(contents.Substring(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out unicode))
                        {
                            sb.Append((char)unicode);
                            i += 7;
                        }
                        else i += 2;
                        continue;
                    }
                    case 'S':   // fracción apilada: \S1/2; o \S1^2;
                    {
                        int end = contents.IndexOf(';', i + 2);
                        if (end < 0) { i += 2; continue; }
                        sb.Append(contents.Substring(i + 2, end - i - 2).Replace('^', '/').Replace('#', '/'));
                        i = end + 1;
                        continue;
                    }
                    case 'A': case 'C': case 'c': case 'F': case 'f': case 'H': case 'Q': case 'T': case 'W': case 'p':
                    {
                        int end = contents.IndexOf(';', i + 2);
                        i = end < 0 ? i + 2 : end + 1;
                        continue;
                    }
                    default: sb.Append(code); i += 2; continue;
                }
            }
            if (c == '{' || c == '}') { i++; continue; }
            if (c == '%' && i + 2 < n && contents[i + 1] == '%')
            {
                char k = char.ToLowerInvariant(contents[i + 2]);
                if (k == 'c') { sb.Append('Ø'); i += 3; continue; }
                if (k == 'd') { sb.Append('°'); i += 3; continue; }
                if (k == 'p') { sb.Append('±'); i += 3; continue; }
                if (k == 'u' || k == 'o') { i += 3; continue; }
                if (k == '%') { sb.Append('%'); i += 3; continue; }
                int ascii;
                if (i + 4 < n && int.TryParse(contents.Substring(i + 2, 3), NumberStyles.None, CultureInfo.InvariantCulture, out ascii))
                {
                    sb.Append((char)ascii);
                    i += 5;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // Los renglones del texto, sin formato, con espacios simples y sin los vacíos.
    private static List<string> Lines(string contents)
    {
        var lines = new List<string>();
        foreach (string raw in PlainText(contents).Split('\n'))
        {
            string line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    private static bool IsAnchorLine(string line) => Normalize(line).StartsWith(AnchorPhrase, StringComparison.Ordinal);

    // Mayúsculas y sin tildes, para comparar "Plano de Detalles" con "PLANO DE DETALLES".
    private static string Normalize(string text)
    {
        string decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Regex.Replace(sb.ToString().ToUpperInvariant(), @"\s+", " ").Trim();
    }

    // Caja del texto en el marco del rótulo (u hacia la derecha del texto, v hacia arriba): { minU, maxU, minV, maxV }.
    private static double[] LocalBox(PlanText text, double cos, double sin)
    {
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        double[] xs = { text.MinX, text.MaxX, text.MinX, text.MaxX };
        double[] ys = { text.MinY, text.MinY, text.MaxY, text.MaxY };
        for (int k = 0; k < 4; k++)
        {
            double u = xs[k] * cos + ys[k] * sin;
            double v = -xs[k] * sin + ys[k] * cos;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        return new[] { minU, maxU, minV, maxV };
    }
}
