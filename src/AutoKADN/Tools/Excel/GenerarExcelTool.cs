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
            Dictionary<UcKey, double> ucPipeTotals = ScanUcs(database);
            if (ucPipeTotals.Count == 0) { editor.WriteMessage("\nNo se encontraron UC válidas en los layouts 'ANILLO X UC'.\n"); return; }
            List<UcKey> detectedUcs = ucPipeTotals.Keys.OrderBy(x => GetSurfaceOrder(x.Surface)).ThenBy(x => DiameterOrder(x.Diameter)).ToList();
            Dictionary<UcKey, Dictionary<MaterialKey, double>> materialQuantities = ScanAccessories(database);
            MergeMaterialQuantities(materialQuantities, ScanSpiral(database));
            MergeMaterialQuantities(materialQuantities, ScanMaterialTest(database));
            MergeMaterialQuantities(materialQuantities, ConvertUcTotalsToPipeMaterials(ucPipeTotals));
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

    private static Dictionary<UcKey, double> ScanUcs(Database database)
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
                    if (surface == null) continue;
                    double value;
                    if (!TryGetDisplayedDimensionValue(dimension, out value)) continue;
                    UcKey uc = new UcKey(diameter, surface);
                    double current;
                    totals.TryGetValue(uc, out current);
                    totals[uc] = current + Math.Abs(value);
                }
            }
            transaction.Commit();
        }
        return totals;
    }

    private static Dictionary<UcKey, Dictionary<MaterialKey, double>> ConvertUcTotalsToPipeMaterials(Dictionary<UcKey, double> ucLengths)
    {
        var result = new Dictionary<UcKey, Dictionary<MaterialKey, double>>();
        foreach (KeyValuePair<UcKey, double> entry in ucLengths)
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
                    GetMaterialXData(blockReference, ref description, ref blockDiameter, ref surface);
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
                    if (index + 7 >= values.Length) continue;
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
                    MText mtext = transaction.GetObject(objectId, OpenMode.ForRead) as MText;
                    if (mtext == null) continue;
                    ResultBuffer xdata = mtext.XData;
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++) if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, MaterialTestXDataType, StringComparison.OrdinalIgnoreCase)) { typeIndex = i; break; }
                    if (typeIndex < 0) continue;
                    int index = typeIndex + 3;
                    while (index + 5 < values.Length)
                    {
                        string description = values[index].Value == null ? string.Empty : values[index].Value.ToString().Trim();
                        string diameter = values[index + 1].Value == null ? string.Empty : values[index + 1].Value.ToString().Trim();
                        string unit = values[index + 2].Value == null ? string.Empty : values[index + 2].Value.ToString().Trim();
                        double quantity;
                        if (!TryReadXDataDouble(values[index + 3].Value, out quantity)) break;
                        string ucDiameter = values[index + 4].Value == null ? string.Empty : values[index + 4].Value.ToString().Trim();
                        string surface = values[index + 5].Value == null ? string.Empty : values[index + 5].Value.ToString().Trim();
                        MaterialSpec material;
                        if (TryGetMaterialSpec(description, diameter, out material))
                        {
                            string normalizedUcDiameter = NormalizeDiameter(ucDiameter);
                            if (normalizedUcDiameter == "1/2" || normalizedUcDiameter == "3/4") AddMaterialQuantity(result, new UcKey(normalizedUcDiameter, NormalizeSurface(surface)), material, unit, Math.Abs(quantity));
                        }
                        index += 6;
                    }
                }
            }
            transaction.Commit();
        }
        return result;
    }

    private static bool TryReadXDataDouble(object value, out double result)
    {
        result = 0.0;
        if (value == null) return false;
        if (value is double) { result = (double)value; return true; }
        if (value is float) { result = (double)(float)value; return true; }
        if (value is int) { result = (int)value; return true; }
        if (value is short) { result = (short)value; return true; }
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static void AddMaterialQuantity(Dictionary<UcKey, Dictionary<MaterialKey, double>> result, UcKey uc, MaterialSpec material, string unit, double quantity)
    {
        if (string.IsNullOrWhiteSpace(uc.Surface) || quantity == 0.0) return;
        string normalizedUnit = string.IsNullOrWhiteSpace(unit) ? "UND" : unit.Trim();
        MaterialKey materialKey = new MaterialKey(material.Description, NormalizeDiameter(material.Diameter), normalizedUnit, material.Code);
        Dictionary<MaterialKey, double> materials;
        if (!result.TryGetValue(uc, out materials)) { materials = new Dictionary<MaterialKey, double>(); result.Add(uc, materials); }
        double current; materials.TryGetValue(materialKey, out current); materials[materialKey] = current + quantity;
    }

    private static void MergeMaterialQuantities(Dictionary<UcKey, Dictionary<MaterialKey, double>> target, Dictionary<UcKey, Dictionary<MaterialKey, double>> source)
    {
        foreach (KeyValuePair<UcKey, Dictionary<MaterialKey, double>> ucEntry in source)
        {
            Dictionary<MaterialKey, double> targetMaterials;
            if (!target.TryGetValue(ucEntry.Key, out targetMaterials)) { targetMaterials = new Dictionary<MaterialKey, double>(); target.Add(ucEntry.Key, targetMaterials); }
            foreach (KeyValuePair<MaterialKey, double> materialEntry in ucEntry.Value)
            {
                double current; targetMaterials.TryGetValue(materialEntry.Key, out current); targetMaterials[materialEntry.Key] = current + materialEntry.Value;
            }
        }
    }

    private static bool TryGetMaterialSpec(string description, string diameter, out MaterialSpec material)
    {
        material = null;
        string normalizedDescription = NormalizeToken(description);
        string normalizedDiameter = NormalizeDiameter(diameter);
        if (string.IsNullOrWhiteSpace(normalizedDescription)) return false;
        foreach (MaterialSpec candidate in MaterialCatalog)
        {
            if (normalizedDescription.IndexOf(NormalizeToken(candidate.Description), StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!string.IsNullOrWhiteSpace(normalizedDiameter) && NormalizeDiameter(candidate.Diameter) == normalizedDiameter) { material = candidate; return true; }
        }
        foreach (MaterialSpec candidate in MaterialCatalog)
        {
            string candidateName = NormalizeToken(candidate.Description);
            if (normalizedDescription.IndexOf(candidateName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            string candidateDiameter = NormalizeDiameter(candidate.Diameter);
            if (normalizedDescription.IndexOf(candidateDiameter, StringComparison.OrdinalIgnoreCase) >= 0) { material = candidate; return true; }
        }
        return false;
    }

    private static string GetUcDiameterFromMaterial(string diameter)
    {
        string normalized = NormalizeDiameter(diameter);
        if (normalized == "1/2") return "1/2";
        if (normalized == "3/4" || normalized.EndsWith("X3/4", StringComparison.OrdinalIgnoreCase)) return "3/4";
        if (normalized == "3/4X1/2") return "3/4";
        return null;
    }

    private static Color GetEffectiveColor(Transaction transaction, Entity entity)
    {
        Color color = entity.Color;
        if (color.ColorMethod == ColorMethod.ByLayer)
        {
            ObjectId layerId = entity.LayerId;
            if (!layerId.IsNull)
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer != null) color = layer.Color;
            }
        }
        return color;
    }

    private static string GetBlockSurface(Transaction transaction, BlockReference blockReference)
    {
        string surface = GetDynamicProperty(blockReference, "UC");
        surface = NormalizeSurface(surface);
        if (!string.IsNullOrWhiteSpace(surface)) return surface;
        ResultBuffer xdata = blockReference.GetXDataForApplication(XDataAppName);
        if (xdata != null)
        {
            TypedValue[] values = xdata.AsArray();
            for (int i = 0; i < values.Length - 1; i++)
            {
                if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, "MATERIAL", StringComparison.OrdinalIgnoreCase) && i + 6 < values.Length)
                    return NormalizeSurface(values[i + 6].Value == null ? string.Empty : values[i + 6].Value.ToString());
            }
        }
        Color color = GetEffectiveColor(transaction, blockReference);
        return GetSurfaceFromColor(color);
    }

    private static void GetMaterialXData(BlockReference blockReference, ref string description, ref string diameter, ref string surface)
    {
        ResultBuffer xdata = blockReference.GetXDataForApplication(XDataAppName);
        if (xdata == null) return;
        TypedValue[] values = xdata.AsArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].TypeCode != (int)DxfCode.ExtendedDataAsciiString || !string.Equals(values[i].Value as string, "MATERIAL", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < values.Length && !string.IsNullOrWhiteSpace(values[i + 1].Value?.ToString())) description = values[i + 1].Value.ToString().Trim();
            if (i + 2 < values.Length && !string.IsNullOrWhiteSpace(values[i + 2].Value?.ToString())) diameter = values[i + 2].Value.ToString().Trim();
            if (i + 6 < values.Length && !string.IsNullOrWhiteSpace(values[i + 6].Value?.ToString())) surface = NormalizeSurface(values[i + 6].Value.ToString());
            return;
        }
    }

    private static string GetDynamicProperty(BlockReference blockReference, string propertyName)
    {
        if (!blockReference.IsDynamicBlock) return string.Empty;
        foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection)
        {
            if (string.Equals(property.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase)) return property.Value == null ? string.Empty : property.Value.ToString().Trim();
        }
        return string.Empty;
    }

    private static string GetSurfaceFromColor(Color color)
    {
        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.ColorMethod == ColorMethod.ByAci && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && color.ColorMethod == ColorMethod.ByColor && color.Red == surface.Red.Value && color.Green == surface.Green.Value && color.Blue == surface.Blue.Value) return surface.Name;
        }
        return null;
    }

    private static string SetActivitySelection(string path, UcKey uc)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
        {
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
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
            if (sheetData == null) throw new InvalidDataException("La hoja no contiene sheetData.");
            XElement row = sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), "14", StringComparison.Ordinal));
            if (row == null) { row = new XElement(mainNs + "row", new XAttribute("r", "14")); sheetData.Add(row); }
            XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), TargetCell, StringComparison.OrdinalIgnoreCase));
            if (cell == null) { cell = new XElement(mainNs + "c", new XAttribute("r", TargetCell)); row.Add(cell); }
            XAttribute style = cell.Attribute("s");
            cell.RemoveNodes();
            cell.SetAttributeValue("t", "inlineStr");
            if (style != null) cell.SetAttributeValue("s", style.Value);
            cell.Add(new XElement(mainNs + "is", new XElement(mainNs + "t", new XAttribute("{http://www.w3.org/XML/1998/namespace}space", "preserve"), activity)));
            SaveXml(archive, worksheetPath, worksheetEntry, worksheet);
            SetWorkbookCalculationMode(archive, workbook, mainNs);
            RemoveCalculationChain(archive, workbookRels, packageRelNs);
            return activity;
        }
    }

    private static void SetMaterialQuantities(string path, string activity, Dictionary<MaterialKey, double> quantities)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update, false))
        {
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
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
            List<SourceMaterialRow> sourceRows = FindSourceMaterialsForActivity(archive, workbook, workbookRels, mainNs, relNs, packageRelNs, activity);
            if (sourceRows.Count == 0) throw new InvalidDataException("No se encontraron materiales base para la actividad seleccionada.");
            XElement worksheet = LoadXml(worksheetEntry);
            XElement sheetData = worksheet.Element(mainNs + "sheetData");
            if (sheetData == null) throw new InvalidDataException("La hoja no contiene sheetData.");
            for (int index = 0; index < sourceRows.Count && MaterialStartRow + index <= MaterialEndRow; index++)
            {
                int rowNumber = MaterialStartRow + index;
                XElement row = sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), rowNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                if (row == null) continue;
                XElement quantityCell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), MaterialQuantityColumn + rowNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
                if (quantityCell == null) { quantityCell = new XElement(mainNs + "c", new XAttribute("r", MaterialQuantityColumn + rowNumber.ToString(CultureInfo.InvariantCulture))); row.Add(quantityCell); }
                SourceMaterialRow source = sourceRows[index];
                MaterialKey matchedKey;
                double quantity;
                if (TryMatchSourceMaterial(source, quantities, out matchedKey, out quantity)) SetNumericCell(quantityCell, quantity, mainNs);
                else SetBlankCell(quantityCell, mainNs);
            }
            for (int rowNumber = MaterialStartRow + sourceRows.Count; rowNumber <= MaterialEndRow; rowNumber++)
            {
                XElement row = sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), rowNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                if (row == null) continue;
                XElement quantityCell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), MaterialQuantityColumn + rowNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
                if (quantityCell != null) SetBlankCell(quantityCell, mainNs);
            }
            SaveXml(archive, worksheetPath, worksheetEntry, worksheet);
            SetWorkbookCalculationMode(archive, workbook, mainNs);
            RemoveCalculationChain(archive, workbookRels, packageRelNs);
        }
    }

    private static List<SourceMaterialRow> FindSourceMaterialsForActivity(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, string activity)
    {
        var result = new List<SourceMaterialRow>();
        XElement sheets = workbook.Element(mainNs + "sheets"); if (sheets == null) return result;
        XElement sourceSheet = sheets.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), "materiales leg", StringComparison.OrdinalIgnoreCase)); if (sourceSheet == null) return result;
        string sourceRelId = (string)sourceSheet.Attribute(relNs + "id");
        XElement sourceRel = workbookRels.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), sourceRelId, StringComparison.Ordinal)); if (sourceRel == null) return result;
        string sourcePath = ResolveZipPath("xl/workbook.xml", (string)sourceRel.Attribute("Target"));
        ZipArchiveEntry sourceEntry = archive.GetEntry(sourcePath); if (sourceEntry == null) return result;
        XElement sourceXml = LoadXml(sourceEntry); XElement sheetData = sourceXml.Element(mainNs + "sheetData"); if (sheetData == null) return result;
        Dictionary<int, string> sharedStrings = LoadSharedStrings(archive, mainNs);
        string activityCode = string.Empty;
        foreach (XElement row in sheetData.Elements(mainNs + "row"))
        {
            string value = ReadColumnText(row, "A", mainNs, sharedStrings);
            if (NormalizeActivityText(value) == NormalizeActivityText(activity)) { activityCode = ReadColumnText(row, "B", mainNs, sharedStrings); break; }
        }
        if (string.IsNullOrWhiteSpace(activityCode)) return result;
        foreach (XElement row in sheetData.Elements(mainNs + "row"))
        {
            int rowNumber; if (!int.TryParse((string)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out rowNumber)) continue;
            if (rowNumber < MaterialStartRow || rowNumber > MaterialEndRow) continue;
            string code = ReadColumnText(row, "G", mainNs, sharedStrings);
            if (!string.Equals(code.Trim(), activityCode.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new SourceMaterialRow(rowNumber, code, ReadColumnText(row, "J", mainNs, sharedStrings), ReadColumnText(row, "K", mainNs, sharedStrings)));
        }
        return result;
    }

    private static bool TryMatchSourceMaterial(SourceMaterialRow source, Dictionary<MaterialKey, double> quantities, out MaterialKey matchedKey, out double quantity)
    {
        matchedKey = default(MaterialKey); quantity = 0.0;
        foreach (KeyValuePair<MaterialKey, double> item in quantities)
        {
            if (!string.IsNullOrWhiteSpace(source.Code) && !string.IsNullOrWhiteSpace(item.Key.Code) && string.Equals(source.Code.Trim(), item.Key.Code.Trim(), StringComparison.OrdinalIgnoreCase)) { matchedKey = item.Key; quantity = item.Value; return true; }
        }
        foreach (KeyValuePair<MaterialKey, double> item in quantities)
        {
            if (NormalizeToken(source.Description) == NormalizeToken(item.Key.Description) && NormalizeDiameter(source.Diameter) == NormalizeDiameter(item.Key.Diameter)) { matchedKey = item.Key; quantity = item.Value; return true; }
        }
        return false;
    }

    private static void SetNumericCell(XElement cell, double value, XNamespace mainNs)
    {
        XAttribute style = cell.Attribute("s");
        cell.RemoveNodes();
        cell.SetAttributeValue("t", null);
        if (style != null) cell.SetAttributeValue("s", style.Value);
        XElement valueElement = new XElement(mainNs + "v", value.ToString("0.###", CultureInfo.InvariantCulture));
        cell.Add(valueElement);
    }

    private static void SetBlankCell(XElement cell, XNamespace mainNs)
    {
        XAttribute style = cell.Attribute("s");
        cell.RemoveNodes();
        cell.SetAttributeValue("t", null);
        if (style != null) cell.SetAttributeValue("s", style.Value);
    }

    private static string GetDropdownSurfaceToken(string surface) => string.Equals(surface, "ASFALTO", StringComparison.OrdinalIgnoreCase) ? "CALZADA ASFALTO" : surface;
    private static string NormalizeActivityText(string value) => NormalizeToken(value);
    private static string NormalizeToken(string value) { if (string.IsNullOrWhiteSpace(value)) return string.Empty; return Regex.Replace(value.Trim().ToUpperInvariant(), @"[^A-Z0-9]", string.Empty); }
    private static string NormalizeDiameter(string value) { if (string.IsNullOrWhiteSpace(value)) return string.Empty; return value.Trim().ToUpperInvariant().Replace("\"", string.Empty).Replace("PULGADAS", string.Empty).Replace("PULG", string.Empty).Replace(" ", string.Empty); }
    private static string NormalizeSurface(string value) { if (string.IsNullOrWhiteSpace(value)) return string.Empty; string normalized = value.Trim().ToUpperInvariant(); normalized = Regex.Replace(normalized, @"\s+", " "); if (normalized == "CALZADA ASFALTO") normalized = "ASFALTO"; return normalized; }

    private static Dictionary<int, string> LoadSharedStrings(ZipArchive archive, XNamespace mainNs)
    {
        var result = new Dictionary<int, string>(); string xml = ReadZipEntry(archive, "xl/sharedStrings.xml"); if (string.IsNullOrWhiteSpace(xml)) return result; XDocument doc = XDocument.Parse(xml); int i = 0; foreach (XElement si in doc.Descendants(mainNs + "si")) { result[i++] = string.Concat(si.Descendants(mainNs + "t").Select(x => x.Value)); } return result;
    }

    private static string ReadColumnText(XElement row, string column, XNamespace mainNs, Dictionary<int, string> sharedStrings)
    {
        XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => Regex.IsMatch((string)x.Attribute("r") ?? string.Empty, "^" + Regex.Escape(column) + "\\d+$", RegexOptions.IgnoreCase));
        return cell == null ? string.Empty : ReadCellText(cell, mainNs, sharedStrings);
    }

    private static string ReadCellText(XElement cell, XNamespace mainNs, Dictionary<int, string> sharedStrings)
    {
        string type = (string)cell.Attribute("t") ?? string.Empty;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase)) return string.Concat(cell.Descendants(mainNs + "t").Select(x => x.Value));
        XElement value = cell.Element(mainNs + "v"); if (value == null) return string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase)) { int index; return int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && sharedStrings.TryGetValue(index, out string text) ? text : string.Empty; }
        return value.Value;
    }

    private static string FindDropdownActivity(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, UcKey uc)
    {
        XElement definedNames = workbook.Element(mainNs + "definedNames"); XElement definedName = definedNames == null ? null : definedNames.Elements(mainNs + "definedName").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), ActivityDefinedName, StringComparison.OrdinalIgnoreCase)); if (definedName == null) throw new InvalidDataException("No se encontró el nombre definido 'ACTIVIDAD'.");
        Match match = Regex.Match(definedName.Value.Trim(), @"^'?((?:[^']|'')+)'?!\$?([A-Z]+)\$?(\d+):\$?([A-Z]+)\$?(\d+)$", RegexOptions.IgnoreCase); if (!match.Success) throw new InvalidDataException("No se pudo interpretar el rango ACTIVIDAD.");
        string sourceSheetName = match.Groups[1].Value.Replace("''", "'"); string column = match.Groups[2].Value; int startRow = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture); int endRow = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        XElement sheets = workbook.Element(mainNs + "sheets"); XElement sourceSheet = sheets == null ? null : sheets.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), sourceSheetName, StringComparison.OrdinalIgnoreCase)); if (sourceSheet == null) throw new InvalidDataException("No se encontró la hoja origen de ACTIVIDAD.");
        string sourceRelId = (string)sourceSheet.Attribute(relNs + "id"); XElement sourceRel = workbookRels.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), sourceRelId, StringComparison.Ordinal)); if (sourceRel == null) throw new InvalidDataException("No se encontró la relación de la hoja origen de ACTIVIDAD.");
        string sourcePath = ResolveZipPath("xl/workbook.xml", (string)sourceRel.Attribute("Target")); ZipArchiveEntry sourceEntry = archive.GetEntry(sourcePath); if (sourceEntry == null) throw new InvalidDataException("No se encontró la hoja origen de ACTIVIDAD.");
        XElement sourceXml = LoadXml(sourceEntry); XElement sheetData = sourceXml.Element(mainNs + "sheetData"); if (sheetData == null) return null; Dictionary<int, string> sharedStrings = LoadSharedStrings(archive, mainNs);
        string diameterToken = NormalizeActivityText(uc.Diameter + " PULG"); string surfaceToken = NormalizeActivityText(GetDropdownSurfaceToken(uc.Surface));
        for (int r = startRow; r <= endRow; r++)
        {
            XElement row = sheetData.Elements(mainNs + "row").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), r.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)); if (row == null) continue;
            XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), column + r.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)); if (cell == null) continue;
            string value = ReadCellText(cell, mainNs, sharedStrings); string normalized = NormalizeActivityText(value);
            if (normalized.IndexOf(diameterToken, StringComparison.Ordinal) >= 0 && normalized.IndexOf(surfaceToken, StringComparison.Ordinal) >= 0) return value;
        }
        return null;
    }

    private static void SetWorkbookCalculationMode(ZipArchive archive, XElement workbook, XNamespace mainNs)
    {
        XElement calcPr = workbook.Element(mainNs + "calcPr"); if (calcPr == null) { calcPr = new XElement(mainNs + "calcPr"); workbook.Add(calcPr); }
        calcPr.SetAttributeValue("calcMode", "auto"); calcPr.SetAttributeValue("fullCalcOnLoad", "1"); calcPr.SetAttributeValue("forceFullCalc", "1"); calcPr.SetAttributeValue("calcOnSave", "1");
        ZipArchiveEntry entry = archive.GetEntry("xl/workbook.xml"); SaveXml(archive, "xl/workbook.xml", entry, workbook);
    }

    private static void RemoveCalculationChain(ZipArchive archive, XElement workbookRels, XNamespace packageRelNs)
    {
        ZipArchiveEntry chain = archive.GetEntry("xl/calcChain.xml"); if (chain != null) chain.Delete();
        foreach (XElement rel in workbookRels.Elements(packageRelNs + "Relationship").Where(x => string.Equals((string)x.Attribute("Type"), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain", StringComparison.OrdinalIgnoreCase) || string.Equals((string)x.Attribute("Target"), "calcChain.xml", StringComparison.OrdinalIgnoreCase)).ToList()) rel.Remove();
        ZipArchiveEntry relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels"); SaveXml(archive, "xl/_rels/workbook.xml.rels", relEntry, workbookRels);
        ZipArchiveEntry contentEntry = archive.GetEntry("[Content_Types].xml");
        if (contentEntry != null)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types"; XElement content = LoadXml(contentEntry);
            foreach (XElement item in content.Elements(ns + "Override").Where(x => string.Equals((string)x.Attribute("PartName"), "/xl/calcChain.xml", StringComparison.OrdinalIgnoreCase)).ToList()) item.Remove();
            SaveXml(archive, "[Content_Types].xml", contentEntry, content);
        }
    }

    private static XElement LoadXml(ZipArchiveEntry entry) { using (Stream stream = entry.Open()) return XElement.Load(stream, LoadOptions.PreserveWhitespace); }
    private static void SaveXml(ZipArchive archive, string entryName, ZipArchiveEntry oldEntry, XElement document) { if (oldEntry != null) oldEntry.Delete(); ZipArchiveEntry newEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal); using (Stream stream = newEntry.Open()) document.Save(stream, SaveOptions.DisableFormatting); }
    private static string ReadZipEntry(ZipArchive archive, string path) { ZipArchiveEntry entry = archive.GetEntry(path); if (entry == null) return string.Empty; using (Stream stream = entry.Open()) using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd(); }
    private static string ResolveZipPath(string basePath, string target)
    {
        string baseDirectory = Path.GetDirectoryName(basePath); string combined = string.IsNullOrWhiteSpace(baseDirectory) ? target : baseDirectory.Replace('\\', '/') + "/" + target; var parts = new List<string>();
        foreach (string part in combined.Replace('\\', '/').Split('/')) { if (part.Length == 0 || part == ".") continue; if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; } parts.Add(part); }
        return string.Join("/", parts);
    }

    private static string GetBlockName(Transaction transaction, BlockReference blockReference)
    {
        ObjectId definitionId = blockReference.BlockTableRecord; if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull) definitionId = blockReference.DynamicBlockTableRecord;
        BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord; return definition == null ? string.Empty : definition.Name;
    }

    private static string GetDiameter(BlockReference blockReference)
    {
        if (!blockReference.IsDynamicBlock) return string.Empty;
        foreach (DynamicBlockReferenceProperty property in blockReference.DynamicBlockReferencePropertyCollection) if (string.Equals(property.PropertyName, "DIAMETRO", StringComparison.OrdinalIgnoreCase)) return property.Value == null ? string.Empty : property.Value.ToString().Trim();
        return string.Empty;
    }

    private static string GetSuggestedFileName(UcKey uc)
    {
        string surfaceCode; if (!SurfaceFileCode.TryGetValue(uc.Surface, out surfaceCode)) surfaceCode = "UC"; string diameterCode = uc.Diameter == "1/2" ? "1-2" : "3-4"; return surfaceCode + " " + diameterCode + " PULG.xlsx";
    }

    private static string EnsureXlsxExtension(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? path : path + ".xlsx";
    private static string ToDisplaySurface(string surface) => surface == "ANDEN TABLETA" ? "ANDÉN TABLETA, BALDOSÍN, GRAVILLA" : surface;
    private static int GetSurfaceOrder(string surface) { int i = Array.FindIndex(SurfaceOrder, x => string.Equals(x, surface, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
    private static int DiameterOrder(string diameter) => diameter == "1/2" ? 0 : 1;

    private struct UcKey : IEquatable<UcKey>
    {
        public UcKey(string diameter, string surface) { Diameter = NormalizeDiameter(diameter); Surface = NormalizeSurface(surface); }
        public string Diameter { get; private set; }
        public string Surface { get; private set; }
        public bool Equals(UcKey other) => string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase) && string.Equals(Surface, other.Surface, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is UcKey && Equals((UcKey)obj);
        public override int GetHashCode() { unchecked { return (StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty) * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Surface ?? string.Empty); } }
    }

    private struct MaterialKey : IEquatable<MaterialKey>
    {
        public MaterialKey(string description, string diameter, string unit, string code) { Description = NormalizeToken(description); Diameter = NormalizeDiameter(diameter); Unit = string.IsNullOrWhiteSpace(unit) ? "UND" : unit.Trim(); Code = code == null ? string.Empty : code.Trim(); }
        public string Description { get; private set; }
        public string Diameter { get; private set; }
        public string Unit { get; private set; }
        public string Code { get; private set; }
        public bool Equals(MaterialKey other) => string.Equals(Description, other.Description, StringComparison.OrdinalIgnoreCase) && string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase) && string.Equals(Unit, other.Unit, StringComparison.OrdinalIgnoreCase) && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => obj is MaterialKey && Equals((MaterialKey)obj);
        public override int GetHashCode() { unchecked { int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Description ?? string.Empty); hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty); hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Unit ?? string.Empty); return hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Code ?? string.Empty); } }
    }

    private sealed class UcSurface
    {
        public UcSurface(string name, int? colorIndex, byte? red, byte? green, byte? blue) { Name = name; ColorIndex = colorIndex; Red = red; Green = green; Blue = blue; }
        public string Name { get; private set; }
        public int? ColorIndex { get; private set; }
        public byte? Red { get; private set; }
        public byte? Green { get; private set; }
        public byte? Blue { get; private set; }
    }

    private sealed class MaterialSpec
    {
        public MaterialSpec(string description, string diameter, string code) { Description = description; Diameter = diameter; Code = code; }
        public string Description { get; private set; }
        public string Diameter { get; private set; }
        public string Code { get; private set; }
    }

    private sealed class SourceMaterialRow
    {
        public SourceMaterialRow(int row, string code, string description, string diameter) { Row = row; Code = code; Description = description; Diameter = diameter; }
        public int Row { get; private set; }
        public string Code { get; private set; }
        public string Description { get; private set; }
        public string Diameter { get; private set; }
    }
}
