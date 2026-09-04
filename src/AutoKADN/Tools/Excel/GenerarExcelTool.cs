using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoKADN.Tools.Excel;

public sealed class GenerarExcelTool
{
    private const string TemplateFileName = "FORMATO LEGALIZACION OBRA CIVIL_FT-09-PD-O-02 (V-2).xlsx";
    private const string TargetSheetName = "Formato de legalización.";
    private const string TargetCell = "D14";
    private const string ActivityDefinedName = "ACTIVIDAD";
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";

    private static readonly UcSurface[] Surfaces =
    {
        new UcSurface("ZONA VERDE", 3, null, null, null),
        new UcSurface("ANDEN TABLETA", 1, null, null, null),
        new UcSurface("CALZADA CONCRETO", 8, null, null, null),
        new UcSurface("DESTAPADO", 2, null, null, null),
        new UcSurface("CUNETA", null, 100, 33, 101),
        new UcSurface("ANDEN CONCRETO", 5, null, null, null),
        new UcSurface("ASFALTO", 30, null, null, null),
        new UcSurface("ADOQUIN", 4, null, null, null)
    };

    private static readonly string[] SurfaceOrder =
    {
        "ZONA VERDE", "ANDEN CONCRETO", "CALZADA CONCRETO", "ANDEN TABLETA",
        "ADOQUIN", "ASFALTO", "CUNETA", "DESTAPADO"
    };

