using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AutoKADN.Core;

namespace AutoKADN.Tools.Excel;

// Datos del formato FT-O-108 (Control de excavación) a partir de las cotas por anillo. Misma estructura que el
// FT-T-127 (una fila por plano, 4 bloques de terreno x 4 diámetros, hojas adicionales), pero:
//  - el ML es de EXCAVACIÓN: las cotas UC menos lo que no se excava (tramos de camisa y cruces con topo); el
//    espiral y el material de prueba no se suman (no están en las cotas);
//  - cada columna con diámetro lleva el tipo de material excavado (CALICHE) en la celda alta;
//  - la tabla de la derecha trae un renglón por terreno y diámetro con ML y M3 (ML x factor de zanja).
// Se escribe como la lista "ft108.paginas" de resumen_obra.json: la app repite el formato una vez por elemento.
internal static class ControlExcavacionBuilder
{
    // Medidas del formato impreso (el PDF no se modifica).
    public const int RowsPerPage = 17;
    public const int BlocksPerPage = 4;
    public const int ColumnsPerBlock = 4;

    // Material excavado que se anota en cada columna.
    private const string SoilType = "CALICHE";

    // M3 de zanja por metro lineal: 0.28 para 1/2" y 3/4"; 0.3 para 2", 3", 4" y 6".
    private const double FactorSmall = 0.28;
    private const double FactorTroncal = 0.3;

    // Orden y nombre de los terrenos tal como aparecen en el selector de la cota UC de la app de AutoCAD.
    private static readonly string[] TerrainOrder =
    {
        "ZONA VERDE", "ANDEN TABLETA", "CALZADA CONCRETO", "DESTAPADO", "CUNETA", "ANDEN CONCRETO", "ASFALTO", "ADOQUIN",
    };

    // Diámetros que procesa la app.
    private static readonly string[] DiameterOrder = { "1/2", "3/4", "2", "3", "4", "6" };

    private sealed class Block
    {
        public string Terrain;
        public List<string> Diameters;
    }

    // Propiedad JSON lista para añadir al objeto: "ft108.paginas": [ ... ]
    public static string BuildProperty(ProjectSummary s)
    {
        List<string> rings = OrderedRings(s);
        List<Block> blocks = BuildBlocks(s);

        var rowChunks = Chunk(rings, RowsPerPage);
        var blockChunks = Chunk(blocks, BlocksPerPage);
        if (rowChunks.Count == 0) rowChunks.Add(new List<string>());
        if (blockChunks.Count == 0) blockChunks.Add(new List<Block>());

        var pages = new List<string>();
        bool first = true;
        foreach (List<string> rows in rowChunks)
            foreach (List<Block> pageBlocks in blockChunks)
            {
                pages.Add(BuildPage(s, rows, pageBlocks, rings, first));
                first = false;
            }
        return ResumenObraTool.Quote("ft108.paginas") + ": [\n    " + string.Join(",\n    ", pages) + "\n  ]";
    }

    // ---------- ML de excavación ----------

    // Cotas UC del plano en ese diámetro y terreno, sin los tramos de camisa ni los cruces con topo.
    // Nunca negativo: si lo excluido supera a las cotas, se cuenta cero.
    internal static double Excavation(ProjectSummary s, string ring, UcKey uc)
    {
        double cotas = Lookup(s.RingCotas, ring, uc);
        double excluded = Lookup(s.RingExcluded, ring, uc);
        double ml = cotas - excluded;
        return ml > 0.0 ? ml : 0.0;
    }

    private static double Lookup(Dictionary<string, Dictionary<UcKey, double>> source, string ring, UcKey uc)
    {
        Dictionary<UcKey, double> perUc; double value;
        return source.TryGetValue(ring, out perUc) && perUc.TryGetValue(uc, out value) ? value : 0.0;
    }

    // M3 = ML x factor, truncado a 2 decimales (como en los formatos ya diligenciados).
    internal static double Volume(string diameter, double ml)
    {
        double factor = (diameter == "1/2" || diameter == "3/4") ? FactorSmall : FactorTroncal;
        return Math.Floor(ml * factor * 100.0 + 1e-9) / 100.0;
    }

