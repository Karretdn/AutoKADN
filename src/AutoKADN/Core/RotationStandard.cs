using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Core;

/// <summary>
/// Regla única de rotación para LIMIK, Vial y Predial.
/// ORTHOMODE activo: 0/90/180/270 grados.
/// ORTHOMODE desactivado: rotación libre.
/// </summary>
public static class RotationStandard
{
    private const double QuarterTurn = Math.PI / 2.0;
    private const double AngleTolerance = 1e-10;

    public static bool IsOrthoEnabled()
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

    public static double FromPoint(Point3d center, Point3d cursor, bool orthoEnabled)
    {
        Vector3d direction = cursor - center;
        if (direction.Length <= Tolerance.Global.EqualPoint)
            return 0.0;

        double angle = Math.Atan2(direction.Y, direction.X);
        return ApplyOrtho(angle, orthoEnabled);
    }

    public static double ApplyOrtho(double angle, bool orthoEnabled)
    {
        if (!orthoEnabled)
            return angle;

        double snapped = Math.Round(angle / QuarterTurn) * QuarterTurn;
        return Math.Abs(snapped) < AngleTolerance ? 0.0 : snapped;
    }

    public static double FromDirection(Vector3d direction, bool orthoEnabled)
    {
        if (direction.Length <= Tolerance.Global.EqualPoint)
            return 0.0;

        double angle = Math.Atan2(direction.Y, direction.X);
        return ApplyOrtho(angle, orthoEnabled);
    }

    public static double MakeReadable(double angle)
    {
        if (angle > Math.PI / 2.0 || angle <= -Math.PI / 2.0)
            angle += angle > 0.0 ? -Math.PI : Math.PI;

        return angle;
    }
}
