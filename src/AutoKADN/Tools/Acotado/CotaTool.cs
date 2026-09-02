using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Acotado;

public sealed class CotaTool
{
    private const double OffsetFromLine = 1.10;
    private const short NearestObjectSnap = 512;
    private const double PointTolerance = 1e-5;

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[COTAK] Acotado rápido. ESC para salir.\n");

        string? type = SelectType(editor);
        if (type is null)
            return;

        object originalOsMode = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("OSMODE");

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", NearestObjectSnap);

            while (true)
            {
                if (!CreateDimensionFromLine(document, editor, type))
                    return;
            }
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("OSMODE", originalOsMode);
        }
    }

    private static string? SelectType(Editor editor)
    {
        var options = new PromptKeywordOptions("\nSeleccione tipo de cota [Longitud/UC]: ")
        {
            AllowNone = false
        };
        options.Keywords.Add("Longitud");
        options.Keywords.Add("UC");

        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static bool CreateDimensionFromLine(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor,
        string type)
    {
        var pointOptions = new PromptPointOptions("\nHaga clic sobre la línea (ESC para salir): ")
        {
            AllowNone = false,
            UserInputControls = UserInputControls.Accept3dCoordinates
                | UserInputControls.NoZeroResponseAccepted
        };

        PromptPointResult pointResult = editor.GetPoint(pointOptions);
        if (pointResult.Status != PromptStatus.OK)
            return false;

        if (!FindLineAtPoint(
                document.Database,
                pointResult.Value,
                out Point3d startPoint,
                out Point3d endPoint,
                out Vector3d direction))
        {
            editor.WriteMessage("\nEl punto seleccionado no corresponde a una línea válida.\n");
            return true;
        }

        Point3d midpoint = startPoint + (endPoint - startPoint) * 0.5;
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        Point3d dimensionLinePoint = midpoint + normal * OffsetFromLine;
        double rotation = Math.Atan2(direction.Y, direction.X);

        ObjectId dimensionId = CreateDimension(
            document.Database,
            startPoint,
            endPoint,
            dimensionLinePoint,
            rotation);

        if (dimensionId == ObjectId.Null)
            return true;

        editor.Regen();

        // La cota aparece inmediatamente. ENTER conserva la medición real;
        // cualquier texto escrito se convierte en el texto mostrado.
        var textOptions = new PromptStringOptions(
            "\nTexto de cota (ENTER para conservar la medida): ")
        {
            AllowSpaces = true,
            AllowNone = true
        };

        PromptResult textResult = editor.GetString(textOptions);
        if (textResult.Status == PromptStatus.Cancel)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        if (textResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(textResult.StringResult))
            SetDimensionText(document.Database, dimensionId, textResult.StringResult);

        string? layerName = SelectLayer(document.Database, editor, type);
        if (layerName is null)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        short colorIndex = SelectColor(editor);
        if (colorIndex < 0)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        SetDimensionAppearance(document.Database, dimensionId, layerName, colorIndex);
        editor.Regen();
        return true;
    }

    private static ObjectId CreateDimension(
        Database database,
        Point3d startPoint,
        Point3d endPoint,
        Point3d dimensionLinePoint,
        double rotation)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForWrite);

        // Se usa exactamente el estilo de cota actualmente configurado en el DWG.
        var dimension = new RotatedDimension(
            rotation,
            startPoint,
            endPoint,
            dimensionLinePoint,
            string.Empty,
            database.Dimstyle);

        currentSpace.AppendEntity(dimension);
        transaction.AddNewlyCreatedDBObject(dimension, true);
        transaction.Commit();
        return dimension.ObjectId;
    }

    private static bool FindLineAtPoint(
        Database database,
        Point3d point,
        out Point3d startPoint,
        out Point3d endPoint,
        out Vector3d direction)
    {
        startPoint = Point3d.Origin;
        endPoint = Point3d.Origin;
        direction = Vector3d.XAxis;
        double bestDistance = double.MaxValue;
        bool found = false;

        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId, OpenMode.ForRead);

        foreach (ObjectId objectId in currentSpace)
        {
            if (transaction.GetObject(objectId, OpenMode.ForRead) is Line line)
            {
                Point3d closest = line.GetClosestPointTo(point, false);
                double distance = closest.DistanceTo(point);
                if (distance > PointTolerance || distance >= bestDistance)
                    continue;

                Vector3d vector = line.EndPoint - line.StartPoint;
                if (vector.Length <= Tolerance.Global.EqualPoint)
                    continue;

                startPoint = line.StartPoint;
                endPoint = line.EndPoint;
                direction = vector.GetNormal();
                bestDistance = distance;
                found = true;
            }
        }

        transaction.Commit();
        return found;
    }

    private static string? SelectLayer(Database database, Editor editor, string type)
    {
        string[] preferred = type.Equals("UC", StringComparison.OrdinalIgnoreCase)
            ? ["UC_1-2", "UC_3-4"]
            : ["COTA_1-2", "COTA_3-4"];

        var available = new List<string>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (string name in preferred)
            {
                if (table.Has(name))
                    available.Add(name);
            }
            transaction.Commit();
        }

        if (available.Count == 0)
        {
            editor.WriteMessage($"\nNo existe ninguna capa disponible para {type}.\n");
            return null;
        }

        var options = new PromptKeywordOptions(
            $"\nSeleccione capa [{string.Join("/", available)}]: ")
        {
            AllowNone = false
        };

        foreach (string name in available)
            options.Keywords.Add(name);

        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static short SelectColor(Editor editor)
    {
        var options = new PromptKeywordOptions(
            "\nSeleccione color [Verde/Rojo/Gris/Amarillo/Morado/Azul/Naranja/Cyan]: ")
        {
            AllowNone = false
        };

        options.Keywords.Add("Verde");
        options.Keywords.Add("Rojo");
        options.Keywords.Add("Gris");
        options.Keywords.Add("Amarillo");
        options.Keywords.Add("Morado");
        options.Keywords.Add("Azul");
        options.Keywords.Add("Naranja");
        options.Keywords.Add("Cyan");

        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK)
            return -1;

        return result.StringResult switch
        {
            "Verde" => 3,
            "Rojo" => 1,
            "Gris" => 8,
            "Amarillo" => 2,
            "Morado" => 6,
            "Azul" => 5,
            "Naranja" => 30,
            "Cyan" => 4,
            _ => -1
        };
    }

    private static void SetDimensionText(Database database, ObjectId dimensionId, string textValue)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is Dimension dimension)
            dimension.DimensionText = textValue;
        transaction.Commit();
    }

    private static void SetDimensionAppearance(
        Database database,
        ObjectId dimensionId,
        string layerName,
        short colorIndex)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        ObjectId layerId = layerTable[layerName];

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is Dimension dimension)
        {
            dimension.LayerId = layerId;
            dimension.ColorIndex = colorIndex;
        }

        transaction.Commit();
    }

    private static void EraseDimension(Database database, ObjectId dimensionId)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(dimensionId, OpenMode.ForWrite, false) is Entity entity)
            entity.Erase();
        transaction.Commit();
    }
}