    // ---------- hoja ----------

    private static string BuildPage(ProjectSummary s, List<string> rows, List<Block> pageBlocks, List<string> allRings, bool first)
    {
        var props = new List<string>();
        int columns = BlocksPerPage * ColumnsPerBlock;

        for (int b = 0; b < BlocksPerPage; b++)
            props.Add(Prop("terreno" + (b + 1), b < pageBlocks.Count ? pageBlocks[b].Terrain : ""));

        // Columna k (1..16) -> (terreno, diámetro) o nada.
        var column = new UcKey?[columns];
        for (int b = 0; b < pageBlocks.Count; b++)
            for (int d = 0; d < pageBlocks[b].Diameters.Count && d < ColumnsPerBlock; d++)
                column[b * ColumnsPerBlock + d] = new UcKey(pageBlocks[b].Diameters[d], pageBlocks[b].Terrain);

        for (int k = 0; k < columns; k++)
            props.Add(Prop("d" + (k + 1), column[k].HasValue ? column[k].Value.Diameter + "\"" : ""));
        for (int k = 0; k < columns; k++)
            props.Add(Prop("material" + (k + 1), column[k].HasValue ? SoilType : ""));

        var items = new List<string>();
        foreach (string ring in rows)
        {
            var cells = new List<string> { Pair("plano", ring) };
            for (int k = 0; k < columns; k++)
            {
                double ml = column[k].HasValue ? Excavation(s, ring, column[k].Value) : 0.0;
                cells.Add(Pair("c" + (k + 1), ml > 0.0 ? Ml(ml) : ""));
            }
            items.Add("{" + string.Join(",", cells) + "}");
        }
        props.Add(Quote("registros") + ": [" + string.Join(", ", items) + "]");

        // TOTAL ML de cada columna: suma de todos los planos (no solo los de esta hoja).
        for (int k = 0; k < columns; k++)
        {
            double total = 0.0;
            if (column[k].HasValue)
                foreach (string ring in allRings) total += Excavation(s, ring, column[k].Value);
            props.Add(Prop("t" + (k + 1), total > 0.0 ? Ml(total) : ""));
        }

        if (first)
        {
            // Tabla "TOTALES UNIDAD CONSTRUCTIVA ML" y observaciones: solo en la primera hoja.
            double totalMl = 0.0, totalVolume = 0.0;
            var ucs = new List<string>();
            foreach (UcKey uc in AllUcs(s, allRings))
            {
                double ml = 0.0;
                foreach (string ring in allRings) ml += Excavation(s, ring, uc);
                if (ml <= 0.0) continue;
                double volume = Volume(uc.Diameter, ml);
                totalMl += ml; totalVolume += volume;
                ucs.Add("{" + Pair("terreno", uc.Surface) + "," + Pair("diametro", uc.Diameter + "\"") + "," + Pair("ml", Ml(ml)) + "," + Pair("m3", Vol(volume)) + "}");
            }
            props.Add(Quote("ucs") + ": [" + string.Join(", ", ucs) + "]");
            props.Add(Prop("total_ml", totalMl > 0.0 ? Ml(totalMl) : ""));
            props.Add(Prop("total_m3", totalVolume > 0.0 ? Vol(totalVolume) : ""));
            props.Add(Prop("observaciones", Observations(s, allRings)));
        }
        return "{" + string.Join(", ", props) + "}";
    }

    // ---------- filas y bloques ----------

