using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Core;

public sealed class TextCreationService
{
    public void CreateText(Point3d position, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return;

        Database database = document.Database;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        var text = new DBText
        {
            Position = position,
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction)
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);
        transaction.Commit();
    }

    public bool CreateTextWithJig(Point3d initialPosition, string content, double height = 1.45)
    {
        Document? document = Application.DocumentManager.MdiActiveDocument;
        if (document is null || string.IsNullOrWhiteSpace(content))
            return false;

        Database database = document.Database;
        Editor editor = document.Editor;

        using Transaction transaction = database.TransactionManager.StartTransaction();

        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
            database.CurrentSpaceId,
            OpenMode.ForWrite);

        var text = new DBText
        {
            TextString = content.Trim(),
            Height = height,
            Layer = GetCurrentLayerName(database, transaction),
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = initialPosition,
            Position = initialPosition
        };

        currentSpace.AppendEntity(text);
        transaction.AddNewlyCreatedDBObject(text, true);

        var jig = new NomenclaturaTextJig(text, initialPosition, ObtenerModoOrto());
        PromptResult result = editor.Drag(jig);

        if (result.Status != PromptStatus.OK)
        {
            text.Erase();
            transaction.Commit();
            return false;
        }

        transaction.Commit();
        return true;
    }

    private static bool ObtenerModoOrto()
    {
        try
        {
            object? value = Application.GetSystemVariable("ORTHOMODE");
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetCurrentLayerName(Database database, Transaction transaction)
    {
        LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(
            database.Clayer,
            OpenMode.ForRead);

        return layer.Name;
    }

    private sealed class NomenclaturaTextJig : EntityJig
    {
        private readonly DBText _text;
        private readonly Point3d _initialPosition;
        private readonly bool _orthoEnabled;
        private Point3d _currentPosition;
        private double _currentRotation;

        public NomenclaturaTextJig(DBText text, Point3d initialPosition, bool orthoEnabled)
            : base(text)
        {
            _text = text;
            _initialPosition = initialPosition;
            _orthoEnabled = orthoEnabled;
            _currentPosition = initialPosition;
            _currentRotation = 0.0;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nMueva el texto y haga clic para terminar: ")
            {
                UseBasePoint = true,
                BasePoint = _initialPosition,
                UserInputControls = UserInputControls.Accept3dCoordinates
                    | UserInputControls.NoZeroResponseAccepted
                    | UserInputControls.NoNegativeResponseAccepted
            };

            PromptPointResult result = prompts.AcquirePoint(options);

            if (result.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            Point3d point = result.Value;
            double rotation = CalcularRotacion(_initialPosition, point, _orthoEnabled);

            if (point.DistanceTo(_currentPosition) < Tolerance.Global.EqualPoint
                && Math.Abs(rotation - _currentRotation) < 1e-9)
                return SamplerStatus.NoChange;

            _currentPosition = point;
            _currentRotation = rotation;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            _text.Position = _currentPosition;
            _text.AlignmentPoint = _currentPosition;
            _text.Rotation = _currentRotation;
            return true;
        }

        private static double CalcularRotacion(Point3d origin, Point3d point, bool orthoEnabled)
        {
            double dx = point.X - origin.X;
            double dy = point.Y - origin.Y;

            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9)
                return 0.0;

            double angle = Math.Atan2(dy, dx);

            if (orthoEnabled)
                angle = Math.Round(angle / (Math.PI / 2.0)) * (Math.PI / 2.0);

            return angle;
        }
    }
}
