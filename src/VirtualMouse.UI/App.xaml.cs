using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Threading;
using VirtualMouse.Core;
using VirtualMouse.Input;
using VirtualMouse.Vision;

namespace VirtualMouse.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch any unhandled exception on the UI thread and show it
        // instead of silently crashing.
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(
                $"Unhandled exception:\n\n{ex.Exception}",
                "Virtual Mouse — Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        base.OnStartup(e);

        try
        {
            var services = new ServiceCollection();

            // Logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Configuration (default settings; overridden by calibration UI)
            services.AddSingleton<TrackingSettings>();

            // Core
            services.AddSingleton<MarkerGrouper>();
            services.AddSingleton<GestureRecognizer>();

            // Vision
            services.AddSingleton<CameraCapture>();
            services.AddSingleton<MarkerDetector>();

            // Input
            services.AddSingleton<WindowsMouseController>();

            // UI — registered last so all dependencies are available
            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Startup failed:\n\n{ex}",
                "Virtual Mouse — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.GetService<WindowsMouseController>()?.ReleaseAll();
        base.OnExit(e);
    }
}
