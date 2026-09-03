using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Acotado;

public sealed class CotaTool
{
    private const double OffsetFromLine = 5.50;
    private const double OverallDimensionScale = 0.05;

    private static readonly (string Label, string Layer)[] LongitudLayers =
    {
        ("TUBERIA 1-2\"", "COTA_1-2"),
        ("TUBERIA 3-4\"", "COTA_3-4")
    };

    private static readonly (string Label, string Layer)[] UCLayers =
    {
        ("CANALIZACION 1-2\"", "UC_1-2"),
        ("CANALIZACION 3-4\"", "UC_3-4")
    };

    private sealed record UCAttribute(
        string Keyword,
        short? Aci,
        byte R,
        byte G,
        byte B);

    private static readonly UCAttribute[] UCAttributes =
    {
        new("ZONA_VERDE", 3, 0, 0, 0),
        new("ANDEN_TABLETA", 1, 0, 0, 0),
        new("CALZADA_CONCRETO", 8, 0, 0, 0),
        new("DESTAPADO", 2, 0, 0, 0),
        new("CUNETA", null, 100, 33, 101),
        new("ANDEN_CONCRETO", 5, 0, 0, 0),
        new("ASFALTO", 30, 0, 0, 0),
        new("ADOQUIN", 4, 0, 0, 0)
    };

    private static readonly IReadOnlyDictionary<string, UCAttribute> UCAttributesByKeyword =
        UCAttributes.ToDictionary(x => x.Keyword, StringComparer.OrdinalIgnoreCase);

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[COTAK] Acotado rápido.\n");

        string? type = SelectType(editor);
        if (type is null) return;

        if (type.Equals("UBICACION", StringComparison.OrdinalIgnoreCase))
        {
            new UbicacionTool().Run();
            return;
        }

