using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoKADN.Proyectos.Services;

/// <summary>
/// Fechas escritas rápido: "25" (día del mes actual), "2509", "250926", "25092026", "25/9", "25/09/26",
/// "25-09-2026", "hoy" y "ayer". Los años de dos cifras se toman como 20xx.
/// </summary>
public static class DateParser
{
    public static readonly CultureInfo Culture = new("es-CO");

    public static bool TryParse(string? text, DateTime today, out DateTime? date)
    {
        date = null;
        string value = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return true; // vacío = sin fecha

        if (value is "hoy" or "h") { date = today.Date; return true; }
        if (value == "ayer") { date = today.Date.AddDays(-1); return true; }

        string[] groups = Regex.Split(value, @"\D+").Where(g => g.Length > 0).ToArray();
        if (groups.Length == 0 || Regex.IsMatch(value, @"[^\d\s/.\-]")) return false;

        int day, month = today.Month, year = today.Year;
        if (groups.Length == 1)
        {
            string g = groups[0];
            switch (g.Length)
            {
                case 1:
                case 2: day = int.Parse(g); break;
                case 4: day = int.Parse(g[..2]); month = int.Parse(g[2..]); break;
                case 6: day = int.Parse(g[..2]); month = int.Parse(g[2..4]); year = 2000 + int.Parse(g[4..]); break;
                case 8: day = int.Parse(g[..2]); month = int.Parse(g[2..4]); year = int.Parse(g[4..]); break;
                default: return false;
            }
        }
        else if (groups.Length is 2 or 3)
        {
            day = int.Parse(groups[0]);
            month = int.Parse(groups[1]);
            if (groups.Length == 3)
            {
                year = int.Parse(groups[2]);
                if (groups[2].Length <= 2) year += 2000;
            }
        }
        else return false;

        if (year < 2000 || year > 2100 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateTime(year, month, day);
        return true;
    }

    /// <summary>"viernes, 2 de octubre de 2026".</summary>
    public static string Describe(DateTime date) => Culture.TextInfo.ToLower(date.ToString("dddd, d 'de' MMMM 'de' yyyy", Culture));

    /// <summary>Formato del Excel de soportes: 25/09/2026.</summary>
    public static string ForExcel(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Formato del cajetín del plano: 25 - SEPTIEMBRE - 2026.</summary>
    public static string ForPlano(DateTime date) =>
        $"{date.Day:00} - {Culture.DateTimeFormat.GetMonthName(date.Month).ToUpper(Culture)} - {date.Year}";
}
