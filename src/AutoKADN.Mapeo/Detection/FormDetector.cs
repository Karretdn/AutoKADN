using System.IO;
using AutoKADN.Mapeo.Model;

namespace AutoKADN.Mapeo.Detection;

// Propone el mapa de un formato: reconoce las celdas vacías, las tablas con filas, los espacios para
// escribir sobre una raya, las casillas y los espacios dentro de un párrafo, y les pone nombre a partir
// de los rótulos impresos alrededor. Es una PROPUESTA: todo queda marcado como no revisado.
public static class FormDetector
{
    private const double Touch = 2.6;

    // Si el PDF casi no trae texto real (letras convertidas a curvas), se lee la página con OCR.
    private const int MinReadableWords = 20;

    public static async Task<MapaFormato> DetectAsync(string pdfPath)
    {
        List<PageLayout> pages = PageLayout.Read(pdfPath);
        string fileName = Path.GetFileName(pdfPath);
        string stem = Path.GetFileNameWithoutExtension(pdfPath);
        var map = new MapaFormato
        {
            Version = 1,
            Codigo = ExtractCode(stem),
            Nombre = stem,
            ArchivoPdf = fileName,
            Generado = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (PageLayout page in pages)
        {
            if (page.ReadableWordCount < MinReadableWords)
            {
                if (OcrReader.IsAvailable)
                {
                    AddOcrTokens(page, await OcrReader.ReadAsync(pdfPath, page.Number));
                    map.Notas.Add($"Página {page.Number}: el PDF no trae texto real; los rótulos se leyeron con OCR y pueden tener errores.");
                }
                else map.Notas.Add($"Página {page.Number}: el PDF no trae texto real y el OCR de Windows no está disponible; los rótulos no se pudieron leer.");
            }
            map.Paginas.Add(new PaginaMapa { Numero = page.Number, Ancho = page.Width, Alto = page.Height });
            new PageDetector(page).Run(map);
        }

        Naming.MakeUnique(map.Campos, c => c.Id, (c, id) => c.Id = id);
        Naming.MakeUnique(map.Tablas, t => t.Id, (t, id) => t.Id = id);
        foreach (CampoMapa campo in map.Campos) campo.Fuente = FlowBindings.Suggest(campo.Id);
        return map;
    }

    // Suma las palabras del OCR que no estén ya cubiertas por texto real o por rayas de espacios en blanco.
    private static void AddOcrTokens(PageLayout page, List<Token> ocr)
    {
        var existing = page.Tokens.ToList();
        foreach (Token t in ocr)
        {
            if (!t.Text.Any(char.IsLetterOrDigit) && t.Text.Length < 2) continue;
            if (existing.Any(e => e.Box.Intersects(t.Box))) continue;
            page.Tokens.Add(t);
        }
        page.Tokens.Sort((a, b) =>
        {
            int c = Math.Abs(a.Baseline - b.Baseline) <= 3.0 ? 0 : a.Baseline.CompareTo(b.Baseline);
            return c != 0 ? c : a.Box.X.CompareTo(b.Box.X);
        });
    }

    public static string ExtractCode(string stem)
    {
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"FT-[A-Z]-\d+");
        return match.Success ? match.Value : stem;
    }

    private sealed class PageDetector
    {
        private readonly PageLayout _page;
        private readonly List<Cell> _cells;
        private readonly List<TablaMapa> _tables = new();

        public PageDetector(PageLayout page)
        {
            _page = page;
            _cells = CellGrid.Find(page);
        }

        public void Run(MapaFormato map)
        {
            var consumed = new HashSet<Cell>();
            var fields = new List<CampoMapa>();

            // 1) secciones con rótulo a la izquierda y varias filas (ej. "UNIÓN POLIETILENO": diámetro, cantidad, total)
            foreach (var (table, merged) in FindSections(consumed))
            {
                map.Tablas.Add(table); _tables.Add(table); fields.AddRange(merged);
            }

            // 2) tablas con filas sueltas (registros)
            foreach (TablaMapa table in FindTables(consumed)) { map.Tablas.Add(table); _tables.Add(table); }

            // 3) celdas sueltas
            foreach (Cell cell in _cells)
            {
                if (consumed.Contains(cell)) continue;
                CampoMapa? field = FieldFromCell(cell);
                if (field is not null) fields.Add(field);
            }

            // 4) rayas para firmar / escribir y espacios dentro de un párrafo
            fields.AddRange(UnderlineFields());
            fields.AddRange(BlankTokenFields());

            fields.Sort((a, b) =>
            {
                int c = Math.Abs(a.Y - b.Y) <= 3 ? 0 : a.Y.CompareTo(b.Y);
                return c != 0 ? c : a.X.CompareTo(b.X);
            });
            map.Campos.AddRange(fields);
        }

        // ---------- vecinos ----------

