using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutoKADN.Core;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// Ventana de «Calcar con IA» y «Detallar con IA»: corre AutoKADN.CalcoIA.exe leer <trabajo>, muestra su avance (líneas
// PASO), el tiempo y deja cancelar. Al terminar muestra el resumen, los avisos y las dudas, y pregunta si se dibuja.
// Al detallar, si la IA dijo que la hoja no es la de ese anillo, muestra los motivos y no deja poner nada.
// DialogResult = true solo si terminó bien y el usuario eligió Dibujar (o Poner cotas).
internal sealed class VentanaCalcoIa : Window
{
    private readonly string _ejecutable;
    private readonly string _carpeta;
    private readonly bool _detallar;
    private readonly TextBlock _estado;
    private readonly TextBlock _tiempo;
    private readonly ListBox _registro;
    private readonly StackPanel _final;
    private readonly Button _dibujar;
    private readonly Button _cancelar;
    private readonly DispatcherTimer _reloj;
    private readonly Stopwatch _cronometro = new Stopwatch();
    private Process? _proceso;
    private bool _terminado;

    public VentanaCalcoIa(string ejecutable, string carpeta, bool detallar = false)
    {
        _ejecutable = ejecutable;
        _carpeta = carpeta;
        _detallar = detallar;
        Title = detallar ? "AutoKADN - Detallar con IA" : "AutoKADN - Calcar con IA";
        Width = 640;
        Height = 520;
        MinWidth = 480;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;

        var raiz = new DockPanel { Margin = new Thickness(14) };

        var cabeza = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _tiempo = new TextBlock { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_tiempo, Dock.Right);
        cabeza.Children.Add(_tiempo);
        _estado = new TextBlock { Text = "Preparando…", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        cabeza.Children.Add(_estado);
        DockPanel.SetDock(cabeza, Dock.Top);
        raiz.Children.Add(cabeza);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        _dibujar = new Button { Content = detallar ? "Poner cotas" : "Dibujar", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Visibility = Visibility.Collapsed };
        _dibujar.Click += (_, _) => { DialogResult = true; };
        _cancelar = new Button { Content = "Cancelar", Padding = new Thickness(18, 6, 18, 6), IsCancel = true };
        _cancelar.Click += (_, _) => Close();
        botones.Children.Add(_dibujar);
        botones.Children.Add(_cancelar);
        DockPanel.SetDock(botones, Dock.Bottom);
        raiz.Children.Add(botones);

        _final = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_final, Dock.Bottom);
        raiz.Children.Add(_final);

        _registro = new ListBox { FontSize = 12 };
        raiz.Children.Add(_registro);
        Content = raiz;

        _reloj = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reloj.Tick += (_, _) => _tiempo.Text = _cronometro.Elapsed.ToString(@"m\:ss");
        Loaded += (_, _) => Iniciar();
        Closing += (_, _) => Detener();
    }

    // Lo que dejó el calco (resultado.json) para mostrarlo en la línea de comandos después de dibujar.
    public List<string> Dudas { get; } = new List<string>();
    public List<string> Avisos { get; } = new List<string>();
    public string? Error { get; private set; }
    // Al detallar: por qué la hoja no corresponde al anillo dibujado (vacío si sí corresponde).
    public List<string> Motivos { get; } = new List<string>();

