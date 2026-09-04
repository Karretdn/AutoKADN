using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoKADN.Tools.Excel;

public sealed class GenerarExcelTool
{
    private const string TemplateFileName = "FORMATO LEGALIZACION OBRA CIVIL_FT-09-PD-O-02 (V-2).xlsx";
    private const string TargetSheetName = "Formato de legalización.";
    private const string TargetCell = "D14";
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
        "ZONA VERDE",
        "ANDEN CONCRETO",
        "CALZADA CONCRETO",
        "ANDEN TABLETA",
        "ADOQUIN",
        "ASFALTO",
        "CUNETA",
        "DESTAPADO"
    };

    private static readonly Dictionary<string, string> ExcelActivityByKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["3/4|ANDEN CONCRETO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN ANDEN CONCRETO",
            ["3/4|ANDEN TABLETA"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN ANDÉN TABLETA, BALDOSÍN, GRAVILLA",
            ["3/4|ASFALTO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN CALZADA ASFALTO",
            ["3/4|CALZADA CONCRETO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN CALZADA CONCRETO",
            ["3/4|ZONA VERDE"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN ZONA VERDE",
            ["3/4|DESTAPADO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN DESTAPADO",
            ["3/4|CUNETA"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN CUNETA",
            ["3/4|ADOQUIN"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 3/4 PULG. EN ADOQUIN",
            ["1/2|ANDEN CONCRETO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN ANDEN CONCRETO",
            ["1/2|ANDEN TABLETA"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN ANDÉN TABLETA, BALDOSÍN, GRAVILLA",
            ["1/2|ASFALTO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN CALZADA ASFALTO",
            ["1/2|CALZADA CONCRETO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN CALZADA CONCRETO",
            ["1/2|ZONA VERDE"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN ZONA VERDE",
            ["1/2|DESTAPADO"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN DESTAPADO",
            ["1/2|CUNETA"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN CUNETA",
            ["1/2|ADOQUIN"] = "CANALIZACION TUBERÍA DE POLIETILENO DE 1/2 PULG. EN ADOQUIN"
        };

    private static readonly Dictionary<string, string> SurfaceFileCode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZONA VERDE"] = "ZV",
            ["ANDEN CONCRETO"] = "AC",
            ["CALZADA CONCRETO"] = "CC",
            ["ADOQUIN"] = "ADO",
            ["DESTAPADO"] = "DES",
            ["ANDEN TABLETA"] = "AT",
            ["CUNETA"] = "CUN",
            ["ASFALTO"] = "ASF"
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
                editor.WriteMessage("\nNo se encontró la plantilla '" + TemplateFileName + "' en la raíz del proyecto ni junto al DLL.\n");
                return;
            }

            List<UcKey> detectedUcs = ScanUcs(database);
            if (detectedUcs.Count == 0)
            {
                editor.WriteMessage("\nNo se encontraron UC válidas en los layouts 'ANILLO X UC'.\n");
                return;
            }

            editor.WriteMessage("\nUC detectadas: " + detectedUcs.Count + "\n");
            for (int i = 0; i < detectedUcs.Count; i++)
            {
                UcKey uc = detectedUcs[i];
                editor.WriteMessage("  " + (i + 1) + ". " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + "\n");
            }

            int generated = 0;
            foreach (UcKey uc in detectedUcs)
            {
                string activity = GetExcelActivity(uc);
                if (activity.Length == 0)
                {
                    editor.WriteMessage("\nNo existe correspondencia en el formato para " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + ".\n");
                    return;
                }

                editor.WriteMessage("\nUC " + (generated + 1) + "/" + detectedUcs.Count + ": " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + "\n");
                editor.WriteMessage("Actividad seleccionada: " + activity + "\n");

                PromptSaveFileOptions saveOptions = new PromptSaveFileOptions("\nGuardar formato Excel: ")
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    DialogCaption = "Guardar formato - " + uc.Diameter + " Pulg. " + ToDisplaySurface(uc.Surface),
                    InitialFileName = GetSuggestedFileName(uc)
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

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Copy(templatePath, outputPath, true);
                SetActivitySelection(outputPath, activity);

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
            DBDictionary layoutDictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);

            foreach (DBDictionaryEntry entry in layoutDictionary)
            {
                if (entry.Value.IsNull) continue;
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;

                string layoutName = layout.LayoutName.Trim();
                if (!IsUcLayout(layoutName)) continue;

                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    Dimension dimension = transaction.GetObject(objectId, OpenMode.ForRead) as Dimension;
                    if (dimension == null) continue;

                    string diameter = GetUcDiameter(dimension.Layer);
                    if (diameter == null) continue;

                    string surface = GetSurface(transaction, dimension);
                    if (surface == null) continue;

                    double value;
                    if (!TryGetDisplayedDimensionValue(dimension, out value)) continue;
                    detected.Add(new UcKey(diameter, surface));
                }
            }

            transaction.Commit();
        }

        return detected
            .OrderBy(x => GetSurfaceOrder(x.Surface))
            .ThenBy(x => DiameterOrder(x.Diameter))
            .ToList();
    }

    private static bool IsUcLayout(string layoutName)
    {
        return Regex.IsMatch(layoutName, @"^ANILLO\s+\d+\s+UC$", RegexOptions.IgnoreCase);
    }

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
            if (surface.Red.HasValue && IsSameRgb(color, surface.Red.Value, surface.Green.Value, surface.Blue.Value)) return surface.Name;
        }

        return null;
    }

    private static bool IsSameRgb(Color color, int red, int green, int blue)
    {
        return color.Red == red && color.Green == green && color.Blue == blue;
    }

    private static bool TryGetDisplayedDimensionValue(Dimension dimension, out double value)
    {
        value = 0.0;
        string text = dimension.DimensionText == null ? string.Empty : dimension.DimensionText.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;

        Match match = Regex.Match(text, @"[-+]?\d+(?:[\.,]\d+)?");
        if (!match.Success) return false;

        return double.TryParse(
            match.Value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string GetExcelActivity(UcKey uc)
    {
        string activity;
        return ExcelActivityByKey.TryGetValue(BuildKey(uc), out activity) ? activity : string.Empty;
    }

    private static string BuildKey(UcKey uc)
    {
        return uc.Diameter + "|" + uc.Surface;
    }

    private static string GetSuggestedFileName(UcKey uc)
    {
        string surfaceCode;
        if (!SurfaceFileCode.TryGetValue(uc.Surface, out surfaceCode))
            surfaceCode = "UC";

        string diameterCode = uc.Diameter == "1/2" ? "1-2" : "3-4";
        return surfaceCode + " " + diameterCode + " PULG.xlsx";
    }

    private static string FindTemplatePath(Database database)
    {
        var candidates = new List<string>();
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            string currentDirectory = assemblyDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(currentDirectory); i++)
            {
                candidates.Add(Path.Combine(currentDirectory, TemplateFileName));
                DirectoryInfo parent = Directory.GetParent(currentDirectory);
                currentDirectory = parent == null ? null : parent.FullName;
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, TemplateFileName));

        try
        {
            string drawingDirectory = Path.GetDirectoryName(database.Filename);
            if (!string.IsNullOrWhiteSpace(drawingDirectory))
                candidates.Add(Path.Combine(drawingDirectory, TemplateFileName));
        }
        catch
        {
            // El dibujo puede no tener una ruta de archivo todavía.
        }

        return candidates
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void SetActivitySelection(string path, string activity)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
        {
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

            ZipArchiveEntry workbookEntry = archive.GetEntry("xl/workbook.xml");
            if (workbookEntry == null) throw new InvalidDataException("La plantilla no contiene xl/workbook.xml.");

            ZipArchiveEntry workbookRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookRelsEntry == null) throw new InvalidDataException("La plantilla no contiene las relaciones del workbook.");

            XElement workbook = LoadXml(workbookEntry);
            XElement workbookRels = LoadXml(workbookRelsEntry);

            XElement sheets = workbook.Element(mainNs + "sheets");
            XElement targetSheet = sheets == null ? null : sheets.Elements(mainNs + "sheet")
                .FirstOrDefault(x => string.Equals((string)x.Attribute("name"), TargetSheetName, StringComparison.OrdinalIgnoreCase));

            if (targetSheet == null) throw new InvalidDataException("No se encontró la hoja '" + TargetSheetName + "'.");

            string relationshipId = (string)targetSheet.Attribute(relNs + "id");
            if (string.IsNullOrWhiteSpace(relationshipId))
                throw new InvalidDataException("La hoja '" + TargetSheetName + "' no tiene relación XML.");

            XElement relationship = workbookRels.Elements(packageRelNs + "Relationship")
                .FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relationshipId, StringComparison.Ordinal));

            if (relationship == null)
                throw new InvalidDataException("No se encontró la relación XML de la hoja '" + TargetSheetName + "'.");

            string target = (string)relationship.Attribute("Target");
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("La relación de '" + TargetSheetName + "' no tiene destino.");

            string worksheetPath = ResolveZipPath("xl/workbook.xml", target);
            ZipArchiveEntry worksheetEntry = archive.GetEntry(worksheetPath);
            if (worksheetEntry == null)
                throw new InvalidDataException("No se encontró la hoja XML '" + worksheetPath + "'.");

            XElement worksheet = LoadXml(worksheetEntry);
            XElement sheetData = worksheet.Element(mainNs + "sheetData");
            if (sheetData == null) throw new InvalidDataException("La hoja objetivo no contiene sheetData.");

            XElement row = sheetData.Elements(mainNs + "row")
                .FirstOrDefault(x => string.Equals((string)x.Attribute("r"), "14", StringComparison.Ordinal));

            if (row == null)
            {
                row = new XElement(mainNs + "row", new XAttribute("r", "14"));
                sheetData.Add(row);
            }

            XElement cell = row.Elements(mainNs + "c")
                .FirstOrDefault(x => string.Equals((string)x.Attribute("r"), TargetCell, StringComparison.Ordinal));

            if (cell == null)
            {
                cell = new XElement(mainNs + "c", new XAttribute("r", TargetCell));
                row.Add(cell);
            }

            XAttribute style = cell.Attribute("s");
            cell.RemoveNodes();
            cell.SetAttributeValue("t", "inlineStr");
            if (style != null) cell.SetAttributeValue("s", style.Value);
            cell.Add(new XElement(mainNs + "is", new XElement(mainNs + "t", activity)));

            SaveXml(worksheetEntry, worksheet);
        }
    }

    private static XElement LoadXml(ZipArchiveEntry entry)
    {
        using (Stream stream = entry.Open())
        {
            return XElement.Load(stream, LoadOptions.PreserveWhitespace);
        }
    }

    private static void SaveXml(ZipArchiveEntry entry, XElement document)
    {
        using (Stream stream = entry.Open())
        {
            document.Save(stream, SaveOptions.DisableFormatting);
        }
    }

    private static string ResolveZipPath(string basePath, string target)
    {
        string baseDirectory = Path.GetDirectoryName(basePath);
        baseDirectory = baseDirectory == null ? string.Empty : baseDirectory.Replace('\\', '/');
        string combined = string.IsNullOrWhiteSpace(baseDirectory) ? target : baseDirectory + "/" + target;
        var parts = new List<string>();

        foreach (string part in combined.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }

        return string.Join("/", parts);
    }

    private static string EnsureXlsxExtension(string path)
    {
        return path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? path : path + ".xlsx";
    }

    private static string ToDisplaySurface(string surface)
    {
        return surface == "ANDEN TABLETA" ? "ANDÉN TABLETA, BALDOSÍN, GRAVILLA" : surface;
    }

    private static int GetSurfaceOrder(string surface)
    {
        int index = Array.FindIndex(SurfaceOrder, value => string.Equals(value, surface, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private static int DiameterOrder(string diameter)
    {
        return diameter == "1/2" ? 0 : 1;
    }

    private struct UcKey : IEquatable<UcKey>
    {
        public UcKey(string diameter, string surface)
        {
            Diameter = diameter;
            Surface = surface;
        }

        public string Diameter { get; private set; }
        public string Surface { get; private set; }

        public bool Equals(UcKey other)
        {
            return string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Surface, other.Surface, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is UcKey && Equals((UcKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty) * 397)
                    ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Surface ?? string.Empty);
            }
        }
    }

    private sealed class UcSurface
    {
        public UcSurface(string name, int? colorIndex, int? red, int? green, int? blue)
        {
            Name = name;
            ColorIndex = colorIndex;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public string Name { get; private set; }
        public int? ColorIndex { get; private set; }
        public int? Red { get; private set; }
        public int? Green { get; private set; }
        public int? Blue { get; private set; }
    }
}