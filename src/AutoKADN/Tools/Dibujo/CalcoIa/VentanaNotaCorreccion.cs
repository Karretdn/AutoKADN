using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoKADN.Tools.Dibujo.CalcoIa;

// «Corregir con IA»: qué está mal en la zona señalada, con las palabras del usuario. Enter corrige, Esc cancela.
internal sealed class VentanaNotaCorreccion : Window
{
    private readonly TextBox _nota;
    private readonly Button _corregir;

    public VentanaNotaCorreccion(string zona)
    {
        Title = "AutoKADN - Corregir con IA";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var raiz = new StackPanel { Margin = new Thickness(14) };
        raiz.Children.Add(new TextBlock { Text = zona, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        raiz.Children.Add(new TextBlock { Text = "¿Qué está mal y qué hay que hacer?", Margin = new Thickness(0, 0, 0, 4) });
        _nota = new TextBox { MinHeight = 64, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) };
        raiz.Children.Add(_nota);
        raiz.Children.Add(new TextBlock
        {
            Text = "Ejemplos: «la tubería va del lado de la calle, no sobre la cerca» · «falta la unión del ramal de la derecha» · «la vía de arriba es la CL 12» · «esa manzana de enfrente no existe».",
            Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        raiz.Children.Add(new TextBlock
        {
            Text = "La IA rehace el calco con la corrección y se redibuja: lo que hayas cambiado a mano en el calco se pierde.",
            Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _corregir = new Button { Content = "Corregir", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false };
        _corregir.Click += (_, _) => { DialogResult = true; };
        var cancelar = new Button { Content = "Cancelar", Padding = new Thickness(18, 6, 18, 6), IsCancel = true };
        botones.Children.Add(_corregir);
        botones.Children.Add(cancelar);
        raiz.Children.Add(botones);
        Content = raiz;

        _nota.TextChanged += (_, _) => _corregir.IsEnabled = _nota.Text.Trim().Length > 0;
        Loaded += (_, _) => Keyboard.Focus(_nota);
    }

    public string Nota => _nota.Text.Trim();
}