        private Cell? Neighbor(Cell c, char side)
        {
            Cell? best = null; double bestScore = double.MaxValue;
            foreach (Cell o in _cells)
            {
                if (ReferenceEquals(o, c)) continue;
                bool ok;
                double score;
                switch (side)
                {
                    case 'L':
                        ok = Math.Abs(o.Box.Right - c.Box.X) <= Touch && o.Box.Y - 1 <= c.Box.CenterY && c.Box.CenterY <= o.Box.Bottom + 1;
                        score = Math.Abs(o.Box.CenterY - c.Box.CenterY); break;
                    case 'R':
                        ok = Math.Abs(o.Box.X - c.Box.Right) <= Touch && o.Box.Y - 1 <= c.Box.CenterY && c.Box.CenterY <= o.Box.Bottom + 1;
                        score = Math.Abs(o.Box.CenterY - c.Box.CenterY); break;
                    case 'U':
                        ok = Math.Abs(o.Box.Bottom - c.Box.Y) <= Touch && o.Box.X - 1 <= c.Box.CenterX && c.Box.CenterX <= o.Box.Right + 1;
                        score = Math.Abs(o.Box.CenterX - c.Box.CenterX); break;
                    default:
                        ok = Math.Abs(o.Box.Y - c.Box.Bottom) <= Touch && o.Box.X - 1 <= c.Box.CenterX && c.Box.CenterX <= o.Box.Right + 1;
                        score = Math.Abs(o.Box.CenterX - c.Box.CenterX); break;
                }
                if (ok && score < bestScore) { best = o; bestScore = score; }
            }
            return best;
        }

        private static bool HasText(Cell c) => !c.IsEmpty && !c.HasImage && Naming.Clean(c.Text).Length > 0;

        // Celda que solo trae el símbolo Ø (como texto, o como dibujo angosto si el PDF no trae texto real).
        private static bool IsDiameterOnly(Cell c) =>
            c.Tokens.Count > 0 ? Naming.Clean(c.Text) is "Ø" or "ø" : c.InkOnly && c.InkWidth <= 9.5;

        // Rótulos a la izquierda: el más cercano y, si hay, el que agrupa varias filas (texto de una celda más alta).
        private List<string> LeftLabels(Cell c)
        {
            var labels = new List<string>();
            Cell? current = c, primary = null;
            for (int step = 0; step < 40; step++)
            {
                current = Neighbor(current!, 'L');
                if (current is null) break;
                if (!HasText(current)) continue;
                if (primary is null) { primary = current; labels.Insert(0, Naming.Clean(current.Text)); continue; }
                if (current.Box.H >= primary.Box.H * 1.4 || primary.InkOnly || IsDiameterOnly(primary)) { labels.Insert(0, Naming.Clean(current.Text)); break; }
                break;
            }
            return labels;
        }

        // Primer encabezado con texto hacia arriba (salta celdas vacías), siempre que esté alineado con la celda:
        // un encabezado de otra columna no sirve de rótulo.
        private string? HeaderAbove(Cell c)
        {
            Cell? current = c;
            for (int step = 0; step < 30; step++)
            {
                current = Neighbor(current!, 'U');
                if (current is null) return null;
                double overlap = Math.Min(current.Box.Right, c.Box.Right) - Math.Max(current.Box.X, c.Box.X);
                if (overlap < c.Box.W * 0.6) return null;
                if (!HasText(current)) continue;
                string text = Naming.Clean(current.Text);
                return text.Length <= 38 ? text : null;
            }
            return null;
        }

        // ---------- secciones con rótulo (diámetro / cantidad / total por renglón) ----------

        private bool IsInputCell(Cell c) => c.IsEmpty || (IsDiameterOnly(c) && DiameterIsInput(c));