    // ANILLO 1, 2, 3... en orden numérico; otros planos por nombre; TRONCAL al final.
    private static List<string> OrderedRings(ProjectSummary s)
    {
        var rings = s.RingCotas.Where(r => r.Value.Values.Any(v => v > 0.0)).Select(r => r.Key).ToList();
        return rings.OrderBy(r => RingRank(r)).ThenBy(r => RingNumber(r)).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int RingRank(string ring)
    {
        if (RingNumber(ring) != int.MaxValue) return 0;
        return string.Equals(ring, "TRONCAL", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static int RingNumber(string ring)
    {
        Match m = Regex.Match(ring, @"^ANILLO\s+(\d+)$", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    // Combinaciones (diámetro, terreno) con excavación en algún plano, de las que procesa la app.
    private static List<UcKey> AllUcs(ProjectSummary s, List<string> rings)
    {
        var set = new HashSet<UcKey>();
        foreach (string ring in rings)
        {
            Dictionary<UcKey, double> cotas;
            if (!s.RingCotas.TryGetValue(ring, out cotas)) continue;
            foreach (UcKey uc in cotas.Keys)
                if (Array.IndexOf(DiameterOrder, uc.Diameter) >= 0 && Excavation(s, ring, uc) > 0.0) set.Add(uc);
        }
        return set.OrderBy(uc => TerrainIndex(uc.Surface)).ThenBy(uc => uc.Surface, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(uc => Array.IndexOf(DiameterOrder, uc.Diameter)).ToList();
    }

    private static List<Block> BuildBlocks(ProjectSummary s)
    {
        List<string> rings = OrderedRings(s);
        var byTerrain = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (UcKey uc in AllUcs(s, rings))
        {
            HashSet<string> set;
            if (!byTerrain.TryGetValue(uc.Surface, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); byTerrain[uc.Surface] = set; }
            set.Add(uc.Diameter);
        }

        var terrains = byTerrain.Keys.OrderBy(t => TerrainIndex(t)).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var blocks = new List<Block>();
        foreach (string terrain in terrains)
        {
            List<string> diameters = byTerrain[terrain].OrderBy(d => Array.IndexOf(DiameterOrder, d)).ToList();
            foreach (List<string> part in Chunk(diameters, ColumnsPerBlock))
                blocks.Add(new Block { Terrain = terrain, Diameters = part });
        }
        return blocks;
    }

    private static int TerrainIndex(string terrain)
    {
        int index = Array.FindIndex(TerrainOrder, t => string.Equals(t, terrain, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    // ---------- observaciones ----------

    // Lo que NO se suma a la excavación: espiral de válvula, cruce con topo y tramos de camisa.
    private static string Observations(ProjectSummary s, List<string> rings)
    {
        var lines = new List<string>();
        AddLine(lines, "ESPIRAL DE VALVULA (NO SE EXCAVA)", rings, r => { double v; return s.RingSpiral.TryGetValue(r, out v) ? v : 0.0; });
        AddLine(lines, "CRUCE CON TOPO (NO SE EXCAVA)", rings, r => Activity(s, r, "CRUCE CON TOPO"));
        AddLine(lines, "CAMISA INSTALADA POR LA CONSTRUCTORA (NO SE EXCAVA)", rings, r => Activity(s, r, "CAMISA"));
        return string.Join("\n", lines);
    }

    private static double Activity(ProjectSummary s, string ring, string label)
    {
        Dictionary<string, double> activities; double v;
        return s.RingActivities.TryGetValue(ring, out activities) && activities.TryGetValue(label, out v) ? v : 0.0;
    }

    private static void AddLine(List<string> lines, string title, List<string> rings, Func<string, double> quantity)
    {
        var parts = new List<string>();
        foreach (string ring in rings)
        {
            double ml = quantity(ring);
            if (ml > 0.0) parts.Add(ring + " = " + Ml(ml) + " ML");
        }
        if (parts.Count > 0) lines.Add(title + ": " + string.Join("; ", parts) + ".");
    }

    // ---------- utilidades ----------

    private static List<List<T>> Chunk<T>(List<T> source, int size)
    {
        var result = new List<List<T>>();
        for (int i = 0; i < source.Count; i += size) result.Add(source.Skip(i).Take(size).ToList());
        return result;
    }

    private static string Ml(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture);
    private static string Vol(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Quote(string value) => ResumenObraTool.Quote(value);
    private static string Pair(string key, string value) => Quote(key) + ":" + Quote(value);
    private static string Prop(string key, string value) => Quote(key) + ": " + Quote(value);
}
