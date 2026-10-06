using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;

namespace AutoKADN.Tools.Excel;

// Resultado de leer el contratista: el nombre y el layout de donde salió, o por qué no se encontró.
internal sealed class ContractorReading
{
    public string Name = string.Empty;
    public string Layout = string.Empty;
    public string Problem = string.Empty;
}

// Nombre de la empresa contratista: está en el cajetín de los planos de detalles, justo arriba del rótulo
// "PLANO DE DETALLES". Se lee del primer layout de detalles (en el orden de las pestañas) donde aparezca.
// Aquí solo se juntan los textos del layout (sueltos, atributos y bloques anidados) con su posición en el papel;
// qué texto es el nombre y cómo se recorta ("hasta el primer punto") lo decide Core/ContratistaRules.
internal static class ContratistaReader
{
    private static readonly Regex DetailLayout = new Regex(@"^(ANILLO\s+\d+\s+DETALLE|TRONCAL\s+DETALLE)$", RegexOptions.IgnoreCase);
    private const int MaxBlockDepth = 3;

    internal static ContractorReading Read(Database database)
    {
        var reading = new ContractorReading();
        int checkedLayouts = 0;
        string anchorLayout = string.Empty;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            DBDictionary layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            var details = new List<Layout>();
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout != null && DetailLayout.IsMatch(layout.LayoutName.Trim())) details.Add(layout);
            }
            foreach (Layout layout in details.OrderBy(l => l.TabOrder))
            {
                var texts = new List<PlanText>();
                Collect(transaction, layout.BlockTableRecordId, Matrix3d.Identity, texts, 0);
                checkedLayouts++;
                string name = ContratistaRules.FindContractor(texts);
                if (name.Length > 0)
                {
                    reading.Name = name;
                    reading.Layout = layout.LayoutName.Trim();
                    break;
                }
                if (anchorLayout.Length == 0 && ContratistaRules.HasAnchor(texts)) anchorLayout = layout.LayoutName.Trim();
            }
            transaction.Commit();
        }

        if (reading.Name.Length == 0)
            reading.Problem = checkedLayouts == 0
                ? "no hay layouts ANILLO n DETALLE ni TRONCAL DETALLE"
                : anchorLayout.Length == 0
                    ? "no se encontró el rótulo \"PLANO DE DETALLES\" en los " + checkedLayouts + " layout(s) de detalles"
                    : "se encontró \"PLANO DE DETALLES\" en " + anchorLayout + " pero no hay un texto con el nombre justo arriba";
        return reading;
    }

    // Textos del espacio (layout o definición de bloque) en coordenadas del papel: toPaper lleva las coordenadas
    // de este espacio al layout.
    private static void Collect(Transaction transaction, ObjectId spaceId, Matrix3d toPaper, List<PlanText> texts, int depth)
    {
        var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForRead);
        Vector3d xAxis = toPaper.CoordinateSystem3d.Xaxis;
        double frameRotation = Math.Atan2(xAxis.Y, xAxis.X);
        foreach (ObjectId id in space)
        {
            Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
            if (entity == null) continue;

            var mtext = entity as MText;
            if (mtext != null) { Add(texts, mtext.Contents, entity, toPaper, mtext.Rotation + frameRotation); continue; }

            // Un atributo no constante muestra el valor de su referencia (abajo); solo el constante vive en la definición.
            var definition = entity as AttributeDefinition;
            if (definition != null)
            {
                if (definition.Constant) Add(texts, definition.TextString, entity, toPaper, definition.Rotation + frameRotation);
                continue;
            }

            var text = entity as DBText;
            if (text != null) { Add(texts, text.TextString, entity, toPaper, text.Rotation + frameRotation); continue; }

            var reference = entity as BlockReference;
            if (reference == null) continue;
            foreach (ObjectId attributeId in reference.AttributeCollection)
            {
                var attribute = transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                if (attribute != null && !attribute.Invisible)
                    Add(texts, attribute.TextString, attribute, toPaper, attribute.Rotation + frameRotation);
            }
            if (depth >= MaxBlockDepth) continue;
            var block = (BlockTableRecord)transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
            if (block.IsFromExternalReference) continue;
            Collect(transaction, reference.BlockTableRecord, toPaper * reference.BlockTransform, texts, depth + 1);
        }
    }

    private static void Add(List<PlanText> texts, string contents, Entity entity, Matrix3d toPaper, double rotation)
    {
        if (string.IsNullOrWhiteSpace(contents)) return;
        try
        {
            Extents3d bounds = entity.GeometricExtents;
            Point3d[] corners =
            {
                new Point3d(bounds.MinPoint.X, bounds.MinPoint.Y, 0.0), new Point3d(bounds.MaxPoint.X, bounds.MinPoint.Y, 0.0),
                new Point3d(bounds.MinPoint.X, bounds.MaxPoint.Y, 0.0), new Point3d(bounds.MaxPoint.X, bounds.MaxPoint.Y, 0.0),
            };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point3d corner in corners)
            {
                Point3d point = corner.TransformBy(toPaper);
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            }
            texts.Add(new PlanText(contents, minX, minY, maxX, maxY, rotation));
        }
        catch (Exception)
        {
            // Texto sin extensión válida (vacío, fuera de dibujo): no se puede ubicar, así que no sirve.
        }
    }
}