        // Un rótulo de varias filas ("ANILLOS REALIZADOS", "UNIÓN POLIETILENO"…) con celdas a su derecha:
        // las celdas que se repiten por renglón forman una tabla; las que abarcan todas las filas son campos (totales).
        private IEnumerable<(TablaMapa Table, List<CampoMapa> Merged)> FindSections(HashSet<Cell> consumed)
        {
            var labels = _cells.Where(c => c.Tokens.Count > 0 && HasText(c) && !IsDiameterOnly(c) && c.Box.W >= 40 && c.Box.H >= 20)
                .OrderBy(c => c.Box.Y).ThenBy(c => c.Box.X).ToList();
            foreach (Cell label in labels)
            {
                // Celdas encadenadas hacia la derecha dentro de la franja vertical del rótulo.
                var chain = new List<Cell>();
                var seen = new HashSet<Cell> { label };
                var frontier = new List<Cell> { label };
                while (frontier.Count > 0)
                {
                    var next = new List<Cell>();
                    foreach (Cell f in frontier)
                        foreach (Cell o in _cells)
                        {
                            if (seen.Contains(o) || Math.Abs(o.Box.X - f.Box.Right) > Touch) continue;
                            if (o.Box.CenterY < label.Box.Y - 1 || o.Box.CenterY > label.Box.Bottom + 1) continue;
                            seen.Add(o); chain.Add(o); next.Add(o);
                        }
                    frontier = next;
                }

                var inputs = chain.Where(c => !consumed.Contains(c) && IsInputCell(c) && c.Box.W >= 8 && c.Box.H >= 6).ToList();
                if (inputs.Count < 2) continue;
                double rowH = inputs.Min(c => c.Box.H);
                if (label.Box.H < rowH * 1.6) continue;
                var rowCells = inputs.Where(c => c.Box.H < rowH * 1.7).ToList();
                var mergedCells = inputs.Where(c => c.Box.H >= rowH * 1.7).ToList();

                // Columnas = celdas que repiten posición en varios renglones.
                var columns = new List<List<Cell>>();
                foreach (Cell c in rowCells.OrderBy(c => c.Box.X).ThenBy(c => c.Box.Y))
                {
                    var col = columns.FirstOrDefault(k => Math.Abs(k[0].Box.X - c.Box.X) <= 1.8 && Math.Abs(k[0].Box.W - c.Box.W) <= 1.8);
                    if (col is null) columns.Add(new List<Cell> { c }); else col.Add(c);
                }
                columns = columns.Where(k => k.Count >= 2).ToList();
                if (columns.Count == 0) continue;
                var rowsColumn = columns.OrderByDescending(k => k.Count).First().OrderBy(c => c.Box.Y).ToList();
                if (rowsColumn.Count < 2) continue;

                string title = Naming.Clean(label.Text).TrimEnd(':').Trim();
                string tableId = Naming.Slug(title);
                var table = new TablaMapa { Id = tableId.Length > 0 ? tableId : "seccion", Etiqueta = title, Pagina = _page.Number, Origen = "auto" };
                foreach (Cell r in rowsColumn) table.Filas.Add(new FilaMapa { Y = Math.Round(r.Box.Y, 2), Alto = Math.Round(r.Box.H, 2) });

                var tableColumns = new List<ColumnaMapa>();
                foreach (var col in columns.OrderBy(k => k[0].Box.X))
                {
                    Cell first = col.OrderBy(c => c.Box.Y).First();
                    bool diameter = col.All(c => IsDiameterOnly(c));
                    string role = diameter ? "diametro" : "cantidad";
                    Cell? header = HeaderCellAbove(first);
                    string headerText = header is null ? "" : Naming.Clean(header.Text);
                    bool wide = header is not null && header.Box.W >= first.Box.W * 1.5;
                    bool diameterHeader = Naming.Slug(headerText) == "d";
                    string id;
                    if (headerText.Length == 0 || diameterHeader) id = role;
                    else if (wide) id = Naming.Slug(headerText) + "." + role;
                    else id = Naming.Slug(headerText);
                    string roleLabel = diameter ? "Diámetro del accesorio" : "Cantidad";
                    string etiqueta = headerText.Length == 0 || diameterHeader ? roleLabel : wide ? headerText + " › " + roleLabel : headerText;
                    string tipo = diameter ? TipoCampo.Texto : TipoCampo.Numero;
                    tableColumns.Add(new ColumnaMapa
                    {
                        Id = id, Etiqueta = etiqueta, Tipo = tipo, X = Math.Round(first.Box.X, 2), Ancho = Math.Round(first.Box.W, 2),
                        Alineacion = diameter ? Alineacion.Izquierda : Alineacion.Centro, TamanoFuente = SuggestFont(first.Box.H),
                        Prefijo = diameter ? "Ø" : null,
                    });
                    foreach (Cell c in col) consumed.Add(c);
                }
                Naming.MakeUnique(tableColumns, c => c.Id, (c, id) => c.Id = id);
                table.Columnas.AddRange(tableColumns);
                table.X = table.Columnas.Min(c => c.X); table.Ancho = table.Columnas.Max(c => c.X + c.Ancho) - table.X;
                table.Y = table.Filas[0].Y; table.Alto = table.Filas[^1].Y + table.Filas[^1].Alto - table.Y;

                // Celdas que abarcan todas las filas = total de la sección.
                var merged = new List<CampoMapa>();
                foreach (Cell m in mergedCells.OrderBy(c => c.Box.X))
                {
                    Cell? header = HeaderCellAbove(m);
                    string headerText = header is null ? "" : Naming.Clean(header.Text);
                    merged.Add(NewField(table.Id + ".total", title + (headerText.Length > 0 ? " › " + headerText : " › Total"), TipoCampo.Numero, m.Box, 0.7, null, null));
                    consumed.Add(m);
                }
                yield return (table, merged);
            }
        }

        // Celda con texto más cercana hacia arriba y alineada con la celda dada.
        private Cell? HeaderCellAbove(Cell c)
        {
            Cell? current = c;
            for (int step = 0; step < 30; step++)
            {
                current = Neighbor(current!, 'U');
                if (current is null) return null;
                double overlap = Math.Min(current.Box.Right, c.Box.Right) - Math.Max(current.Box.X, c.Box.X);
                if (overlap < c.Box.W * 0.6) return null;
                if (HasText(current)) return current;
            }
            return null;
        }

        // ---------- tablas ----------

        private sealed class RowGroup
        {
            public double Y, H;
            public List<Cell> Cells = new();
            public double Right => Cells[^1].Box.Right;
            public double Left => Cells[0].Box.X;
            public bool Matches(RowGroup o) =>
                Cells.Count == o.Cells.Count && Math.Abs(H - o.H) <= 1.6 &&
                Cells.Zip(o.Cells, (a, b) => Math.Abs(a.Box.X - b.Box.X) <= 1.6 && Math.Abs(a.Box.W - b.Box.W) <= 1.6).All(x => x);
        }

