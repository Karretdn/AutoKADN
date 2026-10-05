using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AutoKADN.Core;

namespace AutoKADN.Tools.Excel;

// Datos del formato FT-T-127 (Totales de tubería legalizada) a partir del consolidado por anillo:
//  - una fila por plano: "ANILLO 1", "ANILLO 2"... y al final "TRONCAL";
//  - 4 bloques de unidad constructiva (tipo de terreno) por hoja, cada uno con 4 columnas de diámetro, de menor a mayor;
//    un terreno con más de 4 diámetros sigue en otro bloque con el mismo nombre;
//  - si hay más terrenos que bloques, o más planos que filas, se generan hojas adicionales con los títulos de fila
//    de todos los planos de esa hoja y los bloques que faltan.
// Se escribe como la lista "ft127.paginas" de resumen_obra.json: la app repite el formato una vez por elemento.
internal static class TotalesTuberiaBuilder
{
    // Medidas del formato impreso (el PDF no se modifica).
    public const int RowsPerPage = 20;
    public const int BlocksPerPage = 4;
    public const int ColumnsPerBlock = 4;

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

    // Propiedad JSON lista para añadir al objeto: "ft127.paginas": [ ... ]
    public static string BuildProperty(ProjectSummary s, string gasificadoIso)
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
                pages.Add(BuildPage(s, rows, pageBlocks, rings, gasificadoIso, first));
                first = false;
            }
        return ResumenObraTool.Quote("ft127.paginas") + ": [\n    " + string.Join(",\n    ", pages) + "\n  ]";
    }

    private static string BuildPage(ProjectSummary s, List<string> rows, List<Block> pageBlocks, List<string> allRings, string gasificadoIso, bool first)
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

        var items = new List<string>();
        foreach (string ring in rows)
        {
            var cells = new List<string> { Pair("fecha", gasificadoIso ?? ""), Pair("plano", ring) };
            Dictionary<UcKey, double> pipes;
            s.RingPipes.TryGetValue(ring, out pipes);
            for (int k = 0; k < columns; k++)
            {
                double ml = 0.0;
                if (column[k].HasValue && pipes != null) pipes.TryGetValue(column[k].Value, out ml);
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
                foreach (string ring in allRings)
                {
                    Dictionary<UcKey, double> pipes; double ml;
                    if (s.RingPipes.TryGetValue(ring, out pipes) && pipes.TryGetValue(column[k].Value, out ml)) total += ml;
                }
            props.Add(Prop("t" + (k + 1), total > 0.0 ? Ml(total) : ""));
        }

        if (first)
        {
            // Tabla "TOTALES DE TUBERIA" (consolidado por diámetro) y observaciones: solo en la primera hoja.
            double all = 0.0;
            foreach (string diameter in DiameterOrder)
            {
                double ml = Consolidated(s, diameter);
                all += ml;
                props.Add(Prop("consolidado_" + diameter.Replace('/', '_'), ml > 0.0 ? Ml(ml) : ""));
            }
            props.Add(Prop("consolidado_total", all > 0.0 ? Ml(all) : ""));
            props.Add(Prop("observaciones", Observations(s, allRings)));
        }
        return "{" + string.Join(", ", props) + "}";
    }

    // ---------- filas ----------

    // ANILLO 1, 2, 3... en orden numérico; otros planos por nombre; TRONCAL al final.
    private static List<string> OrderedRings(ProjectSummary s)
    {
        var rings = s.RingPipes.Where(r => r.Value.Values.Any(v => v > 0.0)).Select(r => r.Key).ToList();
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

    // ---------- bloques ----------

    private static List<Block> BuildBlocks(ProjectSummary s)
    {
        // terreno -> diámetros con cantidad
        var byTerrain = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<UcKey, double> pipes in s.RingPipes.Values)
            foreach (KeyValuePair<UcKey, double> item in pipes)
            {
                if (item.Value <= 0.0 || Array.IndexOf(DiameterOrder, item.Key.Diameter) < 0) continue;
                HashSet<string> set;
                if (!byTerrain.TryGetValue(item.Key.Surface, out set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); byTerrain[item.Key.Surface] = set; }
                set.Add(item.Key.Diameter);
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

    // ---------- totales y observaciones ----------

    private static double Consolidated(ProjectSummary s, string diameter)
    {
        double total = 0.0;
        foreach (Dictionary<UcKey, double> pipes in s.RingPipes.Values)
            foreach (KeyValuePair<UcKey, double> item in pipes)
                if (string.Equals(item.Key.Diameter, diameter, StringComparison.OrdinalIgnoreCase)) total += item.Value;
        return total;
    }

    // Anotaciones especiales, en orden: espiral de válvula, cruce con topo y camisa (tramo que no se canaliza).
    private static string Observations(ProjectSummary s, List<string> rings)
    {
        var lines = new List<string>();
        AddLine(lines, "ESPIRAL DE VALVULA", rings, r => { double v; return s.RingSpiral.TryGetValue(r, out v) ? v : 0.0; });
        AddLine(lines, "CRUCE CON TOPO", rings, r => Activity(s, r, "CRUCE CON TOPO"));
        AddLine(lines, "CAMISA INSTALADA POR LA CONSTRUCTORA", rings, r => Activity(s, r, "CAMISA"));
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
    private static string Quote(string value) => ResumenObraTool.Quote(value);
    private static string Pair(string key, string value) => Quote(key) + ":" + Quote(value);
    private static string Prop(string key, string value) => Quote(key) + ": " + Quote(value);
}
