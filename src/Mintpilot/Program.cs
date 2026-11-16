using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Mintpilot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run();
        if (args.Contains("--connection-probe")) return ConnectionProbe.Run();
        bool smoke = args.Contains("--ui-smoke");
        using var instance = new Mutex(true, smoke ? "Local\\Mintpilot.Native.Smoke." + Guid.NewGuid() : "Local\\Mintpilot.Native", out bool first);
        if (!first) { MessageBox.Show("Mintpilot is already running.", "Mintpilot"); return 0; }
        try
        {
            var app = new Application();
            var vault = new Vault(smoke ? Path.Combine(Path.GetTempPath(), "Mintpilot-Smoke-" + Guid.NewGuid()) : null);
            var window = new MainWindow(new Engine(vault), smoke);
            app.DispatcherUnhandledException += (_, e) =>
            {
                MessageBox.Show(e.Exception.Message, "Mintpilot", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            if (smoke)
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        foreach (string page in new[] { "Overview", "Launches", "Promotions", "Wallet", "Settings" })
                        { window.ShowPage(page); window.UpdateLayout(); }
                        window.ShowPage("Overview"); window.UpdateLayout();
                        if (window.ActualWidth < 1000) throw new InvalidOperationException("Native window layout failed.");
                        string target = Path.GetFullPath("ui-smoke.png");
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using (var output = File.Create(target)) encoder.Save(output);
                        File.WriteAllText("ui-smoke-result.txt", "PASS Native WPF startup, five navigation views, local wallet creation, and render.");
                        app.Shutdown(0);
                    }
                    catch (Exception e) { File.WriteAllText("ui-smoke-result.txt", e.ToString()); app.Shutdown(1); }
                }));
            return app.Run(window);
        }
        catch (Exception ex)
        {
            if (smoke) { File.WriteAllText("ui-smoke-result.txt", ex.ToString()); return 1; }
            MessageBox.Show(ex.Message, "Mintpilot could not open", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
        }
    }
}