        private IEnumerable<TablaMapa> FindTables(HashSet<Cell> consumed)
        {
            // Filas de celdas vacías contiguas.
            var empties = _cells.Where(c => c.IsEmpty && !consumed.Contains(c) && c.Box.W >= 8 && c.Box.H >= 6).OrderBy(c => c.Box.Y).ThenBy(c => c.Box.X).ToList();
            var rows = new List<List<Cell>>();
            foreach (Cell c in empties)
            {
                var row = rows.FirstOrDefault(r => Math.Abs(r[0].Box.Y - c.Box.Y) <= 1.6 && Math.Abs(r[0].Box.H - c.Box.H) <= 1.6);
                if (row is null) rows.Add(new List<Cell> { c }); else row.Add(c);
            }

            var groups = new List<RowGroup>();
            foreach (var row in rows)
            {
                row.Sort((a, b) => a.Box.X.CompareTo(b.Box.X));
                var group = new RowGroup { Y = row[0].Box.Y, H = row[0].Box.H };
                foreach (Cell c in row)
                {
                    if (group.Cells.Count > 0 && c.Box.X - group.Right > Touch)
                    {
                        groups.Add(group);
                        group = new RowGroup { Y = c.Box.Y, H = c.Box.H };
                    }
                    group.Cells.Add(c);
                }
                groups.Add(group);
            }

            // Una fila con rótulo a la izquierda (ej. "ANILLOS REALIZADOS | Ø | ...") es un renglón fijo, no una tabla.
            groups = groups.Where(g =>
            {
                Cell? left = Neighbor(g.Cells[0], 'L');
                return left is null || !HasText(left);
            }).OrderBy(g => g.Y).ThenBy(g => g.Left).ToList();

            var runs = new List<List<RowGroup>>();
            foreach (RowGroup g in groups)
            {
                var run = runs.FirstOrDefault(r => r[^1].Matches(g) && Math.Abs(g.Y - (r[^1].Y + r[^1].H)) <= Touch);
                if (run is null) runs.Add(new List<RowGroup> { g }); else run.Add(g);
            }

            foreach (var run in runs.Where(r => r.Count >= 4))
            {
                foreach (RowGroup g in run) foreach (Cell c in g.Cells) consumed.Add(c);
                yield return BuildTable(run);
            }
        }

        private TablaMapa BuildTable(List<RowGroup> run)
        {
            RowGroup first = run[0];
            var box = new RectD(first.Left, first.Y, first.Right - first.Left, run[^1].Y + run[^1].H - first.Y);

            var table = new TablaMapa
            {
                Pagina = _page.Number,
                X = box.X, Y = box.Y, Ancho = box.W, Alto = box.H,
                Origen = "auto",
            };
            foreach (RowGroup g in run) table.Filas.Add(new FilaMapa { Y = g.Y, Alto = g.H });

            Cell topLeft = first.Cells[0];
            string? title = SpanningTitle(first, box);
            table.Etiqueta = title ?? "Registros";
            table.Id = Naming.Slug(title ?? "registros");
            if (table.Id.Length == 0) table.Id = "registros";

            var columns = new List<ColumnaMapa>();
            foreach (Cell cell in first.Cells)
            {
                List<string> chain = ColumnChain(cell);
                string label = chain.Count > 0 ? string.Join(" › ", chain) : "Columna";
                string id = chain.Count > 0 ? Naming.Join(chain) : "columna";
                string tipo = GuessKind(id);
                columns.Add(new ColumnaMapa
                {
                    Id = id,
                    Etiqueta = label,
                    Tipo = tipo,
                    X = cell.Box.X,
                    Ancho = cell.Box.W,
                    Alineacion = tipo == TipoCampo.Texto ? Alineacion.Izquierda : Alineacion.Centro,
                    TamanoFuente = SuggestFont(first.H),
                });
            }
            Naming.MakeUnique(columns, c => c.Id, (c, id) => c.Id = id);
            table.Columnas.AddRange(columns);
            _ = topLeft;
            return table;
        }

        // Rótulo que cubre todo el ancho de la tabla justo arriba (ej. "TOTALES UNIDAD CONSTRUCTIVA ML").
        private string? SpanningTitle(RowGroup first, RectD box)
        {
            Cell? current = first.Cells[0];
            for (int step = 0; step < 12; step++)
            {
                current = Neighbor(current!, 'U');
                if (current is null) return null;
                if (HasText(current) && current.Box.X <= box.X + Touch && current.Box.Right >= box.Right - Touch)
                {
                    string t = Naming.Clean(current.Text);
                    return t.Length <= 60 ? t : null;
                }
            }
            return null;
        }