    private static readonly Dictionary<string, string> SurfaceFileCode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZONA VERDE"] = "ZV", ["ANDEN CONCRETO"] = "AC", ["CALZADA CONCRETO"] = "CC",
            ["ADOQUIN"] = "ADO", ["DESTAPADO"] = "DES", ["ANDEN TABLETA"] = "AT",
            ["CUNETA"] = "CUN", ["ASFALTO"] = "ASF"
        };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        try
        {
            string templatePath = FindTemplatePath(database);
            if (templatePath == null)
            {
                editor.WriteMessage("\nNo se encontró la plantilla '" + TemplateFileName + "'.\n");
                return;
            }

            List<UcKey> detectedUcs = ScanUcs(database);
            if (detectedUcs.Count == 0)
            {
                editor.WriteMessage("\nNo se encontraron UC válidas en los layouts 'ANILLO X UC'.\n");
                return;
            }

            string lastDirectory = null;
            int generated = 0;
            foreach (UcKey uc in detectedUcs)
            {
                PromptSaveFileOptions saveOptions = new PromptSaveFileOptions("\nGuardar formato Excel: ")
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    DialogCaption = "Guardar formato - " + uc.Diameter + " Pulg. " + ToDisplaySurface(uc.Surface),
                    InitialFileName = BuildInitialFileName(lastDirectory, uc)
                };

                PromptFileNameResult saveResult = editor.GetFileNameForSave(saveOptions);
                if (saveResult.Status != PromptStatus.OK)
                {
                    editor.WriteMessage("\nGeneración cancelada.\n");
                    return;
                }

                string outputPath = EnsureXlsxExtension(saveResult.StringResult);
                if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase))
                {
                    editor.WriteMessage("\nNo se puede sobrescribir la plantilla original. Seleccione otro archivo.\n");
                    return;
                }

                string selectedDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrWhiteSpace(selectedDirectory)) lastDirectory = selectedDirectory;
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Copy(templatePath, outputPath, true);

                SetActivitySelection(outputPath, uc);
                generated++;
                editor.WriteMessage("Excel generado: " + outputPath + "\n");
            }

            editor.WriteMessage("\nProceso terminado. Se generaron " + generated + " formato(s) Excel.\n");
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\nError generando Excel: " + ex.Message + "\n");
        }
    }

    private static List<UcKey> ScanUcs(Database database)
    {
        var detected = new HashSet<UcKey>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null || !IsUcLayout(layout.LayoutName.Trim())) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    Dimension dimension = transaction.GetObject(objectId, OpenMode.ForRead) as Dimension;
                    if (dimension == null) continue;
                    string diameter = GetUcDiameter(dimension.Layer);
                    string surface = diameter == null ? null : GetSurface(transaction, dimension);
                    double value;
                    if (surface == null || !TryGetDisplayedDimensionValue(dimension, out value)) continue;
                    detected.Add(new UcKey(diameter, surface));
                }
            }
            transaction.Commit();
        }
        return detected.OrderBy(x => GetSurfaceOrder(x.Surface)).ThenBy(x => DiameterOrder(x.Diameter)).ToList();
    }

    private static bool IsUcLayout(string name) => Regex.IsMatch(name, @"^ANILLO\s+\d+\s+UC$", RegexOptions.IgnoreCase);

    private static string GetUcDiameter(string layer)
    {
        if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }

    private static string GetSurface(Transaction transaction, Dimension dimension)
    {
        Color color = dimension.Color;
        if (color.ColorIndex == 256 || color.IsByLayer)
        {
            ObjectId layerId = dimension.LayerId;
            if (!layerId.IsNull)
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer != null) color = layer.Color;
            }
        }
        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && color.Red == surface.Red.Value && color.Green == surface.Green.Value && color.Blue == surface.Blue.Value) return surface.Name;
        }
        return null;
    }

    private static bool TryGetDisplayedDimensionValue(Dimension dimension, out double value)
    {
        value = 0.0;
        string text = dimension.DimensionText == null ? string.Empty : dimension.DimensionText.Trim();
        Match match = Regex.Match(text, @"[-+]?\d+(?:[\.,]\d+)?");
        return match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FindTemplatePath(Database database)
    {
        var candidates = new List<string>();
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            string current = assemblyDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                candidates.Add(Path.Combine(current, TemplateFileName));
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, TemplateFileName));
        try
        {
            string drawingDirectory = Path.GetDirectoryName(database.Filename);
            if (!string.IsNullOrWhiteSpace(drawingDirectory)) candidates.Add(Path.Combine(drawingDirectory, TemplateFileName));
        }
        catch { }
        return candidates.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static void SetActivitySelection(string path, UcKey uc)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
        {
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";

            ZipArchiveEntry workbookEntry = archive.GetEntry("xl/workbook.xml");
            ZipArchiveEntry workbookRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry == null || workbookRelsEntry == null) throw new InvalidDataException("La plantilla no contiene los archivos XML requeridos.");

            XElement workbook = LoadXml(workbookEntry);
            XElement workbookRels = LoadXml(workbookRelsEntry);
            XElement sheets = workbook.Element(mainNs + "sheets");
            XElement targetSheet = sheets == null ? null : sheets.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), TargetSheetName, StringComparison.OrdinalIgnoreCase));
            if (targetSheet == null) throw new InvalidDataException("No se encontró la hoja '" + TargetSheetName + "'.");

            string relationshipId = (string)targetSheet.Attribute(relNs + "id");
            XElement relationship = workbookRels.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relationshipId, StringComparison.Ordinal));
            if (relationship == null) throw new InvalidDataException("No se encontró la relación XML de la hoja.");

            string worksheetPath = ResolveZipPath("xl/workbook.xml", (string)relationship.Attribute("Target"));
            ZipArchiveEntry worksheetEntry = archive.GetEntry(worksheetPath);
            if (worksheetEntry == null) throw new InvalidDataException("No se encontró la hoja XML.");

            string activity = FindDropdownActivity(archive, workbook, workbookRels, mainNs, relNs, packageRelNs, uc);
            if (activity == null) throw new InvalidDataException("No existe una opción ACTIVIDAD compatible con " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + ".");

            XElement worksheet = LoadXml(worksheetEntry);
            XElement sheetData = worksheet.Element(mainNs + "sheetData");
            XElement row = sheetData == null ? null : sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), "14", StringComparison.Ordinal));
            if (row == null) { if (sheetData == null) throw new InvalidDataException("La hoja no contiene sheetData."); row = new XElement(mainNs + "row", new XAttribute("r", "14")); sheetData.Add(row); }
            XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), TargetCell, StringComparison.OrdinalIgnoreCase));
            if (cell == null) { cell = new XElement(mainNs + "c", new XAttribute("r", TargetCell)); row.Add(cell); }
            XAttribute style = cell.Attribute("s");
            cell.RemoveNodes();
            cell.SetAttributeValue("t", "inlineStr");
            if (style != null) cell.SetAttributeValue("s", style.Value);
            cell.Add(new XElement(mainNs + "is", new XElement(mainNs + "t", new XAttribute(xmlNs + "space", "preserve"), activity)));
            SaveXml(archive, worksheetPath, worksheetEntry, worksheet);

            SetWorkbookCalculationMode(archive, workbook, mainNs);
            RemoveCalculationChain(archive, workbookRels, packageRelNs);
        }
    }

    private static string FindDropdownActivity(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, UcKey uc)
    {
        XElement definedNames = workbook.Element(mainNs + "definedNames");
        XElement definedName = definedNames == null ? null : definedNames.Elements(mainNs + "definedName").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), ActivityDefinedName, StringComparison.OrdinalIgnoreCase));
        if (definedName == null) throw new InvalidDataException("No se encontró el nombre definido 'ACTIVIDAD'.");

        Match match = Regex.Match(definedName.Value.Trim(), @"^'?((?:[^']|'')+)'?!\$?([A-Z]+)\$?(\d+):\$?([A-Z]+)\$?(\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("No se pudo interpretar el rango ACTIVIDAD.");

        string sourceSheetName = match.Groups[1].Value.Replace("''", "'");
        string column = match.Groups[2].Value;
        int startRow = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int endRow = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        XElement sheets = workbook.Element(mainNs + "sheets");
        XElement sourceSheet = sheets == null ? null : sheets.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), sourceSheetName, StringComparison.OrdinalIgnoreCase));
        if (sourceSheet == null) throw new InvalidDataException("No se encontró la hoja origen de ACTIVIDAD.");

        string sourceRelId = (string)sourceSheet.Attribute(relNs + "id");
        XElement sourceRel = workbookRels.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), sourceRelId, StringComparison.Ordinal));
        if (sourceRel == null) throw new InvalidDataException("No se encontró la relación de la hoja origen de ACTIVIDAD.");
        string sourcePath = ResolveZipPath("xl/workbook.xml", (string)sourceRel.Attribute("Target"));
        ZipArchiveEntry sourceEntry = archive.GetEntry(sourcePath);
        if (sourceEntry == null) throw new InvalidDataException("No se encontró la hoja origen de ACTIVIDAD.");

        XElement sourceXml = LoadXml(sourceEntry);
        XElement sheetData = sourceXml.Element(mainNs + "sheetData");
        if (sheetData == null) return null;
        Dictionary<int, string> sharedStrings = LoadSharedStrings(archive, mainNs);
        string diameterToken = NormalizeActivityText(uc.Diameter + " PULG");
        string surfaceToken = NormalizeActivityText(GetDropdownSurfaceToken(uc.Surface));

        for (int r = startRow; r <= endRow; r++)
        {
            XElement row = sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), r.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (row == null) continue;
            XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), column + r.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
            if (cell == null) continue;
            string value = ReadCellText(cell, mainNs, sharedStrings);
            string normalized = NormalizeActivityText(value);
            if (normalized.IndexOf(diameterToken, StringComparison.Ordinal) >= 0 && normalized.IndexOf(surfaceToken, StringComparison.Ordinal) >= 0) return value;
        }
        return null;
    }

    private static string GetDropdownSurfaceToken(string surface)
    {
        if (string.Equals(surface, "ASFALTO", StringComparison.OrdinalIgnoreCase)) return "CALZADA ASFALTO";
        return surface;
    }

    private static string NormalizeActivityText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToUpperInvariant(c));
        }
        return Regex.Replace(builder.ToString(), @"\s+", "").Trim();
    }

    private static Dictionary<int, string> LoadSharedStrings(ZipArchive archive, XNamespace mainNs)
    {
        var result = new Dictionary<int, string>();
        ZipArchiveEntry entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;
        XElement root = LoadXml(entry);
        int i = 0;
        foreach (XElement si in root.Elements(mainNs + "si")) result[i++] = string.Concat(si.Descendants(mainNs + "t").Select(x => x.Value));
        return result;
    }

    private static string ReadCellText(XElement cell, XNamespace mainNs, Dictionary<int, string> sharedStrings)
    {
        string type = (string)cell.Attribute("t");
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase)) return string.Concat(cell.Descendants(mainNs + "t").Select(x => x.Value));
        XElement value = cell.Element(mainNs + "v");
        if (value == null) return string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
        {
            int index;
            return int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && sharedStrings.TryGetValue(index, out string text) ? text : string.Empty;
        }
        return value.Value;
    }

    private static void SetWorkbookCalculationMode(ZipArchive archive, XElement workbook, XNamespace mainNs)
    {
        XElement calcPr = workbook.Element(mainNs + "calcPr");
        if (calcPr == null) { calcPr = new XElement(mainNs + "calcPr"); workbook.Add(calcPr); }
        calcPr.SetAttributeValue("calcMode", "auto");
        calcPr.SetAttributeValue("fullCalcOnLoad", "1");
        calcPr.SetAttributeValue("forceFullCalc", "1");
        calcPr.SetAttributeValue("calcOnSave", "1");
        ZipArchiveEntry entry = archive.GetEntry("xl/workbook.xml");
        SaveXml(archive, "xl/workbook.xml", entry, workbook);
    }

    private static void RemoveCalculationChain(ZipArchive archive, XElement workbookRels, XNamespace packageRelNs)
    {
        ZipArchiveEntry chain = archive.GetEntry("xl/calcChain.xml");
        if (chain != null) chain.Delete();
        foreach (XElement rel in workbookRels.Elements(packageRelNs + "Relationship").Where(x => string.Equals((string)x.Attribute("Type"), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain", StringComparison.OrdinalIgnoreCase) || string.Equals((string)x.Attribute("Target"), "calcChain.xml", StringComparison.OrdinalIgnoreCase)).ToList()) rel.Remove();
        ZipArchiveEntry relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        SaveXml(archive, "xl/_rels/workbook.xml.rels", relEntry, workbookRels);

        ZipArchiveEntry contentEntry = archive.GetEntry("[Content_Types].xml");
        if (contentEntry != null)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            XElement content = LoadXml(contentEntry);
            foreach (XElement item in content.Elements(ns + "Override").Where(x => string.Equals((string)x.Attribute("PartName"), "/xl/calcChain.xml", StringComparison.OrdinalIgnoreCase)).ToList()) item.Remove();
            SaveXml(archive, "[Content_Types].xml", contentEntry, content);
        }
    }

    private static XElement LoadXml(ZipArchiveEntry entry)
    {
        using (Stream stream = entry.Open()) return XElement.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void SaveXml(ZipArchive archive, string entryName, ZipArchiveEntry oldEntry, XElement document)
    {
        if (oldEntry != null) oldEntry.Delete();
        ZipArchiveEntry newEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using (Stream stream = newEntry.Open()) document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static string ResolveZipPath(string basePath, string target)
    {
        string baseDirectory = Path.GetDirectoryName(basePath);
        string combined = string.IsNullOrWhiteSpace(baseDirectory) ? target : baseDirectory.Replace('\\', '/') + "/" + target;
        var parts = new List<string>();
        foreach (string part in combined.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join("/", parts);
    }

    private static string BuildInitialFileName(string lastDirectory, UcKey uc)
    {
        string name = GetSuggestedFileName(uc);
        return string.IsNullOrWhiteSpace(lastDirectory) ? name : Path.Combine(lastDirectory, name);
    }

    private static string GetSuggestedFileName(UcKey uc)
    {
        string code;
        if (!SurfaceFileCode.TryGetValue(uc.Surface, out code)) code = "UC";
        return code + " " + (uc.Diameter == "1/2" ? "1-2" : "3-4") + " PULG.xlsx";
    }

    private static string EnsureXlsxExtension(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? path : path + ".xlsx";
    private static string ToDisplaySurface(string surface) => surface == "ANDEN TABLETA" ? "ANDÉN TABLETA, BALDOSÍN, GRAVILLA" : surface;
    private static int GetSurfaceOrder(string surface) { int i = Array.FindIndex(SurfaceOrder, x => string.Equals(x, surface, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
    private static int DiameterOrder(string diameter) => diameter == "1/2" ? 0 : 1;

    private struct UcKey : IEquatable<UcKey>
    {
        public UcKey(string diameter, string surface) { Diameter = diameter; Surface = surface; }
        public string Diameter { get; private set; }
        public string Surface { get; private set; }
        public bool Equals(UcKey other) => string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase) && string.Equals(Surface, other.Surface, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is UcKey && Equals((UcKey)obj);
        public override int GetHashCode() { unchecked { return (StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty) * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Surface ?? string.Empty); } }
    }

    private sealed class UcSurface
    {
        public UcSurface(string name, int? colorIndex, int? red, int? green, int? blue) { Name = name; ColorIndex = colorIndex; Red = red; Green = green; Blue = blue; }
        public string Name { get; private set; }
        public int? ColorIndex { get; private set; }
        public int? Red { get; private set; }
        public int? Green { get; private set; }
        public int? Blue { get; private set; }
    }
}