        while (CreateDimensionFromLine(document, editor, type)) { }
    }

    private static string? SelectType(Editor editor)
    {
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);

            var options = new PromptKeywordOptions("\nSeleccione tipo de cota [Longitud/UC/Ubicacion]: ")
            {
                AllowNone = true
            };

            options.Keywords.Add("Longitud");
            options.Keywords.Add("UC");
            options.Keywords.Add("Ubicacion");

            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK ? result.StringResult : null;
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static bool CreateDimensionFromLine(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor,
        string type)
    {
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptEntityResult entityResult;

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);

            var entityOptions = new PromptEntityOptions("\nSeleccione la línea: ")
            {
                AllowNone = true
            };

            entityOptions.SetRejectMessage("\nDebe seleccionar una línea o polilínea.\n");
            entityOptions.AddAllowedClass(typeof(Line), false);
            entityOptions.AddAllowedClass(typeof(Polyline), false);

            entityResult = editor.GetEntity(entityOptions);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (entityResult.Status != PromptStatus.OK) return false;

        if (!FindLineAtEntityPoint(
                document.Database,
                entityResult.ObjectId,
                entityResult.PickedPoint,
                out Point3d startPoint,
                out Point3d endPoint,
                out Vector3d direction))
        {
            editor.WriteMessage("\nEl objeto seleccionado no contiene una línea o tramo recto válido.\n");
            return true;
        }

        Point3d midpoint = startPoint + (endPoint - startPoint) * 0.5;
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        if (normal.Y < 0.0) normal = -normal;

        double rotation = Math.Atan2(direction.Y, direction.X);

        ObjectId dimensionId = CreateDimension(
            document.Database,
            startPoint,
            endPoint,
            midpoint + normal * OffsetFromLine,
            rotation);

        if (dimensionId == ObjectId.Null) return true;

        editor.Regen();

        if (!MoveDimensionToSide(document, editor, dimensionId, midpoint, normal))
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        editor.Regen();

        var textOptions = new PromptStringOptions("\nNúmero/texto de cota: ")
        {
            AllowSpaces = true
        };

        PromptResult textResult = editor.GetString(textOptions);

        if (textResult.Status == PromptStatus.Cancel || textResult.Status == PromptStatus.None)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        if (textResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(textResult.StringResult))
        {
            SetDimensionText(document.Database, dimensionId, textResult.StringResult.Trim());
            editor.Regen();
        }

        string? layerName = SelectLayer(document.Database, editor, type);
        if (layerName is null)
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        if (!SetDimensionAppearance(document.Database, dimensionId, layerName))
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

        if (type.Equals("UC", StringComparison.OrdinalIgnoreCase) &&
            !SelectUCAttribute(document.Database, editor, dimensionId))
        {
            EraseDimension(document.Database, dimensionId);
            return false;
        }

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

        BlockTableRecord currentSpace =
            (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        var dimension = new RotatedDimension(
            rotation,
            startPoint,
            endPoint,
            dimensionLinePoint,
            string.Empty,
            database.Dimstyle)
        {
            Dimscale = OverallDimensionScale,
            Dimtad = 0,
            Dimjust = 0
        };

        currentSpace.AppendEntity(dimension);
        transaction.AddNewlyCreatedDBObject(dimension, true);
        transaction.Commit();

        return dimension.ObjectId;
    }

    private static bool MoveDimensionToSide(
        Autodesk.AutoCAD.ApplicationServices.Document document,
        Editor editor,
        ObjectId dimensionId,
        Point3d midpoint,
        Vector3d normal)
    {
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();

        var dimension = transaction.GetObject(dimensionId, OpenMode.ForWrite) as RotatedDimension;
        if (dimension is null)
        {
            transaction.Abort();
            return false;
        }

        var jig = new DimensionSideJig(dimension, midpoint, normal, OffsetFromLine);
        editor.WriteMessage("\nMueva el mouse al lado deseado y haga clic para fijar: ");

        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");
        PromptResult result;

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);
            result = editor.Drag(jig);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }

        if (result.Status == PromptStatus.OK)
        {
            transaction.Commit();
            return true;
        }

        transaction.Abort();
        return false;
    }

    private static string? SelectLayer(Database database, Editor editor, string type)
    {
        var preferred = type.Equals("UC", StringComparison.OrdinalIgnoreCase)
            ? UCLayers
            : LongitudLayers;

        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);

            var options = new PromptKeywordOptions(
                $"\nSeleccione capa [{string.Join("/", preferred.Select(x => x.Label))}]: ")
            {
                AllowNone = true
            };

            string[] keywords = { "OPCION1", "OPCION2" };

            for (int i = 0; i < preferred.Length; i++)
            {
                options.Keywords.Add(
                    keywords[i],
                    preferred[i].Label,
                    preferred[i].Label,
                    true,
                    true);
            }

            PromptResult result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK) return null;

            int index = Array.IndexOf(keywords, result.StringResult);
            if (index < 0 || index >= preferred.Length) return null;

            string exactLayerName = preferred[index].Layer;

            if (!LayerExists(database, exactLayerName))
            {
                editor.WriteMessage($"\nNo existe la capa requerida: {exactLayerName}\n");
                return null;
            }

            return exactLayerName;
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static bool LayerExists(Database database, string layerName)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        LayerTable layerTable =
            (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);

        bool exists = layerTable.Has(layerName);
        transaction.Commit();
        return exists;
    }

    private static bool SelectUCAttribute(Database database, Editor editor, ObjectId dimensionId)
    {
        object originalShortcutMenu = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("SHORTCUTMENU");

        try
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", 0);

            var options = new PromptKeywordOptions(
                $"\nSeleccione atributo UC [{string.Join("/", UCAttributes.Select(x => x.Keyword))}]: ")
            {
                AllowNone = false
            };

            foreach (UCAttribute attribute in UCAttributes)
            {
                options.Keywords.Add(
                    attribute.Keyword,
                    attribute.Keyword,
                    attribute.Keyword,
                    true,
                    true);
            }

            PromptResult result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK) return false;

            if (string.IsNullOrEmpty(result.StringResult) ||
                !UCAttributesByKeyword.TryGetValue(result.StringResult, out UCAttribute? selected))
                return false;

            return SetDimensionColor(database, dimensionId, selected);
        }
        finally
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static bool SetDimensionColor(
        Database database,
        ObjectId dimensionId,
        UCAttribute attribute)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is not Dimension dimension)
        {
            transaction.Abort();
            return false;
        }

        if (attribute.Aci.HasValue)
        {
            if (attribute.Aci.Value < 1 || attribute.Aci.Value > 255)
            {
                transaction.Abort();
                return false;
            }

            dimension.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                attribute.Aci.Value);
        }
        else
        {
            dimension.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(
                attribute.R,
                attribute.G,
                attribute.B);
        }

        transaction.Commit();
        return true;
    }

    private static bool FindLineAtEntityPoint(
        Database database,
        ObjectId selectedObjectId,
        Point3d point,
        out Point3d startPoint,
        out Point3d endPoint,
        out Vector3d direction)
    {
        startPoint = Point3d.Origin;
        endPoint = Point3d.Origin;
        direction = Vector3d.XAxis;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(selectedObjectId, OpenMode.ForRead) is Line line)
        {
            Vector3d segmentVector = line.EndPoint - line.StartPoint;

            if (segmentVector.Length > Tolerance.Global.EqualPoint)
            {
                startPoint = line.StartPoint;
                endPoint = line.EndPoint;
                direction = segmentVector.GetNormal();
                transaction.Commit();
                return true;
            }
        }
        else if (transaction.GetObject(selectedObjectId, OpenMode.ForRead) is Polyline polyline &&
                 polyline.NumberOfVertices >= 2)
        {
            int segmentCount = polyline.Closed
                ? polyline.NumberOfVertices
                : polyline.NumberOfVertices - 1;

            double bestDistance = double.MaxValue;
            bool found = false;

            for (int i = 0; i < segmentCount; i++)
            {
                if (polyline.GetSegmentType(i) != SegmentType.Line)
                    continue;

                int nextIndex = (i + 1) % polyline.NumberOfVertices;
                Point3d segmentStart = polyline.GetPoint3dAt(i);
                Point3d segmentEnd = polyline.GetPoint3dAt(nextIndex);
                Vector3d segmentVector = segmentEnd - segmentStart;

                if (segmentVector.Length <= Tolerance.Global.EqualPoint)
                    continue;

                LineSegment3d segment = new LineSegment3d(segmentStart, segmentEnd);
                Point3d closest = segment.GetClosestPointTo(point).Point;
                double distance = closest.DistanceTo(point);

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    startPoint = segmentStart;
                    endPoint = segmentEnd;
                    direction = segmentVector.GetNormal();
                    found = true;
                }
            }

            transaction.Commit();
            return found;
        }

        transaction.Commit();
        return false;
    }

    private static void SetDimensionText(Database database, ObjectId dimensionId, string textValue)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is Dimension dimension)
            dimension.DimensionText = textValue;

        transaction.Commit();
    }

    private static bool SetDimensionAppearance(Database database, ObjectId dimensionId, string layerName)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        LayerTable layerTable =
            (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);

        if (!layerTable.Has(layerName))
        {
            transaction.Abort();
            return false;
        }

        ObjectId layerId = layerTable[layerName];

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite) is not Dimension dimension)
        {
            transaction.Abort();
            return false;
        }

        dimension.LayerId = layerId;
        dimension.ColorIndex = 256;

        transaction.Commit();
        return true;
    }

    private static void EraseDimension(Database database, ObjectId dimensionId)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();

        if (transaction.GetObject(dimensionId, OpenMode.ForWrite, false) is Entity entity)
            entity.Erase();

        transaction.Commit();
    }

    private sealed class DimensionSideJig : EntityJig
    {
        private readonly RotatedDimension _dimension;
        private readonly Point3d _midpoint;
        private readonly Vector3d _normal;
        private readonly double _fixedOffset;
        private Point3d _lastPoint;

        public DimensionSideJig(
            RotatedDimension dimension,
            Point3d midpoint,
            Vector3d normal,
            double fixedOffset)
            : base(dimension)
        {
            _dimension = dimension;
            _midpoint = midpoint;
            _normal = normal.GetNormal();
            _fixedOffset = fixedOffset;
            _lastPoint = dimension.DimLinePoint;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(
                "\nMueva el mouse al lado deseado y haga clic para fijar: ")
            {
                UseBasePoint = true,
                BasePoint = _midpoint,
                UserInputControls =
                    UserInputControls.Accept3dCoordinates |
                    UserInputControls.NullResponseAccepted
            };

            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            Vector3d fromCenter = result.Value - _midpoint;
            double signedDistance = fromCenter.DotProduct(_normal);
            Vector3d placementNormal = signedDistance >= 0.0 ? _normal : -_normal;
            Point3d projectedPoint = _midpoint + placementNormal * _fixedOffset;

            if (projectedPoint.IsEqualTo(_lastPoint))
                return SamplerStatus.NoChange;

            _lastPoint = projectedPoint;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _dimension.DimLinePoint = _lastPoint;
            return true;
        }
    }
}