        // Encabezados pegados encima de una columna, de arriba hacia abajo. Si un encabezado abarca varias
        // columnas y se repite (ej. "UNIDAD CONSTRUCTIVA" ×4) se le agrega su número.
        private List<string> ColumnChain(Cell column)
        {
            var chain = new List<string>();
            Cell? current = column;
            for (int step = 0; step < 8; step++)
            {
                current = Neighbor(current!, 'U');
                if (current is null) break;
                if (!HasText(current)) continue;
                string text = Naming.Clean(current.Text);
                if (current.Box.W >= column.Box.W * 1.5)
                {
                    var siblings = _cells.Where(o => HasText(o) && Naming.Clean(o.Text) == text && Math.Abs(o.Box.Y - current.Box.Y) <= 2).OrderBy(o => o.Box.X).ToList();
                    if (siblings.Count > 1) text += " " + (siblings.IndexOf(current) + 1);
                }
                chain.Insert(0, text);
                if (chain.Count >= 3) break;
            }
            return chain;
        }

        // ---------- celdas sueltas ----------

        private CampoMapa? FieldFromCell(Cell cell)
        {
            RectD b = cell.Box;
            if (b.W < 6 || b.H < 6) return null;
            if (b.W * b.H > _page.Width * _page.Height * 0.5) return null;

            // Celda con rótulo y mucho espacio libre (OBSERVACIONES): se escribe debajo del rótulo.
            if (HasText(cell) && IsNotesBox(cell, out double labelBottom))
            {
                string label = Naming.Clean(cell.Text).TrimEnd(':').Trim();
                return NewField(Naming.Slug(label), label, TipoCampo.Multilinea,
                    new RectD(b.X + 2, labelBottom + 1, b.W - 4, b.Bottom - labelBottom - 3), 0.7, null, null);
            }

            bool diameter = IsDiameterOnly(cell);
            if (!cell.IsEmpty && !diameter) return null;
            if (diameter && !DiameterIsInput(cell)) return null;

            // Casilla de marcar.
            bool box = b.W >= 8 && b.W <= 24 && b.H >= 8 && b.H <= 24 && Math.Abs(b.W - b.H) <= 4 && cell.IsEmpty;
            if (box)
            {
                string left = WordsLeftOf(b, 3);
                string right = WordsRightOf(b, 1);
                string name = left.Length > 0 ? left : right;
                return NewField(Naming.Slug(name).Length > 0 ? Naming.Slug(name) : "casilla", name.Length > 0 ? name : "Casilla", TipoCampo.Check, b, left.Length > 0 ? 0.7 : 0.3, null, Context(b));
            }

            List<string> labels = LeftLabels(cell);
            ColumnaMapa? tableColumn = ColumnBelowTable(cell);
            string? header = tableColumn is null ? HeaderAbove(cell) : null;
            var parts = new List<string>(labels);
            if (header is not null && !parts.Contains(header)) parts.Add(header);
            string id = Naming.Join(parts);
            string etiqueta = parts.Count > 0 ? string.Join(" › ", parts) : "Sin rótulo";
            if (tableColumn is not null)
            {
                // Renglones de totales justo debajo de una tabla: comparten el nombre de la columna.
                id = id.Length > 0 ? id + "." + tableColumn.Id : tableColumn.Id;
                etiqueta = (parts.Count > 0 ? etiqueta + " › " : "") + tableColumn.Etiqueta;
            }
            if (id.Length == 0) id = "sin_nombre";
            double confidence = labels.Count > 0 ? 0.8 : header is not null ? 0.6 : 0.2;

            string kind = diameter ? TipoCampo.Texto : GuessKind(id);
            CampoMapa field = NewField(id, etiqueta, kind, b, confidence, diameter ? "Ø" : null, null);
            if (diameter) field.Etiqueta = etiqueta + " (Ø)";
            return field;
        }

        private ColumnaMapa? ColumnBelowTable(Cell cell)
        {
            foreach (TablaMapa table in _tables)
            {
                double bottom = table.Y + table.Alto;
                double rowHeight = table.Filas.Count > 0 ? table.Filas.Average(f => f.Alto) : 12;
                if (cell.Box.Y < bottom - 2 || cell.Box.Y - bottom > rowHeight * 3) continue;
                foreach (ColumnaMapa column in table.Columnas)
                    if (Math.Abs(column.X - cell.Box.X) <= 1.8 && Math.Abs(column.Ancho - cell.Box.W) <= 1.8) return column;
            }
            return null;
        }

        private bool DiameterIsInput(Cell cell)
        {
            Cell? right = Neighbor(cell, 'R'), left = Neighbor(cell, 'L'), below = Neighbor(cell, 'D');
            // Entre dos rótulos de texto ("Unidad Constructiva | Ø | ML") es el encabezado de la columna.
            if (right is not null && left is not null && HasText(right) && HasText(left) && !IsDiameterOnly(right) && !IsDiameterOnly(left) && !right.InkOnly && !left.InkOnly)
                return false;
            return (right is not null && right.IsEmpty) || (left is not null && left.IsEmpty) || (below is not null && below.IsEmpty);
        }

