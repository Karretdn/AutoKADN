using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Globalization;

namespace AutoKADN.Tools.Acotado;

// Herramienta 100% visual (sin XData de material - el resumen de materiales lo maneja COTAK):
// traza una línea recta entre el primer y el último punto seleccionado, la divide en N tramos
// iguales ("tubos"), marca cada división con una pequeña cruz perpendicular, etiqueta cada tramo
// con la longitud de tubo indicada (verde, debajo de la línea) y pide, punto por punto, el
// número de "pega"/empalme para cada división (negro, arriba de la línea).
public sealed class PegasTool
{
    private const double TickHalfLength = 2.5;
    private const double LabelOffset = 3.0;
    private const double TextHeight = 2.5;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        editor.WriteMessage("\n[PEGAS] Seleccione los puntos del tramo (Enter o clic derecho para terminar, ESC para cancelar).\n");
        if (!CollectPoints(editor, out List<Point3d> points)) return;

        Point3d startPoint = points[0];
        Point3d endPoint = points[points.Count - 1];
        if (startPoint.DistanceTo(endPoint) <= Tolerance.Global.EqualPoint)
        {
            editor.WriteMessage("\nEl tramo debe tener una longitud mayor que cero.\n");
            return;
        }

        PromptIntegerOptions countOptions = new PromptIntegerOptions("\n¿Cuántos tubos? ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false, LowerLimit = 1
        };
        PromptIntegerResult countResult = editor.GetInteger(countOptions);
        if (countResult.Status != PromptStatus.OK) return;
        int tubeCount = countResult.Value;

        PromptDoubleOptions lengthOptions = new PromptDoubleOptions("\n¿Cuánto mide cada tubo? ")
        {
            AllowNone = false, AllowNegative = false, AllowZero = false
        };
        PromptDoubleResult lengthResult = editor.GetDouble(lengthOptions);
        if (lengthResult.Status != PromptStatus.OK) return;
        double tubeLength = lengthResult.Value;

        Vector3d direction = (endPoint - startPoint).GetNormal();
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();

        var divisionPoints = new Point3d[tubeCount + 1];
        for (int i = 0; i <= tubeCount; i++)
        {
            double t = (double)i / tubeCount;
            divisionPoints[i] = startPoint + (endPoint - startPoint) * t;
        }

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            string layerName = GetCurrentLayerName(database, transaction);
            ObjectId textStyleId = database.Textstyle;

            AddLine(transaction, currentSpace, startPoint, endPoint, layerName);

            for (int i = 0; i <= tubeCount; i++)
            {
                Point3d divisionPoint = divisionPoints[i];
                AddLine(transaction, currentSpace, divisionPoint - normal * TickHalfLength, divisionPoint + normal * TickHalfLength, layerName);
            }

            for (int i = 0; i < tubeCount; i++)
            {
                Point3d midpoint = divisionPoints[i] + (divisionPoints[i + 1] - divisionPoints[i]) * 0.5;
                Point3d labelPosition = midpoint - normal * LabelOffset;
                AddCenteredText(transaction, currentSpace, FormatQuantity(tubeLength), labelPosition, TextHeight, layerName, textStyleId, GreenColor());
            }

            transaction.Commit();
        }

        editor.Regen();

        for (int i = 0; i <= tubeCount; i++)
        {
            PromptStringOptions pegaOptions = new PromptStringOptions($"\nNúmero de pega en el punto {i + 1} de {tubeCount + 1}: ")
            {
                AllowSpaces = false
            };
            PromptResult pegaResult = editor.GetString(pegaOptions);
            if (pegaResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pegaResult.StringResult))
            {
                editor.WriteMessage("\n[PEGAS] Numeración de pegas cancelada. Se conserva lo ya ingresado.\n");
                break;
            }

            Point3d labelPosition = divisionPoints[i] + normal * LabelOffset;
            using Transaction transaction = database.TransactionManager.StartTransaction();
            BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            string layerName = GetCurrentLayerName(database, transaction);
            ObjectId textStyleId = database.Textstyle;
            AddCenteredText(transaction, currentSpace, pegaResult.StringResult.Trim(), labelPosition, TextHeight, layerName, textStyleId, BlackColor());
            transaction.Commit();
            editor.Regen();
        }

        editor.WriteMessage("\n[PEGAS] Tendido generado.\n");
    }

    private static bool CollectPoints(Editor editor, out List<Point3d> points)
    {
        points = new List<Point3d>();

        PromptPointOptions firstOptions = new PromptPointOptions("\nPrimer punto (ESC para cancelar): ") { AllowNone = true };
        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status != PromptStatus.OK) return false;
        points.Add(first.Value);

        while (true)
        {
            PromptPointOptions options = new PromptPointOptions("\nSiguiente punto (Enter o clic derecho para terminar): ")
            {
                BasePoint = points[points.Count - 1],
                UseBasePoint = true,
                AllowNone = true
            };
            PromptPointResult result = editor.GetPoint(options);
            if (result.Status == PromptStatus.OK)
            {
                points.Add(result.Value);
                continue;
            }
            break;
        }

        return points.Count >= 2;
    }

    private static Color GreenColor() => Color.FromColorIndex(ColorMethod.ByAci, 3);
    private static Color BlackColor() => Color.FromRgb(0, 0, 0);

    private static void AddLine(Transaction transaction, BlockTableRecord currentSpace, Point3d start, Point3d end, string layerName)
    {
        var line = new Line(start, end) { Layer = layerName, ColorIndex = 256 };
        currentSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
    }

    private static void AddCenteredText(Transaction transaction, BlockTableRecord currentSpace, string value, Point3d position, double height, string layerName, ObjectId textStyleId, Color color)
    {
        var text = new DBText
        {
            TextString = value,
            Position = position,
            Height = height,
            TextStyleId = textStyleId,
            Layer = layerName,
            Color = color,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = position
        };
        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
    }

    private static string FormatQuantity(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        if (transaction.GetObject(database.Clayer, OpenMode.ForRead) is LayerTableRecord layer) return layer.Name;
        return string.Empty;
    }
}
