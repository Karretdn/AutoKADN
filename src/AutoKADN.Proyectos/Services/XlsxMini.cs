using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

namespace AutoKADN.Proyectos.Services;

/// <summary>
/// Lector mínimo de .xlsx (solo lectura de la primera hoja) sin dependencias externas. Abre el archivo
/// con FileShare.ReadWrite para poder leerlo aunque esté abierto en Excel.
/// </summary>
public static class XlsxMini
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Filas de la primera hoja; cada fila es un diccionario columna ("A", "B"...) a texto.</summary>
    public static List<Dictionary<string, string>> ReadFirstSheet(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        List<string> shared = ReadSharedStrings(zip);
        ZipArchiveEntry? sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                          .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (sheet is null) return new List<Dictionary<string, string>>();

        XDocument document;
        using (Stream sheetStream = sheet.Open()) document = XDocument.Load(sheetStream);

        var rows = new List<Dictionary<string, string>>();
        foreach (XElement row in document.Descendants(Main + "row"))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement cell in row.Elements(Main + "c"))
            {
                string reference = (string?)cell.Attribute("r") ?? string.Empty;
                string column = new string(reference.TakeWhile(char.IsLetter).ToArray());
                string value = ReadCell(cell, shared);
                if (column.Length > 0 && value.Length > 0) values[column] = value;
            }
            rows.Add(values);
        }
        return rows;
    }

    private static string ReadCell(XElement cell, List<string> shared)
    {
        string type = (string?)cell.Attribute("t") ?? string.Empty;
        if (type == "inlineStr") return string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)).Trim();
        string raw = (string?)cell.Element(Main + "v") ?? string.Empty;
        if (type == "s" && int.TryParse(raw, out int index) && index >= 0 && index < shared.Count) return shared[index].Trim();
        return raw.Trim();
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return result;
        XDocument document;
        using (Stream stream = entry.Open()) document = XDocument.Load(stream);
        foreach (XElement item in document.Root!.Elements(Main + "si"))
            result.Add(string.Concat(item.Descendants(Main + "t").Select(t => t.Value)));
        return result;
    }
}
