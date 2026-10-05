using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AutoKADN.Proyectos.Services;

/// <summary>
/// Rellena la cabecera del formato de legalización (XX X PULG.xlsx) editando directamente el XML de la hoja
/// "Formato de legalización.". No pasa por una librería de Excel: así se conservan intactos los estilos, las
/// listas desplegables, las hojas ocultas y todo lo demás de la plantilla, y AutoKADN puede seguir
/// generando los Excel por UC a partir de este archivo.
/// </summary>
public static class SoporteWriter
{
    private const string SheetName = "Formato de legalización.";
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Celdas de la cabecera. Los valores son texto o número (long/int/double).</summary>
    public static void FillHeader(string xlsxPath, IReadOnlyDictionary<string, object> cells)
    {
        using ZipArchive zip = ZipFile.Open(xlsxPath, ZipArchiveMode.Update);

        string sheetPath = FindSheetPath(zip);
        XDocument sheet = ReadXml(zip, sheetPath);
        XElement sheetData = sheet.Root!.Element(Main + "sheetData") ?? throw new InvalidDataException("La hoja no tiene datos.");

        foreach (KeyValuePair<string, object> pair in cells) SetCell(sheetData, pair.Key, pair.Value);

        // La plantilla viene protegida; la copia de trabajo debe poder editarse.
        sheet.Root.Element(Main + "sheetProtection")?.Remove();
        WriteXml(zip, sheetPath, sheet);

        // Que Excel recalcule al abrir, por si alguna fórmula depende de la cabecera.
        XDocument workbook = ReadXml(zip, "xl/workbook.xml");
        XElement? calc = workbook.Root!.Element(Main + "calcPr");
        if (calc is not null)
        {
            calc.SetAttributeValue("fullCalcOnLoad", "1");
            WriteXml(zip, "xl/workbook.xml", workbook);
        }
    }

    private static string FindSheetPath(ZipArchive zip)
    {
        XDocument workbook = ReadXml(zip, "xl/workbook.xml");
        XElement? sheet = workbook.Root!.Element(Main + "sheets")!.Elements(Main + "sheet")
            .FirstOrDefault(s => string.Equals((string?)s.Attribute("name"), SheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            // Sin el nombre esperado: la hoja activa del libro.
            int active = (int?)workbook.Root.Element(Main + "bookViews")?.Element(Main + "workbookView")?.Attribute("activeTab") ?? 0;
            sheet = workbook.Root.Element(Main + "sheets")!.Elements(Main + "sheet").ElementAtOrDefault(active)
                ?? throw new InvalidDataException("No se encontró la hoja del formato de legalización.");
        }

        string relId = (string?)sheet.Attribute(Rel + "id") ?? throw new InvalidDataException("La hoja no tiene relación.");
        XDocument rels = ReadXml(zip, "xl/_rels/workbook.xml.rels");
        string target = rels.Root!.Elements(PackageRel + "Relationship")
            .First(r => (string?)r.Attribute("Id") == relId).Attribute("Target")!.Value;
        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
    }

    private static void SetCell(XElement sheetData, string reference, object value)
    {
        int rowNumber = int.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture);
        XElement row = FindOrCreateRow(sheetData, rowNumber);
        XElement cell = FindOrCreateCell(row, reference);

        // Se conserva el estilo (atributo s) de la celda.
        cell.RemoveNodes();
        cell.Attribute("t")?.Remove();

        switch (value)
        {
            case string text:
                cell.SetAttributeValue("t", "inlineStr");
                var t = new XElement(Main + "t", text);
                if (text != text.Trim() || text.Contains("  ")) t.SetAttributeValue(XmlNs + "space", "preserve");
                cell.Add(new XElement(Main + "is", t));
                break;
            case int or long or double or decimal:
                cell.Add(new XElement(Main + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
                break;
            default:
                throw new ArgumentException("Tipo de valor no soportado: " + value.GetType().Name);
        }
    }

    private static XElement FindOrCreateRow(XElement sheetData, int rowNumber)
    {
        XElement? before = null;
        foreach (XElement row in sheetData.Elements(Main + "row"))
        {
            int current = (int)row.Attribute("r")!;
            if (current == rowNumber) return row;
            if (current > rowNumber) { before = row; break; }
        }
        var created = new XElement(Main + "row", new XAttribute("r", rowNumber));
        if (before is null) sheetData.Add(created); else before.AddBeforeSelf(created);
        return created;
    }

    private static XElement FindOrCreateCell(XElement row, string reference)
    {
        int column = ColumnIndex(reference);
        XElement? before = null;
        foreach (XElement cell in row.Elements(Main + "c"))
        {
            string current = (string)cell.Attribute("r")!;
            if (string.Equals(current, reference, StringComparison.OrdinalIgnoreCase)) return cell;
            if (ColumnIndex(current) > column) { before = cell; break; }
        }
        var created = new XElement(Main + "c", new XAttribute("r", reference));
        if (before is null) row.Add(created); else before.AddBeforeSelf(created);
        return created;
    }

    private static int ColumnIndex(string reference)
    {
        int index = 0;
        foreach (char c in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return index;
    }

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        ZipArchiveEntry entry = zip.GetEntry(path) ?? throw new FileNotFoundException("Falta " + path + " dentro del Excel.");
        using Stream stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void WriteXml(ZipArchive zip, string path, XDocument document)
    {
        zip.GetEntry(path)?.Delete();
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }
}