    private void Iniciar()
    {
        try
        {
            var inicio = new ProcessStartInfo(_ejecutable, $"leer \"{_carpeta}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = _carpeta,
            };
            _proceso = new Process { StartInfo = inicio, EnableRaisingEvents = true };
            _proceso.OutputDataReceived += (_, e) => { if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => Linea(e.Data))); };
            _proceso.ErrorDataReceived += (_, e) => { };
            _proceso.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(Termino));
            _proceso.Start();
            _proceso.BeginOutputReadLine();
            _proceso.BeginErrorReadLine();
            _cronometro.Start();
            _reloj.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
        {
            Fallo("no se pudo iniciar AutoKADN.CalcoIA.exe: " + ex.Message);
        }
    }

    private void Linea(string linea)
    {
        if (linea.StartsWith("PASO ", StringComparison.Ordinal))
        {
            string paso = linea.Substring(5).Trim();
            _estado.Text = paso;
            _registro.Items.Add($"{_cronometro.Elapsed:m\\:ss}  {paso}");
            _registro.ScrollIntoView(_registro.Items[_registro.Items.Count - 1]);
        }
        else if (linea.StartsWith("ERROR ", StringComparison.Ordinal)) Error = linea.Substring(6).Trim();
    }

    private void Termino()
    {
        if (_terminado) return;
        _terminado = true;
        _cronometro.Stop();
        _reloj.Stop();
        string resultado = Path.Combine(_carpeta, "resultado.json");
        string salida = _detallar ? "detalle.json" : "plano.json";
        bool rechazado = false;
        try
        {
            if (File.Exists(resultado) && MiniJson.Parse(File.ReadAllText(resultado)) is Dictionary<string, object> estado
                && estado.TryGetValue("estado", out object valor) && valor is string texto && texto == "rechazado")
            {
                rechazado = true;
                Agregar(estado, "motivos", Motivos);
            }
        }
        catch (FormatException) { }
        if (rechazado)
        {
            _terminado = true;
            _cronometro.Stop();
            _reloj.Stop();
            _estado.Text = "La hoja no corresponde a este anillo: no se pone nada.";
            _estado.Foreground = Brushes.Firebrick;
            foreach (string motivo in Motivos) _final.Children.Add(Renglon("• " + motivo));
            _final.Visibility = Visibility.Visible;
            _cancelar.Content = "Cerrar";
            return;
        }
        if (!File.Exists(resultado) || !File.Exists(Path.Combine(_carpeta, salida)))
        {
            Fallo(Error ?? (_detallar ? "terminó sin detalle" : "el calco terminó sin plano"));
            return;
        }

        try
        {
            if (MiniJson.Parse(File.ReadAllText(resultado)) is Dictionary<string, object> datos)
            {
                Agregar(datos, "avisos", Avisos);
                Agregar(datos, "dudas", Dudas);
                var resumen = new List<string>();
                Agregar(datos, "resumen", resumen);
                _final.Children.Add(Titulo(_detallar ? "Listo. Esto se va a poner:" : "Listo. Esto se va a dibujar:"));
                foreach (string linea in resumen) _final.Children.Add(Renglon("• " + linea));
                if (Avisos.Count > 0) { _final.Children.Add(Titulo("Avisos:")); foreach (string a in Avisos) _final.Children.Add(Renglon("• " + a)); }
                if (Dudas.Count > 0) { _final.Children.Add(Titulo("Revise en el dibujo:")); foreach (string d in Dudas) _final.Children.Add(Renglon("• " + d)); }
            }
        }
        catch (FormatException) { /* sin resumen: igual se puede dibujar */ }

        _estado.Text = $"Terminado en {_cronometro.Elapsed:m\\:ss}.";
        _final.Visibility = Visibility.Visible;
        _dibujar.Visibility = Visibility.Visible;
        _dibujar.Focus();
    }

    private void Fallo(string mensaje)
    {
        _terminado = true;
        _cronometro.Stop();
        _reloj.Stop();
        Error = mensaje;
        _estado.Text = (_detallar ? "No se pudo detallar: " : "No se pudo calcar: ") + mensaje;
        _estado.Foreground = Brushes.Firebrick;
        _final.Children.Add(Renglon("Registro del trabajo: " + Path.Combine(_carpeta, "registro.txt")));
        _final.Visibility = Visibility.Visible;
        _cancelar.Content = "Cerrar";
    }

    private void Detener()
    {
        _reloj.Stop();
        if (_proceso == null) return;
        try
        {
            if (!_proceso.HasExited)
            {
                // Se cancela todo: el lector y lo que haya lanzado (Claude Code, el servidor de herramientas).
                using (Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {_proceso.Id}") { CreateNoWindow = true, UseShellExecute = false })) { }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
    }

    private static void Agregar(Dictionary<string, object> datos, string campo, List<string> lista)
    {
        if (datos.TryGetValue(campo, out object valor) && valor is List<object> elementos)
            foreach (object e in elementos) if (e is string s && s.Trim().Length > 0) lista.Add(s.Trim());
    }

    private static TextBlock Titulo(string texto) => new TextBlock { Text = texto, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };

    private static TextBlock Renglon(string texto) => new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 1) };
}