        private bool IsNotesBox(Cell cell, out double labelBottom)
        {
            labelBottom = 0;
            if (cell.Box.H < 24 || cell.Box.W < 120 || cell.Tokens.Count == 0 || cell.Tokens.Count > 3) return false;
            double textArea = cell.Tokens.Sum(t => t.Box.W * t.Box.H);
            if (textArea > cell.Box.W * cell.Box.H * 0.15) return false;
            RectD first = cell.Tokens[0].Box;
            if (first.Y > cell.Box.Y + cell.Box.H * 0.35) return false;
            labelBottom = cell.Tokens.Max(t => t.Box.Bottom);
            return true;
        }

        // ---------- rayas ----------

        private IEnumerable<CampoMapa> UnderlineFields()
        {
            var result = new List<(CampoMapa Field, string? Group, bool Inherited)>();
            foreach (Seg line in MergedHorizontals())
            {
                double length = line.B - line.A;
                if (length < 28 || IsCellEdge(line)) continue;

                string left = TokensOnLineLeftOf(line);
                string below = TokensBelow(line);
                string above = left.Length == 0 && below.Length == 0 ? TokensAbove(line) : "";

                string? group = left.Length > 0 ? left : null;
                string labelText = left.Length > 0 ? left : "";
                string? inheritedFrom = null;
                if (left.Length == 0)
                {
                    // Raya sin rótulo propio (ej. "Nombre" / "FIRMA" bajo la raya): hereda el de la raya vecina.
                    foreach (var prior in result.AsEnumerable().Reverse())
                    {
                        if (prior.Group is null) continue;
                        bool sameRow = Math.Abs(prior.Field.Y - (line.Pos - 14)) <= 4;
                        if (sameRow) { inheritedFrom = prior.Group; break; }
                    }
                }

                var pieces = new List<string>();
                if (labelText.Length > 0) pieces.Add(labelText);
                else if (inheritedFrom is not null) pieces.Add(inheritedFrom);
                if (below.Length > 0) pieces.Add(below);
                if (pieces.Count == 0 && above.Length > 0) pieces.Add(above);

                string etiqueta = pieces.Count > 0 ? string.Join(" › ", pieces) : "Sin rótulo";
                string id = pieces.Count > 0 ? Naming.Join(pieces) : "linea";
                double confidence = left.Length > 0 || below.Length > 0 ? 0.7 : 0.3;
                RectD rect = new(line.A, line.Pos - 14, length, 14);
                CampoMapa field = NewField(id.Length > 0 ? id : "linea", etiqueta, GuessKind(id), rect, confidence, null, null);
                result.Add((field, group ?? inheritedFrom, left.Length == 0));
            }
            return result.Select(r => r.Field);
        }

        private List<Seg> _mergedH = null!;
        private List<Seg> MergedHorizontals() => _mergedH ??= CellGrid.Merge(_page.Horizontals);

        private bool IsCellEdge(Seg line)
        {
            foreach (Cell cell in _cells)
            {
                bool edge = Math.Abs(cell.Box.Y - line.Pos) <= 2.2 || Math.Abs(cell.Box.Bottom - line.Pos) <= 2.2;
                if (!edge) continue;
                double overlap = Math.Min(cell.Box.Right, line.B) - Math.Max(cell.Box.X, line.A);
                if (overlap > 0.4 * Math.Min(cell.Box.W, line.B - line.A)) return true;
            }
            return false;
        }

        private string TokensOnLineLeftOf(Seg line)
        {
            var onLine = _page.Tokens.Where(t => !t.IsBlank && t.Baseline >= line.Pos - 14 && t.Baseline <= line.Pos + 5 && t.Box.Right <= line.A + 1.5)
                .OrderByDescending(t => t.Box.X).ToList();
            var picked = new List<Token>();
            double edge = line.A;
            foreach (Token t in onLine)
            {
                if (edge - t.Box.Right > (picked.Count == 0 ? 70 : 14)) break;
                picked.Insert(0, t);
                edge = t.Box.X;
                if (picked.Count >= 6) break;
            }
            picked = picked.OrderBy(t => Math.Round(t.Baseline / 3)).ThenBy(t => t.Box.X).ToList();
            return Naming.Clean(string.Join(" ", picked.Select(t => t.Text))).TrimEnd(':').Trim();
        }

        private string TokensBelow(Seg line)
        {
            var below = _page.Tokens.Where(t => !t.IsBlank && t.Box.Y >= line.Pos - 1 && t.Box.Y <= line.Pos + 9 && t.Box.CenterX >= line.A - 2 && t.Box.CenterX <= line.B + 2)
                .OrderBy(t => t.Box.X).ToList();
            if (below.Count == 0) return "";
            double baseline = below[0].Baseline;
            var row = _page.Tokens.Where(t => !t.IsBlank && Math.Abs(t.Baseline - baseline) <= 2.5).ToList();
            if (row.Min(t => t.Box.X) < line.A - 14 || row.Max(t => t.Box.Right) > line.B + 14) return "";
            return Naming.Clean(string.Join(" ", below.Select(t => t.Text)));
        }

        private string TokensAbove(Seg line)
        {
            var above = _page.Tokens.Where(t => !t.IsBlank && t.Baseline >= line.Pos - 30 && t.Baseline < line.Pos - 14 && t.Box.CenterX >= line.A - 2 && t.Box.CenterX <= line.B + 2)
                .OrderBy(t => t.Box.X).ToList();
            return Naming.Clean(string.Join(" ", above.Select(t => t.Text)));
        }

