using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Panel «Importar y calcar»: la hoja escaneada (PDF o imagen, y la página), el plano del proyecto (PDF vectorizado,
// opcional) y qué hacer al aceptar: solo importar (para calcar a mano) o calcar con IA y qué dibujar. Viene con lo último
// que se eligió en el DWG (CalcoMemoria); si la hoja ya está importada en el layout, no se vuelve a importar.
internal sealed class VentanaCalco : Window
{
    private readonly TextBox _hoja;
    private readonly TextBox _pagina;
    private readonly TextBlock _paginas;
    private readonly StackPanel _filaPagina;
    private readonly TextBlock _estadoHoja;
    private readonly TextBox _plano;
    private readonly RadioButton _soloImportar;
    private readonly RadioButton _calcar;
    private readonly CheckBox _bloques;
    private readonly CheckBox _textos;
    private readonly CheckBox _verdes;
    private readonly CheckBox _magenta;
    private readonly CheckBox _aledanas;
    private readonly StackPanel _casillas;
    private readonly Button _aceptar;
    private readonly string _importada;      // archivo de la hoja ya importada en el layout (vacío si no hay)
    private readonly int _paginaImportada;
    private readonly bool _enAnillo;
    private string _contado = string.Empty;   // el PDF cuyas páginas ya se contaron (contar lee el archivo)
    private int _total;

    public VentanaCalco(CalcoMemoria memoria, string importada, int paginaImportada, bool enAnillo)
    {
        _importada = importada;
        _paginaImportada = paginaImportada;
        _enAnillo = enAnillo;
        Title = "AutoKADN - Importar y calcar";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var raiz = new StackPanel { Margin = new Thickness(14) };

        raiz.Children.Add(Titulo("Hoja escaneada (el croquis del gasificado)"));
        _hoja = new TextBox { IsReadOnly = true, Padding = new Thickness(3) };
        raiz.Children.Add(FilaArchivo(_hoja, () => Elegir(_hoja, "Hoja escaneada (*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp"), null));
        _filaPagina = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _filaPagina.Children.Add(new TextBlock { Text = "Página", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _pagina = new TextBox { Width = 50, Padding = new Thickness(3), HorizontalContentAlignment = HorizontalAlignment.Center };
        _filaPagina.Children.Add(_pagina);
        _filaPagina.Children.Add(Boton("−", () => CambiarPagina(-1), 26));
        _filaPagina.Children.Add(Boton("+", () => CambiarPagina(+1), 26));
        _paginas = new TextBlock { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _filaPagina.Children.Add(_paginas);
        raiz.Children.Add(_filaPagina);
        _estadoHoja = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        raiz.Children.Add(_estadoHoja);

        raiz.Children.Add(Titulo("Plano del proyecto (PDF vectorizado, opcional)"));
        _plano = new TextBox { IsReadOnly = true, Padding = new Thickness(3) };
        raiz.Children.Add(FilaArchivo(_plano, () => Elegir(_plano, "Plano del proyecto (*.pdf)|*.pdf"), () => { _plano.Text = string.Empty; }));

        raiz.Children.Add(Titulo("Al aceptar"));
        _soloImportar = new RadioButton { Content = "Solo importar la hoja (para calcar a mano)", GroupName = "que", Margin = new Thickness(0, 2, 0, 2) };
        _calcar = new RadioButton { Content = "Calcar con IA: la geometría (manzana, predios, entorno y tubería) y además…", GroupName = "que", Margin = new Thickness(0, 2, 0, 2) };
        raiz.Children.Add(_soloImportar);
        raiz.Children.Add(_calcar);
        _casillas = new StackPanel { Margin = new Thickness(22, 2, 0, 0) };
        _bloques = Casilla("Bloques (accesorios)");
        _textos = Casilla("Textos de predios");
        _verdes = Casilla("Cotas verdes (longitud de cada tramo)");
        _magenta = Casilla("Cotas magenta y LC (ubicación; otra pasada de la IA)");
        _aledanas = Casilla("Manzanas aledañas desde el plano del proyecto (próximo paso)");
        foreach (CheckBox c in new[] { _bloques, _textos, _verdes, _magenta, _aledanas }) _casillas.Children.Add(c);
        var completo = Boton("Completo", () => { foreach (CheckBox c in new[] { _bloques, _textos, _verdes, _magenta }) c.IsChecked = true; }, 90);
        completo.HorizontalAlignment = HorizontalAlignment.Left;
        completo.Margin = new Thickness(0, 6, 0, 0);
        _casillas.Children.Add(completo);
        raiz.Children.Add(_casillas);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _aceptar = new Button { Content = "Aceptar", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        _aceptar.Click += (_, _) => { if (Valido()) DialogResult = true; };
        var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(18, 6, 18, 6), IsCancel = true };
        botones.Children.Add(_aceptar);
        botones.Children.Add(cancelar);
        raiz.Children.Add(botones);
        Content = raiz;

        // Lo que viene: la hoja importada en este layout manda sobre la última usada en el DWG.
        _hoja.Text = importada.Length > 0 ? importada : memoria.Hoja;
        _pagina.Text = (importada.Length > 0 ? paginaImportada : memoria.Pagina).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _plano.Text = memoria.Plano;
        _bloques.IsChecked = memoria.Bloques;
        _textos.IsChecked = memoria.TextosPredio;
        _verdes.IsChecked = memoria.CotasVerdes;
        _magenta.IsChecked = memoria.CotasMagenta;
        _aledanas.IsChecked = false;
        if (memoria.SoloImportar || !enAnillo) _soloImportar.IsChecked = true; else _calcar.IsChecked = true;
        if (!enAnillo)
        {
            _calcar.IsEnabled = false;
            _calcar.ToolTip = "Calcar con IA se hace en un layout ANILLO n DETALLE";
        }

        _hoja.TextChanged += (_, _) => Actualizar();
        _pagina.TextChanged += (_, _) => Actualizar();
        _plano.TextChanged += (_, _) => Actualizar();
        _soloImportar.Checked += (_, _) => Actualizar();
        _calcar.Checked += (_, _) => Actualizar();
        Loaded += (_, _) => { Actualizar(); _aceptar.Focus(); };
    }

    public string Hoja => _hoja.Text.Trim();
    public int Pagina => int.TryParse(_pagina.Text.Trim(), out int p) && p > 0 ? p : 1;
    public string Plano => _plano.Text.Trim();
    public bool SoloImportar => _soloImportar.IsChecked == true;
    public bool Bloques => _bloques.IsChecked == true;
    public bool TextosPredio => _textos.IsChecked == true;
    public bool CotasVerdes => _verdes.IsChecked == true;
    public bool CotasMagenta => _magenta.IsChecked == true;
    public bool Aledanas => _aledanas.IsChecked == true && _aledanas.IsEnabled;

    // La hoja elegida es la que ya está importada en el layout (mismo archivo y página): no se vuelve a importar.
    public bool YaImportada => _importada.Length > 0 && MismoArchivo(Hoja, _importada) && (!EsPdf(Hoja) || Pagina == _paginaImportada);

    private void Actualizar()
    {
        bool pdf = EsPdf(Hoja);
        _filaPagina.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        if (pdf)
        {
            if (_contado != Hoja) { _contado = Hoja; _total = File.Exists(Hoja) ? CalcoImporter.CountPdfPages(Hoja) : 0; }
            _paginas.Text = _total > 0 ? $"de {_total}" : string.Empty;
        }
        _estadoHoja.Text = Hoja.Length == 0 ? "Elija la hoja."
            : !File.Exists(Hoja) ? "No se encuentra el archivo."
            : YaImportada && !SoloImportar ? "Ya está importada en este layout: se calca esa (no se vuelve a importar)."
            : "Se importa en el marco y luego encierra la zona del croquis (Enter: toda la hoja; [Girar] si está de lado).";
        _casillas.IsEnabled = _calcar.IsChecked == true;
        _aledanas.IsEnabled = false; // se activa cuando la lectura del plano del proyecto esté lista
        _aledanas.ToolTip = Plano.Length == 0 ? "Cargue el plano del proyecto" : "Próximo paso: falta enseñarle a la IA cómo se calcan las aledañas con el plano";
        _aceptar.IsEnabled = Hoja.Length > 0 && File.Exists(Hoja);
    }

    private bool Valido()
    {
        if (Plano.Length > 0 && !File.Exists(Plano))
        {
            System.Windows.MessageBox.Show(this, "No se encuentra el plano del proyecto. Quítelo o elija otro.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void CambiarPagina(int paso)
    {
        int nueva = Math.Max(1, Pagina + paso);
        if (_total > 0) nueva = Math.Min(_total, nueva);
        _pagina.Text = nueva.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Elegir(TextBox destino, string filtro)
    {
        var dialogo = new Microsoft.Win32.OpenFileDialog { Filter = filtro, CheckFileExists = true };
        if (destino.Text.Length > 0)
        {
            try { dialogo.InitialDirectory = Path.GetDirectoryName(destino.Text); } catch (ArgumentException) { }
        }
        if (dialogo.ShowDialog(this) == true) destino.Text = dialogo.FileName;
    }

    private static bool EsPdf(string ruta) => ruta.Length > 0 && CalcoImporter.IsPdf(ruta);

    private static bool MismoArchivo(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static TextBlock Titulo(string texto) => new TextBlock { Text = texto, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };

    private static CheckBox Casilla(string texto) => new CheckBox { Content = texto, Margin = new Thickness(0, 2, 0, 2) };

    private static Button Boton(string texto, Action accion, double ancho)
    {
        var boton = new Button { Content = texto, MinWidth = ancho, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0) };
        boton.Click += (_, _) => accion();
        return boton;
    }

    private Grid FilaArchivo(TextBox caja, Action examinar, Action? quitar)
    {
        var fila = new Grid();
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fila.Children.Add(caja);
        var botonExaminar = Boton("Examinar…", examinar, 80);
        Grid.SetColumn(botonExaminar, 1);
        fila.Children.Add(botonExaminar);
        if (quitar != null)
        {
            var botonQuitar = Boton("Quitar", quitar, 60);
            Grid.SetColumn(botonQuitar, 2);
            fila.Children.Add(botonQuitar);
        }
        caja.MouseDoubleClick += (_, e) => { examinar(); e.Handled = true; };
        return fila;
    }
}
