using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoKADN.Proyectos;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Un error inesperado se muestra en vez de dejar la app colgada o cerrarla sin decir nada.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "AutoKADN · Error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        string? shot = Environment.GetEnvironmentVariable("AUTOKADN_DEV_SHOT");
        string? devPhotos = Environment.GetEnvironmentVariable("AUTOKADN_DEV_PHOTOS");
        if (!string.IsNullOrWhiteSpace(shot) && !string.IsNullOrWhiteSpace(devPhotos))
        {
            // Solo desarrollo: captura de la ventana de fotos con una carpeta de prueba.
            var photos = new PhotosWindow(new Services.AppSettings { ResourcesFolder = Services.AppSettings.BundledResourcesFolder }, devPhotos);
            MainWindow = photos;
            photos.ContentRendered += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                var bitmap = new RenderTargetBitmap((int)photos.ActualWidth, (int)photos.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(photos);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(shot)) encoder.Save(stream);
                Shutdown();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            photos.Show();
            return;
        }

        var window = new MainWindow();
        if (!string.IsNullOrWhiteSpace(shot)) PrepareScreenshot(window, shot);

        MainWindow = window;
        window.Show();
    }

    // Solo para desarrollo: AUTOKADN_DEV_SHOT=ruta.png guarda una captura de la ventana y cierra.
    // AUTOKADN_DEV_PDF=ruta.pdf carga esa orden antes; AUTOKADN_DEV_HEIGHT cambia el alto.
    private void PrepareScreenshot(MainWindow window, string path)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("AUTOKADN_DEV_HEIGHT"), out int height)) window.Height = height;
        window.ContentRendered += (_, _) =>
        {
            string? pdf = Environment.GetEnvironmentVariable("AUTOKADN_DEV_PDF");
            if (!string.IsNullOrWhiteSpace(pdf)) window.ViewModel.LoadPdf(pdf);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(path)) encoder.Save(stream);
                Shutdown();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }
}
