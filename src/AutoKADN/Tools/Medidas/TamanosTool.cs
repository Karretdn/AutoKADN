using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AutoKADN.Core;

namespace AutoKADN.Tools.Medidas;

// Ventana "Tamaños": un número por herramienta (texto de Límites, Anotaciones, Nomenclatura vial y predial;
// escala de Cotas y tamaño de los Bloques de material). Al aceptar se guardan, las herramientas los usan en
// lo que dibujen desde ese momento y se actualiza lo que ya está en el dibujo abierto.
public sealed class TamanosTool
{
    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        // Solo se toca el layout que está abierto en este momento (o el modelo si se está en la pestaña Modelo).
        LayoutManager layoutManager = LayoutManager.Current;
        string layoutName = layoutManager.CurrentLayout;
        ObjectId scope;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layout = (Layout)transaction.GetObject(layoutManager.GetLayoutId(layoutName), OpenMode.ForRead);
            scope = layout.BlockTableRecordId;
            transaction.Commit();
        }

        TamanoValores previo = Core.Tamanos.Load();
        TamanoValores propuesta = previo.Clone();
        double? bloquesEnDibujo = TamanosAplicador.CurrentBlockScale(database, scope);
        if (bloquesEnDibujo.HasValue) propuesta.Bloques = bloquesEnDibujo.Value; // se muestra el tamaño que tienen hoy en este layout

        var dialog = new TamanosWindow(propuesta, layoutName);
        try { new System.Windows.Interop.WindowInteropHelper(dialog).Owner = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; } catch { }
        if (dialog.ShowDialog() != true) return;

        TamanoValores nuevo = dialog.Result;
        try
        {
            Core.Tamanos.Save(nuevo);
            TamanosResultado resultado = TamanosAplicador.Aplicar(database, previo, nuevo, scope);
            editor.Regen();
            editor.WriteMessage($"\n[TAMANOS] Guardado. Actualizado solo en el layout '{layoutName}': {resultado.Cotas} cota(s), {resultado.Bloques} bloque(s), "
                + $"{resultado.Anotaciones} anotación(es), {resultado.Limites} límite(s), {resultado.Vial} vial, {resultado.Predial} predial.\n");
            if (resultado.Total == 0) editor.WriteMessage("[TAMANOS] No había nada que cambiar: todo ya tenía esos tamaños.\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n[TAMANOS] ERROR: {ex.Message}\n");
        }
    }

    private sealed class TamanosWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.TextBox _limites, _anotaciones, _vial, _predial, _cotas, _bloques;
        public TamanoValores Result { get; private set; } = new TamanoValores();

        public TamanosWindow(TamanoValores values, string layoutName)
        {
            Title = "AutoKADN - TAMAÑOS";
            Width = 330;
            SizeToContent = System.Windows.SizeToContent.Height;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ResizeMode = System.Windows.ResizeMode.NoResize;

            var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(12) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            for (int i = 0; i < 9; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            AddHeader(grid, 0, "Texto");
            _limites = AddField(grid, 1, "Límites:", values.Limites);
            _anotaciones = AddField(grid, 2, "Anotaciones:", values.Anotaciones);
            _vial = AddField(grid, 3, "Nomenclatura vial:", values.Vial);
            _predial = AddField(grid, 4, "Nomenclatura predial:", values.Predial);
            AddHeader(grid, 5, "Escala");
            _cotas = AddField(grid, 6, "Cotas:", values.Cotas);
            _bloques = AddField(grid, 7, "Bloques de material:", values.Bloques);

            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancelar", Padding = new System.Windows.Thickness(10, 3, 10, 3), Margin = new System.Windows.Thickness(0, 0, 6, 0) };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            buttons.Children.Add(cancel);
            var accept = new System.Windows.Controls.Button { Content = "Aceptar", Padding = new System.Windows.Thickness(10, 3, 10, 3), FontWeight = System.Windows.FontWeights.Bold, IsDefault = true };
            accept.Click += OnAccept;
            buttons.Children.Add(accept);
            System.Windows.Controls.Grid.SetRow(buttons, 8);
            System.Windows.Controls.Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            // Aviso arriba: los cambios solo llegan al layout que está abierto.
            var note = new System.Windows.Controls.TextBlock
            {
                Text = $"Solo cambia lo que está en el layout abierto: {layoutName}.", TextWrapping = System.Windows.TextWrapping.Wrap,
                FontStyle = System.Windows.FontStyles.Italic, Margin = new System.Windows.Thickness(12, 10, 12, 0)
            };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(note);
            panel.Children.Add(grid);
            Content = panel;
            _limites.Focus();
        }

        private static void AddHeader(System.Windows.Controls.Grid grid, int row, string text)
        {
            var header = new System.Windows.Controls.TextBlock { Text = text, FontWeight = System.Windows.FontWeights.Bold, Margin = new System.Windows.Thickness(0, row == 0 ? 0 : 8, 0, 4) };
            System.Windows.Controls.Grid.SetRow(header, row);
            System.Windows.Controls.Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);
        }

        private static System.Windows.Controls.TextBox AddField(System.Windows.Controls.Grid grid, int row, string label, double value)
        {
            var text = new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new System.Windows.Thickness(0, 0, 8, 4) };
            System.Windows.Controls.Grid.SetRow(text, row);
            grid.Children.Add(text);
            var box = new System.Windows.Controls.TextBox { Text = value.ToString("0.######", CultureInfo.InvariantCulture), Padding = new System.Windows.Thickness(3, 1, 3, 1), Margin = new System.Windows.Thickness(0, 0, 0, 4) };
            box.GotFocus += (_, _) => box.SelectAll();
            System.Windows.Controls.Grid.SetRow(box, row);
            System.Windows.Controls.Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        private static bool TryParse(System.Windows.Controls.TextBox box, out double value) =>
            double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0.0;

        private void OnAccept(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!TryParse(_limites, out double limites) || !TryParse(_anotaciones, out double anotaciones) || !TryParse(_vial, out double vial) ||
                !TryParse(_predial, out double predial) || !TryParse(_cotas, out double cotas) || !TryParse(_bloques, out double bloques))
            {
                System.Windows.MessageBox.Show(this, "Escribe números mayores que cero en todos los campos.", "AutoKADN", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            Result = new TamanoValores { Limites = limites, Anotaciones = anotaciones, Vial = vial, Predial = predial, Cotas = cotas, Bloques = bloques };
            DialogResult = true;
            Close();
        }
    }
}