        // ---------- espacios dentro de un párrafo ("A los _____ días del mes de ____") ----------

        private IEnumerable<CampoMapa> BlankTokenFields()
        {
            var tokens = _page.Tokens;
            var fields = new List<CampoMapa>();
            // Renglones de texto.
            var lines = new List<List<Token>>();
            foreach (Token t in tokens)
            {
                var line = lines.LastOrDefault();
                if (line is not null && Math.Abs(line[0].Baseline - t.Baseline) <= 2.0) line.Add(t); else lines.Add(new List<Token> { t });
            }

            var pendingMulti = new List<(Token Token, int Line)>();
            string multiLabel = "";
            string lastGroupLabel = ""; double lastGroupBaseline = double.MinValue; double lastGroupX = 0;

            void FlushMulti()
            {
                if (pendingMulti.Count == 0) return;
                var rects = pendingMulti.Select(p => new RectMapa { X = p.Token.Box.X, Y = p.Token.Baseline - 11, Ancho = p.Token.Box.W, Alto = 13 }).ToList();
                double x = rects.Min(r => r.X), y = rects.Min(r => r.Y);
                double right = rects.Max(r => r.X + r.Ancho), bottom = rects.Max(r => r.Y + r.Alto);
                string id = Naming.Slug(multiLabel);
                CampoMapa f = NewField(id.Length > 0 ? id : "texto_libre", multiLabel.Length > 0 ? multiLabel : "Texto libre", TipoCampo.Multilinea,
                    new RectD(x, y, right - x, bottom - y), 0.6, null, multiLabel.Length > 0 ? multiLabel + " [ … ]" : null);
                f.Lineas = rects;
                fields.Add(f);
                pendingMulti.Clear();
                multiLabel = "";
            }

            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                var nonBlank = line.Where(t => !t.IsBlank).ToList();
                var blanks = line.Where(t => t.IsBlank).ToList();
                bool onlyBlanks = blanks.Count > 0 && nonBlank.Count == 0;

                // Renglones que son solo rayas, uno tras otro = un cuadro de texto libre de varias líneas.
                if (onlyBlanks && blanks.Sum(b => b.Box.W) >= 120 && blanks.All(b => CaptionBelow(b).Length == 0))
                {
                    bool continues = pendingMulti.Count > 0 && Math.Abs(line[0].Baseline - pendingMulti[^1].Token.Baseline) <= 22;
                    if (!continues) FlushMulti();
                    if (pendingMulti.Count == 0) PullPartialLineAbove(li);
                    foreach (Token b in blanks) pendingMulti.Add((b, li));
                    continue;
                }
                FlushMulti();

                foreach (Token blank in blanks)
                {
                    int index = tokens.IndexOf(blank);
                    var prev = new List<string>();
                    for (int k = index - 1; k >= 0 && prev.Count < 4; k--)
                    {
                        if (tokens[k].IsBlank) break;
                        if (blank.Baseline - tokens[k].Baseline > 20) break;   // no tomar palabras de un párrafo anterior
                        string word = tokens[k].Text;
                        if (!word.Any(char.IsLetterOrDigit)) continue;
                        prev.Insert(0, word);
                    }
                    var next = new List<string>();
                    for (int k = index + 1; k < tokens.Count && next.Count < 2; k++)
                    {
                        if (tokens[k].IsBlank) break;
                        if (!tokens[k].Text.Any(char.IsLetterOrDigit)) continue;
                        next.Add(tokens[k].Text);
                    }

                    string caption = CaptionBelow(blank);
                    bool signature = caption.Length > 0;
                    string nameLabel;
                    string context = string.Join(" ", prev.TakeLast(3)) + " [ … ] " + string.Join(" ", next);

                    if (signature)
                    {
                        // Raya de firma: "Por el Contratista ______ / Nombre – C.C.".
                        string head = nonBlank.Count > 0 && nonBlank.Last().Box.Right <= blank.Box.X + 2 ? Naming.Clean(string.Join(" ", nonBlank.Where(t => t.Box.Right <= blank.Box.X + 2).Select(t => t.Text))) : "";
                        if (head.Length == 0 && Math.Abs(blank.Baseline - lastGroupBaseline) <= 90 && Math.Abs(blank.Box.X - lastGroupX) <= 40) head = lastGroupLabel;
                        else if (head.Length > 0) { lastGroupLabel = head; lastGroupBaseline = blank.Baseline; lastGroupX = blank.Box.X; }
                        nameLabel = head.Length > 0 ? head + " › " + caption : caption;
                        context = nameLabel;
                    }
                    else
                    {
                        var words = prev.Count > 0 ? prev.TakeLast(3) : next;
                        nameLabel = string.Join(" ", words);
                    }

                    string id = Naming.Slug(nameLabel.Replace(" › ", " "));
                    if (signature) id = Naming.Join(nameLabel.Split(" › "));
                    var rect = new RectD(blank.Box.X, blank.Baseline - 11, blank.Box.W, 13);
                    fields.Add(NewField(id.Length > 0 ? id : "espacio", nameLabel.Length > 0 ? nameLabel : "Espacio", GuessKind(id), rect, signature ? 0.6 : 0.5, null, context));
                }
            }
            FlushMulti();
            return fields;

