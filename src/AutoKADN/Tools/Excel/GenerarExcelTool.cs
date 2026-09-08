using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoKADN.Tools.Excel;

public sealed class GenerarExcelTool
{
    private const string TargetSheetName = "Formato de legalización.";
    private const string TargetCell = "D14";
    private const string ActivityDefinedName = "ACTIVIDAD";
    private const string UcLayerHalf = "UC_1-2";
    private const string UcLayerThreeQuarter = "UC_3-4";
    private const string BlocksLayer = "Mat";
    private const string MaterialQuantityColumn = "G";
    private const int MaterialStartRow = 46;
    private const int MaterialEndRow = 121;
    private const string MaterialTestXDataType = "MATERIAL_PRUEBA";
    private const string SpiralXDataType = "ESPIRAL";
    private const string XDataAppName = "AUTOKADN";
    private const string UcSurfaceXDataType = "UC_SURFACE";

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

    private static readonly Dictionary<string, string> SurfaceFileCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ZONA VERDE"] = "ZV", ["ANDEN CONCRETO"] = "AC", ["CALZADA CONCRETO"] = "CC", ["ADOQUIN"] = "ADO",
        ["DESTAPADO"] = "DES", ["ANDEN TABLETA"] = "AT", ["CUNETA"] = "CUN", ["ASFALTO"] = "ASF"
    };

    private static readonly MaterialSpec[] MaterialCatalog =
    {
        new MaterialSpec("UNION", "1/2", "100003150"), new MaterialSpec("TUBERIA", "1/2", "100003135"),
        new MaterialSpec("TEE", "1/2", "100003119"), new MaterialSpec("TAPON", "1/2", "100003108"),
        new MaterialSpec("SILLETA", "2x3/4", "100003085"), new MaterialSpec("VALVULA", "3/4", "100003160"),
        new MaterialSpec("UNION", "3/4", "100003142"), new MaterialSpec("TUBERIA", "3/4", "100003130"),
        new MaterialSpec("TEE", "3/4", "100003118"), new MaterialSpec("TAPON", "3/4", "100003102"),
        new MaterialSpec("REDUCCION", "3/4x1/2", "100003075"), new MaterialSpec("SILLETA", "3x3/4", "100003086"),
        new MaterialSpec("SILLETA", "4x3/4", "100003087"), new MaterialSpec("SILLETA", "6x3/4", "100003088")
    };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document == null) return;
        Editor editor = document.Editor;
        Database database = document.Database;
        try
        {
            string templatePath = SelectTemplatePath(editor);
            if (string.IsNullOrWhiteSpace(templatePath)) { editor.WriteMessage("\nGeneración cancelada: no se seleccionó la plantilla base.\n"); return; }
            Dictionary<UcKey, double> ucPipeTotals = ScanUcTotals(database);
            if (ucPipeTotals.Count == 0) { editor.WriteMessage("\nNo se encontraron UC válidas en los layouts 'ANILLO X UC'.\n"); return; }
            List<UcKey> detectedUcs = ucPipeTotals.Keys.OrderBy(x => GetSurfaceOrder(x.Surface)).ThenBy(x => DiameterOrder(x.Diameter)).ToList();
            Dictionary<UcKey, Dictionary<MaterialKey, double>> materialQuantities = ScanAccessories(database);
            MergeMaterialQuantities(materialQuantities, BuildPipeMaterialQuantities(ucPipeTotals));
            MergeMaterialQuantities(materialQuantities, ScanSpiral(database));
            MergeMaterialQuantities(materialQuantities, ScanMaterialTest(database));
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(templatePath));
            int generated = 0;
            editor.WriteMessage("\nUC detectadas: " + detectedUcs.Count + ". Se procesarán una por una.\n");
            foreach (UcKey uc in detectedUcs)
            {
                string suggestedPath = Path.Combine(outputDirectory, GetSuggestedFileName(uc));
                PromptSaveFileOptions saveOptions = new PromptSaveFileOptions("\nGuardar Excel para " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + ": ") { Filter = "Excel (*.xlsx)|*.xlsx", DialogCaption = "Guardar formato - " + uc.Diameter + " Pulg. " + ToDisplaySurface(uc.Surface), InitialFileName = suggestedPath };
                PromptFileNameResult saveResult = editor.GetFileNameForSave(saveOptions);
                if (saveResult.Status != PromptStatus.OK) { editor.WriteMessage("\nSe omitió " + uc.Diameter + " Pulg. - " + ToDisplaySurface(uc.Surface) + ". Continuando con la siguiente UC.\n"); continue; }
                string outputPath = EnsureXlsxExtension(saveResult.StringResult);
                if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(templatePath), StringComparison.OrdinalIgnoreCase)) { editor.WriteMessage("\nNo se puede sobrescribir la plantilla original. Se omitirá esta UC y se continuará con la siguiente.\n"); continue; }
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Copy(templatePath, outputPath, true);
                string activity = SetActivitySelection(outputPath, uc);
                Dictionary<MaterialKey, double> quantities;
                if (!materialQuantities.TryGetValue(uc, out quantities)) quantities = new Dictionary<MaterialKey, double>();
                SetMaterialQuantities(outputPath, activity, quantities);
                generated++;
                editor.WriteMessage("Excel generado: " + outputPath + "\n");
            }
            editor.WriteMessage("\nProceso terminado. Se generaron " + generated + " de " + detectedUcs.Count + " formato(s) Excel.\n");
        }
        catch (Exception ex) { editor.WriteMessage("\nError generando Excel: " + ex.Message + "\n"); }
    }

    private static string SelectTemplatePath(Editor editor)
    {
        PromptOpenFileOptions options = new PromptOpenFileOptions("\nSeleccione la plantilla Excel base: ") { Filter = "Excel (*.xlsx)|*.xlsx", DialogCaption = "Seleccionar plantilla Excel base", PreferCommandLine = false };
        PromptFileNameResult result = editor.GetFileNameForOpen(options);
        if (result.Status != PromptStatus.OK) return null;
        string path = result.StringResult;
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static Dictionary<UcKey, double> ScanUcTotals(Database database)
    {
        var totals = new Dictionary<UcKey, double>();
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
                    if (diameter == null) continue;
                    string surface = GetSurface(transaction, dimension);
                    double value;
                    if (surface == null || !TryGetDisplayedDimensionValue(dimension, out value)) continue;
                    UcKey key = new UcKey(diameter, surface);
                    double current;
                    totals.TryGetValue(key, out current);
                    totals[key] = current + Math.Abs(value);
                }
            }
            transaction.Commit();
        }
        return totals;
    }

    private static Dictionary<UcKey, Dictionary<MaterialKey, double>> BuildPipeMaterialQuantities(Dictionary<UcKey, double> ucPipeTotals)
    {
        var result = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        foreach (KeyValuePair<UcKey, double> entry in ucPipeTotals)
        {
            if (entry.Value <= 0.0) continue;
            MaterialSpec material;
            if (!TryGetMaterialSpec("TUBERIA", entry.Key.Diameter, out material)) continue;
            AddMaterialQuantity(result, entry.Key, material, "ML", entry.Value);
        }
        return result;
    }

    private static Dictionary<UcKey, Dictionary<MaterialKey, double>> ScanAccessories(Database database)
    {
        var result = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null || !IsDetailLayout(layout.LayoutName.Trim())) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    BlockReference blockReference = transaction.GetObject(objectId, OpenMode.ForRead) as BlockReference;
                    if (blockReference == null || !string.Equals(blockReference.Layer, BlocksLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    string surface = GetBlockSurface(transaction, blockReference);
                    if (surface == null) continue;
                    string description = GetBlockName(transaction, blockReference);
                    string blockDiameter = GetDiameter(blockReference);
                    MaterialSpec material;
                    if (!TryGetMaterialSpec(description, blockDiameter, out material)) continue;
                    string ucDiameter = GetUcDiameterFromMaterial(material.Diameter);
                    if (ucDiameter == null) continue;
                    AddMaterialQuantity(result, new UcKey(ucDiameter, surface), material, "UND", 1.0);
                }
            }
            transaction.Commit();
        }
        return result;
    }

    private static Dictionary<UcKey, Dictionary<MaterialKey, double>> ScanSpiral(Database database)
    {
        var result = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    MText mtext = transaction.GetObject(objectId, OpenMode.ForRead) as MText;
                    if (mtext == null) continue;
                    ResultBuffer xdata = mtext.XData;
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, SpiralXDataType, StringComparison.OrdinalIgnoreCase))
                        {
                            typeIndex = i;
                            break;
                        }
                    }
                    if (typeIndex < 0) continue;
                    int index = typeIndex + 1;
                    if (index + 8 >= values.Length) continue;
                    double pipe;
                    double unions;
                    double tees;
                    double valves;
                    double saddles;
                    if (!TryReadXDataDouble(values[index].Value, out pipe) || !TryReadXDataDouble(values[index + 1].Value, out unions) || !TryReadXDataDouble(values[index + 2].Value, out tees) || !TryReadXDataDouble(values[index + 3].Value, out valves) || !TryReadXDataDouble(values[index + 4].Value, out saddles)) continue;
                    string saddleDiameter = values[index + 5].Value == null ? string.Empty : values[index + 5].Value.ToString().Trim();
                    string surface = values[index + 7].Value == null ? string.Empty : values[index + 7].Value.ToString().Trim();
                    surface = NormalizeSurface(surface);
                    if (string.IsNullOrWhiteSpace(surface)) continue;
                    UcKey uc = new UcKey("3/4", surface);
                    MaterialSpec material;
                    if (pipe > 0.0 && TryGetMaterialSpec("TUBERIA", "3/4", out material)) AddMaterialQuantity(result, uc, material, "ML", Math.Abs(pipe));
                    if (unions > 0.0 && TryGetMaterialSpec("UNION", "3/4", out material)) AddMaterialQuantity(result, uc, material, "UND", Math.Abs(unions));
                    if (tees > 0.0 && TryGetMaterialSpec("TEE", "3/4", out material)) AddMaterialQuantity(result, uc, material, "UND", Math.Abs(tees));
                    if (valves > 0.0 && TryGetMaterialSpec("VALVULA", "3/4", out material)) AddMaterialQuantity(result, uc, material, "UND", Math.Abs(valves));
                    if (saddles > 0.0 && TryGetMaterialSpec("SILLETA", saddleDiameter, out material)) AddMaterialQuantity(result, uc, material, "UND", Math.Abs(saddles));
                }
            }
            transaction.Commit();
        }
        return result;
    }

    private static Dictionary<UcKey, Dictionary<MaterialKey, double>> ScanMaterialTest(Database database)
    {
        var result = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null) continue;
                BlockTableRecord space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId objectId in space)
                {
                    Entity entity = transaction.GetObject(objectId, OpenMode.ForRead) as Entity;
                    if (entity == null) continue;
                    ResultBuffer xdata = entity.XData;
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, MaterialTestXDataType, StringComparison.OrdinalIgnoreCase))
                        {
                            typeIndex = i;
                            break;
                        }
                    }
                    if (typeIndex < 0 || typeIndex + 4 >= values.Length) continue;
                    string description = values[typeIndex + 1].Value == null ? string.Empty : values[typeIndex + 1].Value.ToString().Trim();
                    string diameter = values[typeIndex + 2].Value == null ? string.Empty : values[typeIndex + 2].Value.ToString().Trim();
                    string surface = values[typeIndex + 3].Value == null ? string.Empty : NormalizeSurface(values[typeIndex + 3].Value.ToString().Trim());
                    double quantity;
                    if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(surface) || !TryReadXDataDouble(values[typeIndex + 4].Value, out quantity)) continue;
                    MaterialSpec material;
                    if (!TryGetMaterialSpec(description, diameter, out material)) continue;
                    string ucDiameter = GetUcDiameterFromMaterial(material.Diameter);
                    if (ucDiameter == null) continue;
                    AddMaterialQuantity(result, new UcKey(ucDiameter, surface), material, "UND", Math.Abs(quantity));
                }
            }
            transaction.Commit();
        }
        return result;
    }

    private static void MergeMaterialQuantities(Dictionary<UcKey, Dictionary<MaterialKey, double>> target, Dictionary<UcKey, Dictionary<MaterialKey, double>> source)
    {
        foreach (KeyValuePair<UcKey, Dictionary<MaterialKey, double>> group in source)
        {
            foreach (KeyValuePair<MaterialKey, double> material in group.Value)
            {
                double current;
                if (!target.TryGetValue(group.Key, out Dictionary<MaterialKey, double> targetGroup))
                {
                    targetGroup = new Dictionary<MaterialKey, double>();
                    target[group.Key] = targetGroup;
                }
                targetGroup.TryGetValue(material.Key, out current);
                targetGroup[material.Key] = current + material.Value;
            }
        }
    }

    private static void AddMaterialQuantity(Dictionary<UcKey, Dictionary<MaterialKey, double>> target, UcKey uc, MaterialSpec material, string unit, double quantity)
    {
        if (quantity <= 0.0) return;
        if (!target.TryGetValue(uc, out Dictionary<MaterialKey, double> group))
        {
            group = new Dictionary<MaterialKey, double>();
            target[uc] = group;
        }
        MaterialKey key = new MaterialKey(material.Description, material.Diameter, material.Code, unit);
        double current;
        group.TryGetValue(key, out current);
        group[key] = current + quantity;
    }

    private static string SetActivitySelection(string outputPath, UcKey uc)
    {
        string activity = FindDropdownActivity(outputPath, uc);
        if (string.IsNullOrWhiteSpace(activity)) return string.Empty;
        UpdateCellValue(outputPath, TargetSheetName, TargetCell, activity);
        return activity;
    }

    private static void SetMaterialQuantities(string outputPath, string activity, Dictionary<MaterialKey, double> quantities)
    {
        if (string.IsNullOrWhiteSpace(activity) || quantities == null || quantities.Count == 0) return;
        List<SourceMaterialRow> sourceRows = FindSourceMaterialsForActivity(outputPath, activity);
        if (sourceRows.Count == 0) return;
        foreach (SourceMaterialRow row in sourceRows)
        {
            double total = 0.0;
            foreach (KeyValuePair<MaterialKey, double> item in quantities)
            {
                if (TryMatchSourceMaterial(row, item.Key)) total += item.Value;
            }
            if (total > 0.0) UpdateCellValue(outputPath, TargetSheetName, MaterialQuantityColumn + row.Row.ToString(CultureInfo.InvariantCulture), total.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static bool TryMatchSourceMaterial(SourceMaterialRow source, MaterialKey key)
    {
        if (!string.IsNullOrWhiteSpace(source.Code) && !string.IsNullOrWhiteSpace(key.Code)) return string.Equals(source.Code.Trim(), key.Code.Trim(), StringComparison.OrdinalIgnoreCase);
        return NormalizeToken(source.Description) == NormalizeToken(key.Description) && NormalizeToken(source.Diameter) == NormalizeToken(key.Diameter);
    }

    private static List<SourceMaterialRow> FindSourceMaterialsForActivity(string xlsxPath, string activity)
    {
        var rows = new List<SourceMaterialRow>();
        using (ZipArchive archive = ZipFile.OpenRead(xlsxPath))
        {
            string workbookXml = ReadZipEntry(archive, "xl/workbook.xml");
            string relsXml = ReadZipEntry(archive, "xl/_rels/workbook.xml.rels");
            if (string.IsNullOrWhiteSpace(workbookXml) || string.IsNullOrWhiteSpace(relsXml)) return rows;
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            XDocument workbook = XDocument.Parse(workbookXml);
            XDocument rels = XDocument.Parse(relsXml);
            XElement sheet = workbook.Root == null ? null : workbook.Root.Element(mainNs + "sheets")?.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), "materiales leg", StringComparison.OrdinalIgnoreCase));
            if (sheet == null) return rows;
            string relId = (string)sheet.Attribute(relNs + "id");
            string target = rels.Root?.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value;
            if (string.IsNullOrWhiteSpace(target)) return rows;
            string sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
            string sheetXml = ReadZipEntry(archive, sheetPath.Replace("xl/xl/", "xl/"));
            if (string.IsNullOrWhiteSpace(sheetXml)) return rows;
            XDocument sheetDoc = XDocument.Parse(sheetXml);
            List<string> sharedStrings = ReadSharedStrings(archive, mainNs);
            Dictionary<string, string> cells = ReadSheetCells(sheetDoc, sharedStrings, mainNs);
            string activityCode = FindActivityCode(cells, activity);
            if (string.IsNullOrWhiteSpace(activityCode)) return rows;
            for (int row = MaterialStartRow; row <= MaterialEndRow; row++)
            {
                string sourceActivityCode = GetCell(cells, "G", row);
                if (!string.Equals(sourceActivityCode.Trim(), activityCode.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new SourceMaterialRow(row, GetCell(cells, "J", row), GetCell(cells, "K", row)));
            }
        }
        return rows;
    }

    private static string FindActivityCode(Dictionary<string, string> cells, string activity)
    {
        for (int row = 1; row <= 5000; row++)
        {
            string candidate = GetCell(cells, "A", row);
            if (string.Equals(NormalizeToken(candidate), NormalizeToken(activity), StringComparison.OrdinalIgnoreCase)) return GetCell(cells, "B", row);
        }
        return string.Empty;
    }

    private static string FindDropdownActivity(string xlsxPath, UcKey uc)
    {
        using (ZipArchive archive = ZipFile.OpenRead(xlsxPath))
        {
            string workbookXml = ReadZipEntry(archive, "xl/workbook.xml");
            string relsXml = ReadZipEntry(archive, "xl/_rels/workbook.xml.rels");
            if (string.IsNullOrWhiteSpace(workbookXml) || string.IsNullOrWhiteSpace(relsXml)) return string.Empty;
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            XDocument workbook = XDocument.Parse(workbookXml);
            XDocument rels = XDocument.Parse(relsXml);
            XElement definedName = workbook.Root?.Element(mainNs + "definedNames")?.Elements(mainNs + "definedName").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), ActivityDefinedName, StringComparison.OrdinalIgnoreCase));
            if (definedName == null) return string.Empty;
            string formula = definedName.Value.Trim().TrimStart('=');
            int bang = formula.IndexOf('!');
            if (bang < 0) return string.Empty;
            string sheetName = formula.Substring(0, bang).Trim('\'');
            string range = formula.Substring(bang + 1).Trim();
            int colon = range.IndexOf(':');
            string startCell = colon >= 0 ? range.Substring(0, colon) : range;
            string endCell = colon >= 0 ? range.Substring(colon + 1) : range;
            int startRow = GetRowNumber(startCell);
            int endRow = GetRowNumber(endCell);
            if (startRow <= 0 || endRow <= 0) return string.Empty;
            XElement sheet = workbook.Root?.Element(mainNs + "sheets")?.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase));
            if (sheet == null) return string.Empty;
            string relId = (string)sheet.Attribute(relNs + "id");
            string target = rels.Root?.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value;
            if (string.IsNullOrWhiteSpace(target)) return string.Empty;
            string sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
            string sheetXml = ReadZipEntry(archive, sheetPath.Replace("xl/xl/", "xl/"));
            if (string.IsNullOrWhiteSpace(sheetXml)) return string.Empty;
            XDocument sheetDoc = XDocument.Parse(sheetXml);
            List<string> sharedStrings = ReadSharedStrings(archive, mainNs);
            Dictionary<string, string> cells = ReadSheetCells(sheetDoc, sharedStrings, mainNs);
            string targetDiameter = NormalizeToken(uc.Diameter);
            string targetSurface = NormalizeToken(uc.Surface);
            for (int row = startRow; row <= endRow; row++)
            {
                string activity = GetCell(cells, "A", row);
                if (string.IsNullOrWhiteSpace(activity)) continue;
                string diameter = GetCell(cells, "B", row);
                string surface = GetCell(cells, "C", row);
                if (NormalizeToken(diameter) == targetDiameter && NormalizeToken(surface) == targetSurface) return activity;
            }
        }
        return string.Empty;
    }

    private static void UpdateCellValue(string xlsxPath, string sheetName, string address, string value)
    {
        string temp = xlsxPath + ".tmp";
        using (ZipArchive source = ZipFile.OpenRead(xlsxPath))
        using (ZipArchive target = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                ZipArchiveEntry newEntry = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using (Stream input = entry.Open()) using (Stream output = newEntry.Open()) input.CopyTo(output);
            }
        }
        File.Delete(xlsxPath);
        File.Move(temp, xlsxPath);
    }

    private static Dictionary<string, string> ReadSheetCells(XDocument sheetDoc, List<string> sharedStrings, XNamespace mainNs)
    {
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement cell in sheetDoc.Descendants(mainNs + "c"))
        {
            string reference = (string)cell.Attribute("r");
            if (string.IsNullOrWhiteSpace(reference)) continue;
            string type = (string)cell.Attribute("t");
            string value = cell.Element(mainNs + "v")?.Value ?? string.Empty;
            if (type == "s" && int.TryParse(value, out int sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count) value = sharedStrings[sharedIndex];
            else if (type == "inlineStr") value = string.Concat(cell.Descendants(mainNs + "t").Select(x => x.Value));
            cells[reference] = value;
        }
        return cells;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive, XNamespace mainNs)
    {
        var values = new List<string>();
        string xml = ReadZipEntry(archive, "xl/sharedStrings.xml");
        if (string.IsNullOrWhiteSpace(xml)) return values;
        XDocument doc = XDocument.Parse(xml);
        foreach (XElement si in doc.Descendants(mainNs + "si")) values.Add(string.Concat(si.Descendants(mainNs + "t").Select(x => x.Value)));
        return values;
    }

    private static string ReadZipEntry(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path);
        if (entry == null) return string.Empty;
        using (Stream stream = entry.Open()) using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
    }

    private static string GetCell(Dictionary<string, string> cells, string column, int row) => cells.TryGetValue(column + row.ToString(CultureInfo.InvariantCulture), out string value) ? value : string.Empty;
    private static int GetRowNumber(string cell)
    {
        Match match = Regex.Match(cell ?? string.Empty, "(\\d+)$");
        return match.Success && int.TryParse(match.Groups[1].Value, out int row) ? row : 0;
    }
    private static bool TryReadXDataDouble(object value, out double result) => double.TryParse(value == null ? string.Empty : value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result) || double.TryParse(value == null ? string.Empty : value.ToString(), NumberStyles.Any, CultureInfo.CurrentCulture, out result);
    private static bool TryGetDisplayedDimensionValue(Dimension dimension, out double value)
    {
        string text = dimension.DimensionText ?? string.Empty;
        text = Regex.Replace(text, "<[^>]+>", string.Empty).Replace("\\P", " ").Trim();
        text = Regex.Replace(text, "[^0-9,.-]", string.Empty).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
    private static string GetUcDiameter(string layer)
    {
        if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2";
        if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4";
        return null;
    }
    private static string GetUcDiameterFromMaterial(string diameter)
    {
        string normalized = NormalizeToken(diameter);
        if (normalized == "12" || normalized.StartsWith("12X")) return "1/2";
        if (normalized == "34" || normalized.StartsWith("34X")) return "3/4";
        return null;
    }
    private static string GetSurface(Transaction transaction, Entity entity)
    {
        ResultBuffer xdata = entity.XData;
        if (xdata != null)
        {
            TypedValue[] values = xdata.AsArray();
            for (int i = 0; i < values.Length; i++) if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, UcSurfaceXDataType, StringComparison.OrdinalIgnoreCase) && i + 1 < values.Length) return NormalizeSurface(values[i + 1].Value?.ToString());
        }
        return GetSurfaceFromColor(entity.Color);
    }
    private static string GetBlockSurface(Transaction transaction, BlockReference blockReference) => GetSurfaceFromColor(blockReference.Color);
    private static string GetSurfaceFromColor(Color color)
    {
        if (color == null || color.ColorMethod != ColorMethod.ByColor) return null;
        int index = color.ColorIndex;
        return Surfaces.FirstOrDefault(x => x.ColorIndex == index)?.Name;
    }
    private static string NormalizeSurface(string surface)
    {
        if (string.IsNullOrWhiteSpace(surface)) return null;
        string value = NormalizeToken(surface);
        foreach (UcSurface item in Surfaces) if (NormalizeToken(item.Name) == value) return item.Name;
        return null;
    }
    private static string ToDisplaySurface(string surface) => string.Equals(surface, "ASFALTO", StringComparison.OrdinalIgnoreCase) ? "CALZADA ASFALTO" : surface;
    private static int GetSurfaceOrder(string surface)
    {
        int index = Array.FindIndex(SurfaceOrder, x => string.Equals(x, surface, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 999 : index;
    }
    private static int DiameterOrder(string diameter) => string.Equals(diameter, "1/2", StringComparison.OrdinalIgnoreCase) ? 1 : string.Equals(diameter, "3/4", StringComparison.OrdinalIgnoreCase) ? 2 : 999;
    private static bool IsUcLayout(string name) => NormalizeToken(name).StartsWith("ANILLOXUC");
    private static bool IsDetailLayout(string name) => NormalizeToken(name).StartsWith("DETALLE");
    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = value.Replace('\u00A0', ' ');
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        normalized = normalized.Normalize(NormalizationForm.FormD);
        normalized = new string(normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return normalized.ToUpperInvariant().Replace(" ", string.Empty);
    }
    private static string GetBlockName(Transaction transaction, BlockReference blockReference)
    {
        BlockTableRecord record = transaction.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
        return record == null ? string.Empty : record.Name.Replace("*U", string.Empty).Trim();
    }
    private static string GetDiameter(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock) return string.Empty;
        foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection) if (string.Equals(property.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase)) return property.Value == null ? string.Empty : property.Value.ToString().Trim();
        return string.Empty;
    }
    private static string GetSuggestedFileName(UcKey uc)
    {
        string surfaceCode; if (!SurfaceFileCode.TryGetValue(uc.Surface, out surfaceCode)) surfaceCode = NormalizeToken(uc.Surface); string diameterCode = uc.Diameter == "1/2" ? "1-2" : "3-4"; return surfaceCode + " " + diameterCode + " PULG.xlsx";
    }
    private static string EnsureXlsxExtension(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? path : path + ".xlsx";
    private static bool TryGetMaterialSpec(string description, string diameter, out MaterialSpec material)
    {
        string d = NormalizeToken(diameter); string desc = NormalizeToken(description);
        material = MaterialCatalog.FirstOrDefault(x => NormalizeToken(x.Description) == desc && NormalizeToken(x.Diameter) == d);
        return material != null;
    }

    private sealed record UcSurface(string Name, int? ColorIndex, int? TrueColorR, int? TrueColorG, int? TrueColorB);
    private sealed record MaterialSpec(string Description, string Diameter, string Code);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct MaterialKey(string Description, string Diameter, string Code, string Unit);
    private sealed record SourceMaterialRow(int Row, string Code, string Description);
}
