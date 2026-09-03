using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AutoKADN.Tools.Excel;

public sealed class GenerarExcelTool
{
    private const string AppName = "AUTOKADN";
    private const string Materials = "RESUMEN_MATERIALES";
    private const string Uc = "RESUMEN_UC";

    public void Run()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (doc is null) return;
        Editor ed = doc.Editor;

        List<Row> rows = ReadRows(doc.Database);
        if (rows.Count == 0)
        {
            ed.WriteMessage("\nNo se encontraron resúmenes identificados.\n");
            return;
        }

        PromptSaveFileOptions opt = new PromptSaveFileOptions("\nGuardar Excel: ")
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            DialogCaption = "Generar Excel"
        };
        PromptFileNameResult save = ed.GetFileNameForSave(opt);
        if (save.Status != PromptStatus.OK) return;

        string path = save.StringResult.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? save.StringResult
            : save.StringResult + ".xlsx";

        try
        {
            WriteWorkbook(path, rows);
            ed.WriteMessage($"\nExcel generado correctamente: {path}\n");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError generando Excel: {ex.Message}\n");
        }
    }

    private static List<Row> ReadRows(Database db)
    {
        var result = new List<Row>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        foreach (ObjectId bid in bt)
        {
            if (tr.GetObject(bid, OpenMode.ForRead) is not BlockTableRecord space || !space.IsLayout)
                continue;

            string layout = ((Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead)).LayoutName;

            foreach (ObjectId oid in space)
            {
                if (tr.GetObject(oid, OpenMode.ForRead) is not DBText text)
                    continue;

                ResultBuffer? data = text.GetXDataForApplication(AppName);
                if (data is null) continue;

                TypedValue[] v = data.AsArray();
                if (v.Length < 4) continue;
                if (!string.Equals(v[0].Value?.ToString(), AppName, StringComparison.OrdinalIgnoreCase)) continue;

                string type = v[1].Value?.ToString() ?? string.Empty;
                if (!type.Equals(Materials, StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals(Uc, StringComparison.OrdinalIgnoreCase))
                    continue;

                string taggedLayout = v[2].Value?.ToString() ?? layout;
                string summaryId = v[3].Value?.ToString() ?? string.Empty;
                string rowId = v.Length >= 5 ? v[4].Value?.ToString() ?? string.Empty : string.Empty;
                string field = v.Length >= 6 ? v[5].Value?.ToString() ?? string.Empty : string.Empty;

                result.Add(new Row(type, taggedLayout, summaryId, rowId, field, text.TextString ?? string.Empty, text.Position));
            }
        }

        tr.Commit();
        return result;
    }

    private static void WriteWorkbook(string path, List<Row> rows)
    {
        if (File.Exists(path)) File.Delete(path);

        List<LogicalRow> materialRows = BuildLogicalRows(rows, Materials, 4);
        List<LogicalRow> ucRows = BuildLogicalRows(rows, Uc, 3);

        using FileStream fs = File.Create(path);
        using ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "xl/workbook.xml", Workbook);
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        Add(zip, "xl/styles.xml", Styles);

        // Los identificadores se conservan internamente para relacionar la información,
        // pero nunca se exportan como columnas visibles al usuario.
        Add(zip, "xl/worksheets/sheet1.xml", SheetXml(
            new[] { "TIPO", "LAYOUT", "ELEMENTOS" },
            rows.GroupBy(r => new { r.Type, r.Layout, r.SummaryId })
                .Select(g => new[] { g.Key.Type, g.Key.Layout, g.Count().ToString(CultureInfo.InvariantCulture) })));

        Add(zip, "xl/worksheets/sheet2.xml", SheetXml(
            new[] { "LAYOUT", "DESCRIPCION", "DIAMETRO", "UNIDAD", "CANTIDAD" },
            materialRows.Select(r => new[]
            {
                r.Layout,
                r.Get("DESCRIPCION"),
                r.Get("DIAMETRO"),
                r.Get("UNIDAD"),
                r.Get("CANTIDAD")
            })));

        Add(zip, "xl/worksheets/sheet3.xml", SheetXml(
            new[] { "LAYOUT", "DESCRIPCION", "UNIDAD", "CANTIDAD" },
            ucRows.Select(r => new[]
            {
                r.Layout,
                r.Get("DESCRIPCION"),
                r.Get("UNIDAD"),
                r.Get("CANTIDAD")
            })));

        Add(zip, "xl/worksheets/sheet4.xml", SheetXml(
            new[] { "TIPO", "CANTIDAD DE TABLAS", "CANTIDAD DE FILAS" },
            rows.GroupBy(r => r.Type)
                .Select(g => new[]
                {
                    g.Key,
                    g.Select(x => x.SummaryId).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture),
                    BuildLogicalRows(rows, g.Key, g.Key.Equals(Materials, StringComparison.OrdinalIgnoreCase) ? 4 : 3).Count.ToString(CultureInfo.InvariantCulture)
                })));
    }

    private static List<LogicalRow> BuildLogicalRows(List<Row> rows, string type, int expectedFields)
    {
        var selected = rows.Where(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<LogicalRow>();

        foreach (IGrouping<string, Row> summary in selected.GroupBy(r => r.SummaryId, StringComparer.OrdinalIgnoreCase))
        {
            // Los resúmenes nuevos llevan RowId. Ese identificador es la relación real entre
            // descripción, diámetro, unidad y cantidad de una misma fila.
            foreach (IGrouping<string, Row> rowGroup in summary.Where(r => !string.IsNullOrWhiteSpace(r.RowId))
                                                               .GroupBy(r => r.RowId, StringComparer.OrdinalIgnoreCase))
            {
                var logical = new LogicalRow(summary.Key, rowGroup.First().Layout, rowGroup.Key);
                foreach (Row row in rowGroup)
                    logical.Fields[row.Field.ToUpperInvariant()] = row.Text;
                result.Add(logical);
            }

            // Compatibilidad con resúmenes antiguos que solo tienen ID de tabla.
            List<Row> legacy = summary.Where(r => string.IsNullOrWhiteSpace(r.RowId))
                                     .OrderByDescending(r => r.Position.Y)
                                     .ThenBy(r => r.Position.X)
                                     .ToList();
            if (legacy.Count > 0)
            {
                double tolerance = 0.01;
                foreach (IGrouping<int, Row> yGroup in legacy.GroupBy(r => (int)Math.Round(r.Position.Y / tolerance)))
                {
                    var logical = new LogicalRow(summary.Key, yGroup.First().Layout, "LEGACY_" + yGroup.Key.ToString(CultureInfo.InvariantCulture));
                    foreach (Row row in yGroup.OrderBy(r => r.Position.X))
                    {
                        string field = GuessLegacyField(type, row.Position.X, yGroup.Min(x => x.Position.X), expectedFields);
                        if (!logical.Fields.ContainsKey(field)) logical.Fields[field] = row.Text;
                    }
                    result.Add(logical);
                }
            }
        }

        return result.OrderBy(r => r.Layout, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.SummaryId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.RowId, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    private static string GuessLegacyField(string type, double x, double minX, int expectedFields)
    {
        double delta = x - minX;
        if (type.Equals(Materials, StringComparison.OrdinalIgnoreCase))
        {
            if (delta < 35.0) return "DESCRIPCION";
            if (delta < 62.0) return "DIAMETRO";
            if (delta < 88.0) return "UNIDAD";
            return "CANTIDAD";
        }

        if (delta < 88.0) return "DESCRIPCION";
        if (delta < 103.0) return "UNIDAD";
        return "CANTIDAD";
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using StreamWriter sw = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        sw.Write(xml);
    }

    private static string SheetXml(string[] header, IEnumerable<string[]> data)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var all = new List<string[]> { header };
        all.AddRange(data);

        XElement sheetData = new XElement(ns + "sheetData");
        for (int r = 0; r < all.Count; r++)
        {
            XElement row = new XElement(ns + "row", new XAttribute("r", r + 1));
            for (int c = 0; c < all[r].Length; c++)
            {
                row.Add(new XElement(ns + "c",
                    new XAttribute("r", Cell(c, r)),
                    new XAttribute("t", "inlineStr"),
                    new XElement(ns + "is",
                        new XElement(ns + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            all[r][c] ?? string.Empty))));
            }
            sheetData.Add(row);
        }

        return new XElement(ns + "worksheet", sheetData).ToString(SaveOptions.DisableFormatting);
    }

    private static string Cell(int c, int r)
    {
        int n = c + 1;
        StringBuilder s = new StringBuilder();
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            s.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return s.ToString() + (r + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static readonly string ContentTypes = "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet3.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet4.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>";
    private static readonly string RootRels = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
    private static readonly string Workbook = "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"TABLAS_PROCESADAS\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"MATERIALES\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"UC\" sheetId=\"3\" r:id=\"rId3\"/><sheet name=\"RESUMEN\" sheetId=\"4\" r:id=\"rId4\"/></sheets></workbook>";
    private static readonly string WorkbookRels = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/><Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet4.xml\"/></Relationships>";
    private static readonly string Styles = "<?xml version=\"1.0\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"1\"><xf xfId=\"0\"/></cellXfs></styleSheet>";

    private readonly record struct Row(string Type, string Layout, string SummaryId, string RowId, string Field, string Text, Autodesk.AutoCAD.Geometry.Point3d Position);

    private sealed class LogicalRow
    {
        public string SummaryId { get; }
        public string Layout { get; }
        public string RowId { get; }
        public Dictionary<string, string> Fields { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public LogicalRow(string summaryId, string layout, string rowId)
        {
            SummaryId = summaryId;
            Layout = layout;
            RowId = rowId;
        }

        public string Get(string field) => Fields.TryGetValue(field, out string? value) ? value : string.Empty;
    }
}
