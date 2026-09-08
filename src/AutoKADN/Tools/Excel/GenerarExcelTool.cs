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
            MergeMaterialQuantities(materialQuantities, ConvertUcTotalsToPipeMaterials(ucPipeTotals));
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

                    string description = GetBlockName(transaction, blockReference);
                    string blockDiameter = GetDiameter(blockReference);
                    string surface = GetBlockSurface(transaction, blockReference);

                    // XData MATERIAL es una fuente válida para bloques existentes creados por versiones anteriores.
                    GetMaterialXData(blockReference, ref description, ref blockDiameter, ref surface);

                    description = description == null ? string.Empty : description.Trim();
                    blockDiameter = blockDiameter == null ? string.Empty : blockDiameter.Trim();
                    surface = NormalizeSurface(surface);
                    if (string.IsNullOrWhiteSpace(surface)) continue;

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
                    ResultBuffer xdata = mtext.GetXDataForApplication(XDataAppName);
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, SpiralXDataType, StringComparison.OrdinalIgnoreCase)) { typeIndex = i; break; }
                    }
                    if (typeIndex < 0) continue;
                    int index = typeIndex + 1;
                    if (index + 7 >= values.Length) continue;

                    double pipe, unions, tees, valves, saddles;
                    if (!TryReadXDataDouble(values[index].Value, out pipe) ||
                        !TryReadXDataDouble(values[index + 1].Value, out unions) ||
                        !TryReadXDataDouble(values[index + 2].Value, out tees) ||
                        !TryReadXDataDouble(values[index + 3].Value, out valves) ||
                        !TryReadXDataDouble(values[index + 4].Value, out saddles)) continue;

                    string saddleDiameter = values[index + 5].Value == null ? string.Empty : values[index + 5].Value.ToString().Trim();
                    string surface = NormalizeSurface(values[index + 7].Value == null ? string.Empty : values[index + 7].Value.ToString());
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
                    ResultBuffer xdata = mtext.GetXDataForApplication(XDataAppName);
                    if (xdata == null) continue;
                    TypedValue[] values = xdata.AsArray();
                    int typeIndex = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, MaterialTestXDataType, StringComparison.OrdinalIgnoreCase)) { typeIndex = i; break; }
                    }
                    if (typeIndex < 0) continue;

                    // MATERIAL_PRUEBA:
                    // [MATERIAL_PRUEBA, layout, guid, nombre, diametro, unidad, cantidad, ucDiametro, terreno] x N
                    int index = typeIndex + 3;
                    while (index + 5 < values.Length)
                    {
                        string description = values[index].Value == null ? string.Empty : values[index].Value.ToString().Trim();
                        string diameter = values[index + 1].Value == null ? string.Empty : values[index + 1].Value.ToString().Trim();
                        string unit = values[index + 2].Value == null ? string.Empty : values[index + 2].Value.ToString().Trim();
                        double quantity;
                        if (!TryReadXDataDouble(values[index + 3].Value, out quantity)) break;
                        string ucDiameter = NormalizeDiameter(values[index + 4].Value == null ? string.Empty : values[index + 4].Value.ToString());
                        string surface = NormalizeSurface(values[index + 5].Value == null ? string.Empty : values[index + 5].Value.ToString());

                        MaterialSpec material;
                        if (TryGetMaterialSpec(description, diameter, out material) && (ucDiameter == "1/2" || ucDiameter == "3/4") && !string.IsNullOrWhiteSpace(surface))
                            AddMaterialQuantity(result, new UcKey(ucDiameter, surface), material, unit, Math.Abs(quantity));

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
        // Fuente principal: propiedad dinámica UC del bloque.
        string surface = NormalizeSurface(GetDynamicProperty(blockReference, "UC"));
        if (!string.IsNullOrWhiteSpace(surface)) return surface;

        // Compatibilidad: XData MATERIAL de bloques creados por versiones anteriores.
        ResultBuffer xdata = blockReference.GetXDataForApplication(XDataAppName);
        if (xdata != null)
        {
            TypedValue[] values = xdata.AsArray();
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, "MATERIAL", StringComparison.OrdinalIgnoreCase) && i + 6 < values.Length)
                    return NormalizeSurface(values[i + 6].Value == null ? string.Empty : values[i + 6].Value.ToString());
            }
        }

        return null;
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
            if (string.Equals(property.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase)) return property.Value == null ? string.Empty : property.Value.ToString().Trim();
        return string.Empty;
    }

    private static bool IsUcLayout(string name) => Regex.IsMatch(name, @"^ANILLO\s+\d+\s+UC$", RegexOptions.IgnoreCase);
    private static bool IsDetailLayout(string name) => Regex.IsMatch(name, @"^ANILLO\s+\d+\s+DETALLE$", RegexOptions.IgnoreCase);
    private static string GetUcDiameter(string layer) { if (string.Equals(layer, UcLayerHalf, StringComparison.OrdinalIgnoreCase)) return "1/2"; if (string.Equals(layer, UcLayerThreeQuarter, StringComparison.OrdinalIgnoreCase)) return "3/4"; return null; }

    private static string GetSurface(Transaction transaction, Dimension dimension)
    {
        string surfaceFromXData = GetSurfaceFromXData(dimension);
        if (surfaceFromXData != null) return surfaceFromXData;
        Color color = GetEffectiveColor(transaction, dimension);
        return GetSurfaceFromColor(color);
    }

    private static string GetSurfaceFromXData(DBObject entity)
    {
        ResultBuffer xdata = entity.GetXDataForApplication(XDataAppName);
        if (xdata == null) return null;
        TypedValue[] values = xdata.AsArray();
        for (int i = 0; i < values.Length - 1; i++)
            if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString && string.Equals(values[i].Value as string, UcSurfaceXDataType, StringComparison.OrdinalIgnoreCase)) return NormalizeSurface(values[i + 1].Value as string);
        return null;
    }

    private static string GetSurfaceFromColor(Color color)
    {
        foreach (UcSurface surface in Surfaces)
        {
            if (surface.ColorIndex.HasValue && color.ColorMethod == ColorMethod.ByAci && color.ColorIndex == surface.ColorIndex.Value) return surface.Name;
            if (surface.Red.HasValue && color.ColorMethod == ColorMethod.TrueColor && color.Red == surface.Red.Value && color.Green == surface.Green.Value && color.Blue == surface.Blue.Value) return surface.Name;
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

    private static string SetActivitySelection(string path, UcKey uc)
    {
        string activity = FindDropdownActivity(path, uc);
        if (string.IsNullOrWhiteSpace(activity)) return string.Empty;
        UpdateCellValue(path, TargetSheetName, TargetCell, activity);
        return activity;
    }

    private static void SetMaterialQuantities(string path, string activity, Dictionary<MaterialKey, double> quantities)
    {
        if (string.IsNullOrWhiteSpace(activity) || quantities == null || quantities.Count == 0) return;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            string workbookXml = ReadZipEntry(archive, "xl/workbook.xml");
            string relsXml = ReadZipEntry(archive, "xl/_rels/workbook.xml.rels");
            if (string.IsNullOrWhiteSpace(workbookXml) || string.IsNullOrWhiteSpace(relsXml)) return;
            XNamespace mainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
            XDocument workbook = XDocument.Parse(workbookXml);
            XDocument workbookRels = XDocument.Parse(relsXml);
            List<SourceMaterialRow> sourceRows = FindSourceMaterialsForActivity(archive, workbook, workbookRels, mainNs, relNs, packageRelNs, activity);
            if (sourceRows.Count == 0) return;
            foreach (SourceMaterialRow row in sourceRows)
            {
                double total;
                MaterialKey matchedKey;
                if (!TryMatchSourceMaterial(row, quantities, out matchedKey, out total)) continue;
                XElement cell = LoadSheetCellForWrite(archive, workbook, workbookRels, mainNs, relNs, packageRelNs, TargetSheetName, MaterialQuantityColumn + row.Row.ToString(CultureInfo.InvariantCulture));
                if (cell == null) continue;
                SetNumericCell(cell, total, mainNs);
            }
            SetWorkbookCalculationMode(archive, workbook, mainNs);
            UpdateWorkbookXml(archive, "xl/workbook.xml", workbook);
        }
    }

    private static List<SourceMaterialRow> FindSourceMaterialsForActivity(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, string activity)
    {
        var rows = new List<SourceMaterialRow>();
        XElement sheet = workbook.Root?.Element(mainNs + "sheets")?.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), "materiales leg", StringComparison.OrdinalIgnoreCase));
        if (sheet == null) return rows;
        string relId = (string)sheet.Attribute(relNs + "id");
        string target = workbookRels.Root?.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target)) return rows;
        string sheetPath = ResolveZipPath("xl/workbook.xml", target);
        string sheetXml = ReadZipEntry(archive, sheetPath);
        if (string.IsNullOrWhiteSpace(sheetXml)) return rows;
        XDocument sheetDoc = XDocument.Parse(sheetXml);
        Dictionary<int, string> sharedStrings = LoadSharedStrings(archive, mainNs);
        string activityCode = string.Empty;
        foreach (XElement row in sheetDoc.Descendants(mainNs + "row"))
        {
            string a = ReadColumnText(row, "A", mainNs, sharedStrings);
            if (NormalizeActivityText(a) != NormalizeActivityText(activity)) continue;
            activityCode = ReadColumnText(row, "B", mainNs, sharedStrings);
            break;
        }
        if (string.IsNullOrWhiteSpace(activityCode)) return rows;
        foreach (XElement row in sheetDoc.Descendants(mainNs + "row"))
        {
            if (!int.TryParse((string)row.Attribute("r"), out int rowNumber) || rowNumber < MaterialStartRow || rowNumber > MaterialEndRow) continue;
            string code = ReadColumnText(row, "G", mainNs, sharedStrings);
            if (!string.Equals(code.Trim(), activityCode.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new SourceMaterialRow(rowNumber, ReadColumnText(row, "J", mainNs, sharedStrings), ReadColumnText(row, "K", mainNs, sharedStrings), ReadColumnText(row, "L", mainNs, sharedStrings)));
        }
        return rows;
    }

    private static bool TryMatchSourceMaterial(SourceMaterialRow source, Dictionary<MaterialKey, double> quantities, out MaterialKey matchedKey, out double quantity)
    {
        matchedKey = default; quantity = 0.0;
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
        cell.SetAttributeValue("t", null);
        XElement valueElement = cell.Element(mainNs + "v");
        if (valueElement == null) { valueElement = new XElement(mainNs + "v"); cell.Add(valueElement); }
        valueElement.Value = value.ToString("0.###", CultureInfo.InvariantCulture);
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
        XElement cell = row.Elements(mainNs + "c").FirstOrDefault(x => string.Equals(((string)x.Attribute("r"))?.TrimStart('0').TrimEnd('0'), column, StringComparison.OrdinalIgnoreCase) || Regex.IsMatch((string)x.Attribute("r") ?? string.Empty, "^" + Regex.Escape(column) + "\\d+$", RegexOptions.IgnoreCase)); return cell == null ? string.Empty : ReadCellText(cell, mainNs, sharedStrings);
    }
    private static string ReadCellText(XElement cell, XNamespace mainNs, Dictionary<int, string> sharedStrings) { string type = (string)cell.Attribute("t") ?? string.Empty; string value = cell.Element(mainNs + "v")?.Value ?? string.Empty; if (type == "s" && int.TryParse(value, out int index) && sharedStrings.TryGetValue(index, out string text)) return text; if (type == "inlineStr") return string.Concat(cell.Descendants(mainNs + "t").Select(x => x.Value)); return value; }

    private static string FindDropdownActivity(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, UcKey uc)
    {
        XElement definedNames = workbook.Root?.Element(mainNs + "definedNames"); XElement definedName = definedNames?.Elements(mainNs + "definedName").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), ActivityDefinedName, StringComparison.OrdinalIgnoreCase)); if (definedName == null) return string.Empty; string formula = definedName.Value.Trim(); Match match = Regex.Match(formula, @"'([^']+)'!\$([A-Z]+)\$([0-9]+):\$([A-Z]+)\$([0-9]+)"); if (!match.Success) return string.Empty; string sheetName = match.Groups[1].Value; string startColumn = match.Groups[2].Value; int startRow = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture); string endColumn = match.Groups[4].Value; int endRow = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture); XElement sheet = workbook.Root?.Element(mainNs + "sheets")?.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase)); if (sheet == null) return string.Empty; string relId = (string)sheet.Attribute(relNs + "id"); string target = workbookRels.Root?.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value; if (string.IsNullOrWhiteSpace(target)) return string.Empty; string sheetPath = ResolveZipPath("xl/workbook.xml", target); string sheetXml = ReadZipEntry(archive, sheetPath); if (string.IsNullOrWhiteSpace(sheetXml)) return string.Empty; XDocument sheetDoc = XDocument.Parse(sheetXml); Dictionary<int, string> sharedStrings = LoadSharedStrings(archive, mainNs); foreach (XElement row in sheetDoc.Descendants(mainNs + "row")) { if (!int.TryParse((string)row.Attribute("r"), out int rowNumber) || rowNumber < startRow || rowNumber > endRow) continue; string surface = ReadColumnText(row, "A", mainNs, sharedStrings); string diameter = ReadColumnText(row, "B", mainNs, sharedStrings); if (NormalizeToken(surface) == NormalizeToken(GetDropdownSurfaceToken(uc.Surface)) && NormalizeDiameter(diameter) == NormalizeDiameter(uc.Diameter)) return ReadColumnText(row, startColumn, mainNs, sharedStrings); } return string.Empty;
    }

    private static void SetWorkbookCalculationMode(ZipArchive archive, XElement workbook, XNamespace mainNs) { XElement calcPr = workbook.Root?.Element(mainNs + "calcPr"); if (calcPr == null) { calcPr = new XElement(mainNs + "calcPr"); workbook.Root?.Add(calcPr); } calcPr.SetAttributeValue("calcMode", "auto"); calcPr.SetAttributeValue("fullCalcOnLoad", "1"); calcPr.SetAttributeValue("forceFullCalc", "1"); }
    private static void UpdateWorkbookXml(ZipArchive archive, string entryName, XElement document) { ZipArchiveEntry entry = archive.GetEntry(entryName); if (entry == null) return; entry.Delete(); ZipArchiveEntry newEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal); using (Stream stream = newEntry.Open()) document.Save(stream, SaveOptions.DisableFormatting); }
    private static XElement LoadSheetCellForWrite(ZipArchive archive, XElement workbook, XElement workbookRels, XNamespace mainNs, XNamespace relNs, XNamespace packageRelNs, string sheetName, string cellRef) { XElement sheet = workbook.Root?.Element(mainNs + "sheets")?.Elements(mainNs + "sheet").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase)); if (sheet == null) return null; string relId = (string)sheet.Attribute(relNs + "id"); string target = workbookRels.Root?.Elements(packageRelNs + "Relationship").FirstOrDefault(x => string.Equals((string)x.Attribute("Id"), relId, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value; if (string.IsNullOrWhiteSpace(target)) return null; string path = ResolveZipPath("xl/workbook.xml", target); ZipArchiveEntry entry = archive.GetEntry(path); if (entry == null) return null; XDocument doc; using (Stream stream = entry.Open()) doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace); XElement cell = doc.Descendants(mainNs + "c").FirstOrDefault(x => string.Equals((string)x.Attribute("r"), cellRef, StringComparison.OrdinalIgnoreCase)); return cell; }
    private static string ReadZipEntry(ZipArchive archive, string name) { ZipArchiveEntry entry = archive.GetEntry(name); if (entry == null) return string.Empty; using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8, true)) return reader.ReadToEnd(); }
    private static string ResolveZipPath(string basePath, string target) { string baseDirectory = Path.GetDirectoryName(basePath).Replace('\\', '/'); string combined = (baseDirectory + "/" + target).Replace('\\', '/'); var parts = new List<string>(); foreach (string part in combined.Split('/')) { if (part == "" || part == ".") continue; if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1); else if (part != "..") parts.Add(part); } return string.Join("/", parts); }
    private static string GetBlockName(Transaction transaction, BlockReference blockReference) { ObjectId definitionId = blockReference.BlockTableRecord; if (blockReference.IsDynamicBlock && !blockReference.DynamicBlockTableRecord.IsNull) definitionId = blockReference.DynamicBlockTableRecord; BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead) as BlockTableRecord; return definition?.Name ?? string.Empty; }
    private static string GetDiameter(BlockReference blockReference) { return GetDynamicProperty(blockReference, "DIAMETRO"); }
    private static string GetSuggestedFileName(UcKey uc) { string diameter = uc.Diameter.Replace('/', '-'); string code = SurfaceFileCode.TryGetValue(uc.Surface, out string value) ? value : NormalizeToken(uc.Surface); return "Formato_" + diameter + "_" + code + ".xlsx"; }
    private static string EnsureXlsxExtension(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? path : path + ".xlsx";
    private static string ToDisplaySurface(string surface) => surface == "ANDEN TABLETA" ? "ANDÉN TABLETA, BALDOSÍN, GRAVILLA" : surface;
    private static int GetSurfaceOrder(string surface) { int i = Array.FindIndex(SurfaceOrder, x => string.Equals(x, surface, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
    private static int DiameterOrder(string diameter) => diameter == "1/2" ? 0 : 1;

    private sealed record UcSurface(string Name, int? ColorIndex, byte? Red, byte? Green, byte? Blue);
    private sealed record MaterialSpec(string Description, string Diameter, string Code);
    private readonly record struct UcKey(string Diameter, string Surface);
    private readonly record struct MaterialKey(string Description, string Diameter, string Unit, string Code);
    private sealed record SourceMaterialRow(int Row, string Code, string Description, string Diameter);
}