            // "debido a: ______" — la raya parcial del renglón anterior pertenece al mismo cuadro.
            void PullPartialLineAbove(int lineIndex)
            {
                List<Token>? above = null; Token? last = null;
                for (int back = 1; back <= 2 && lineIndex - back >= 0; back++)
                {
                    var candidate = lines[lineIndex - back];
                    Token? tail = candidate.LastOrDefault();
                    if (tail is null || !tail.IsBlank || tail.Box.W < 100) continue;
                    if (line0Baseline(lineIndex) - tail.Baseline > 30) break;
                    above = candidate; last = tail; break;
                }
                if (above is null || last is null) return;
                // Texto que precede a la raya, p. ej. "se retomó el día ____ debido a:"
                var words = above.Where(t => !t.IsBlank && t.Box.Right <= last.Box.X + 1).Select(t => t.Text).ToList();
                multiLabel = string.Join(" ", words.TakeLast(2));
                pendingMulti.Add((last, lineIndex - 1));
                // Esa raya ya se había contado como un espacio suelto: se retira.
                fields.RemoveAll(f => Math.Abs(f.X - last.Box.X) < 1 && Math.Abs(f.Y - (last.Baseline - 11)) < 1);
            }

            double line0Baseline(int i) => lines[i][0].Baseline;
        }

        // Rótulo centrado justo debajo de una raya ("Firma", "Nombre – C.C."). Si el renglón de abajo es un
        // párrafo que sigue (se extiende mucho más allá de la raya), no es un rótulo.
        private string CaptionBelow(Token blank)
        {
            var below = _page.Tokens.Where(t => !t.IsBlank && t.Box.Y >= blank.Baseline && t.Box.Y <= blank.Baseline + 16 &&
                                                 t.Box.CenterX >= blank.Box.X - 2 && t.Box.CenterX <= blank.Box.Right + 2).OrderBy(t => t.Box.X).ToList();
            if (below.Count == 0) return "";
            double baseline = below[0].Baseline;
            var row = _page.Tokens.Where(t => !t.IsBlank && Math.Abs(t.Baseline - baseline) <= 2.5).ToList();
            if (row.Min(t => t.Box.X) < blank.Box.X - 14 || row.Max(t => t.Box.Right) > blank.Box.Right + 14) return "";
            return Naming.Clean(string.Join(" ", below.Select(t => t.Text)));
        }

        // ---------- utilidades ----------

        private string WordsLeftOf(RectD box, int max)
        {
            var words = _page.Tokens.Where(t => !t.IsBlank && Math.Abs(t.Box.CenterY - box.CenterY) <= 7 && t.Box.Right <= box.X + 1 && box.X - t.Box.Right < 40)
                .OrderByDescending(t => t.Box.X).Take(max).Reverse().Select(t => t.Text);
            return Naming.Clean(string.Join(" ", words)).Trim('/', ' ');
        }

        private string WordsRightOf(RectD box, int max)
        {
            var words = _page.Tokens.Where(t => !t.IsBlank && Math.Abs(t.Box.CenterY - box.CenterY) <= 7 && t.Box.X >= box.Right - 1 && t.Box.X - box.Right < 40)
                .OrderBy(t => t.Box.X).Take(max).Select(t => t.Text);
            return Naming.Clean(string.Join(" ", words)).Trim('/', ' ');
        }

        private string Context(RectD box) => (WordsLeftOf(box, 4) + " [ ] " + WordsRightOf(box, 3)).Trim();

        private CampoMapa NewField(string id, string etiqueta, string tipo, RectD rect, double confidence, string? prefijo, string? contexto) => new()
        {
            Id = id,
            Etiqueta = etiqueta,
            Contexto = contexto,
            Tipo = tipo,
            Pagina = _page.Number,
            X = Math.Round(rect.X, 2), Y = Math.Round(rect.Y, 2), Ancho = Math.Round(rect.W, 2), Alto = Math.Round(rect.H, 2),
            Alineacion = tipo == TipoCampo.Multilinea ? Alineacion.Izquierda : Alineacion.Centro,
            TamanoFuente = SuggestFont(rect.H),
            Prefijo = prefijo,
            Origen = "auto",
            Confianza = confidence,
            Revisado = false,
        };

        private static double SuggestFont(double height) => Math.Round(Math.Clamp(height * 0.62, 6.0, 10.0), 1);

        private static string GuessKind(string id)
        {
            if (id.Contains("fecha")) return TipoCampo.Fecha;
            string[] numeric = { "ml", "cantidad", "total", "m3", "no", "numero", "subtotal", "anillos", "tuberia", "diametro" };
            foreach (string part in id.Split('.', '_'))
                if (numeric.Contains(part) && !id.Contains("orden")) return TipoCampo.Numero;
            return TipoCampo.Texto;
        }
    }
}
