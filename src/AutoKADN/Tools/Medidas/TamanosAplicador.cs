using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using AutoKADN.Core;
using AutoKADN.Tools.Layouts;

namespace AutoKADN.Tools.Medidas;

/// <summary>Qué se actualizó en el dibujo al aplicar los tamaños.</summary>
public sealed class TamanosResultado
{
    public int Cotas, Bloques, Anotaciones, Limites, Vial, Predial;
    public int Total => Cotas + Bloques + Anotaciones + Limites + Vial + Predial;
}

/// <summary>
/// Pone en el dibujo abierto los tamaños elegidos:
///  · Cotas: todas las cotas de las capas COTA_*, UC_* y COTAS MAGENTA (escala de cota).
///  · Bloques: los de la capa Mat (tamaño; se conserva la posición y el espejo).
///  · Anotaciones: los textos de ACTIVIDAD y ESPIRAL (los que crea ANOTACIONES).
///  · Límites, vial y predial: los textos marcados por las herramientas; los de antes se reconocen por lo que
///    dicen (LB, LP, LC; "KR 12 - ASF") y los prediales por tener el tamaño anterior dentro del área de plano.
/// Los textos de otros tamaños que escribió la persona a mano no se tocan.
/// </summary>
public static class TamanosAplicador
{
    private const string XDataAppName = "AUTOKADN";
    private const string BlocksLayer = "Mat";
    private const double Tolerance = 1e-6;
    private static readonly Regex RingLayout = new Regex(@"^(?:ANILLO\s+\d+|TRONCAL)\s+(?:DETALLE|UC)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Tamaño más común (valor absoluto de la escala) de los bloques de la capa Mat; null si no hay.
    /// `scope`: el espacio (modelo o papel de un layout) donde se cuentan; sin indicar, todo el dibujo.
    /// </summary>
    public static double? CurrentBlockScale(Database database, ObjectId scope = default)
    {
        var counts = new Dictionary<double, int>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            foreach (BlockTableRecord space in Spaces(database, transaction, scope))
            {
                foreach (ObjectId id in space)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is not BlockReference block) continue;
                    if (!string.Equals(block.Layer, BlocksLayer, StringComparison.OrdinalIgnoreCase)) continue;
                    double scale = Math.Round(Math.Abs(block.ScaleFactors.X), 3);
                    counts[scale] = counts.TryGetValue(scale, out int n) ? n + 1 : 1;
                }
            }
            transaction.Commit();
        }
        if (counts.Count == 0) return null;
        return counts.OrderByDescending(p => p.Value).First().Key;
    }

    /// <summary>
    /// `scope`: el espacio (modelo o papel de un layout) que se actualiza; solo ese, el resto del dibujo no se toca.
    /// Sin indicar, todo el dibujo.
    /// </summary>
    public static TamanosResultado Aplicar(Database database, TamanoValores previo, TamanoValores nuevo, ObjectId scope = default)
    {
        var resultado = new TamanosResultado();
        using Transaction transaction = database.TransactionManager.StartTransaction();
        foreach (BlockTableRecord space in Spaces(database, transaction, scope))
        {
            bool isRingLayout = IsRingLayout(transaction, space);
            foreach (ObjectId id in space)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
                switch (entity)
                {
                    case Dimension dimension when Core.Tamanos.IsCotaLayer(dimension.Layer):
                        if (Math.Abs(dimension.Dimscale - nuevo.Cotas) > Tolerance)
                        {
                            dimension.UpgradeOpen();
                            dimension.Dimscale = nuevo.Cotas;
                            dimension.RecomputeDimensionBlock(true);
                            resultado.Cotas++;
                        }
                        break;

                    case BlockReference block when string.Equals(block.Layer, BlocksLayer, StringComparison.OrdinalIgnoreCase):
                        if (ScaleBlock(block, nuevo.Bloques)) resultado.Bloques++;
                        break;

                    case MText mtext when IsAnnotation(mtext):
                        if (Math.Abs(mtext.TextHeight - nuevo.Anotaciones) > Tolerance)
                        {
                            mtext.UpgradeOpen();
                            mtext.TextHeight = nuevo.Anotaciones;
                            resultado.Anotaciones++;
                        }
                        break;

                    case DBText text:
                        string? kind = Kind(text, previo, isRingLayout);
                        if (kind is null) break;
                        double height = Core.Tamanos.HeightFor(nuevo, kind);
                        bool untagged = SizeTags.ReadKind(text) is null;
                        if (Math.Abs(text.Height - height) > Tolerance || untagged)
                        {
                            text.UpgradeOpen();
                            if (Math.Abs(text.Height - height) > Tolerance)
                            {
                                text.Height = height;
                                text.AdjustAlignment(database);
                                if (kind == Core.Tamanos.KindLimite) resultado.Limites++;
                                else if (kind == Core.Tamanos.KindVial) resultado.Vial++;
                                else resultado.Predial++;
                            }
                            if (untagged) SizeTags.Tag(text, database, transaction, kind); // desde ahora se reconoce sin adivinar
                        }
                        break;
                }
            }
        }
        transaction.Commit();
        return resultado;
    }

    // Escala uniforme conservando el signo de cada eje (los bloques espejados tienen escala negativa).
    private static bool ScaleBlock(BlockReference block, double size)
    {
        Autodesk.AutoCAD.Geometry.Scale3d current = block.ScaleFactors;
        if (Math.Abs(Math.Abs(current.X) - size) <= Tolerance && Math.Abs(Math.Abs(current.Y) - size) <= Tolerance && Math.Abs(Math.Abs(current.Z) - size) <= Tolerance)
            return false;
        block.UpgradeOpen();
        block.ScaleFactors = new Autodesk.AutoCAD.Geometry.Scale3d(Sign(current.X) * size, Sign(current.Y) * size, Sign(current.Z) * size);
        return true;
    }

    private static double Sign(double value) => value < 0 ? -1.0 : 1.0;

    // Texto de ANOTACIONES: lleva XData AUTOKADN de tipo ACTIVIDAD o ESPIRAL.
    private static bool IsAnnotation(MText mtext)
    {
        using ResultBuffer? buffer = mtext.GetXDataForApplication(XDataAppName);
        if (buffer is null) return false;
        TypedValue[] values = buffer.AsArray();
        if (values.Length < 2) return false;
        string? type = values[1].Value as string;
        return string.Equals(type, "ACTIVIDAD", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "ESPIRAL", StringComparison.OrdinalIgnoreCase);
    }

    // Marcado por la herramienta, o reconocido por lo que dice, o predial de antes (tamaño anterior dentro del área de plano).
    private static string? Kind(DBText text, TamanoValores previo, bool isRingLayout)
    {
        string? tagged = SizeTags.ReadKind(text);
        if (tagged is not null) return tagged;

        string? byContent = Core.Tamanos.ClassifyText(text.TextString);
        if (byContent is not null) return byContent;

        if (isRingLayout && Math.Abs(text.Height - previo.Predial) <= Tolerance
            && (ClonarUcTool.IsInsideFrame(text, ClonarUcTool.DetalleFrameMinY, ClonarUcTool.DetalleFrameMaxY)
                || ClonarUcTool.IsInsideFrame(text, ClonarUcTool.UcFrameMinY, ClonarUcTool.UcFrameMaxY)))
            return Core.Tamanos.KindPredial;
        return null;
    }

    private static bool IsRingLayout(Transaction transaction, BlockTableRecord space)
    {
        if (space.LayoutId.IsNull) return false;
        return transaction.GetObject(space.LayoutId, OpenMode.ForRead) is Layout layout && RingLayout.IsMatch(layout.LayoutName.Trim());
    }

    // El espacio indicado, o (sin indicar) el espacio modelo y los espacios papel de cada layout.
    private static IEnumerable<BlockTableRecord> Spaces(Database database, Transaction transaction, ObjectId scope)
    {
        if (!scope.IsNull)
        {
            yield return (BlockTableRecord)transaction.GetObject(scope, OpenMode.ForRead);
            yield break;
        }
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (record.IsLayout) yield return record;
        }
    }
}
