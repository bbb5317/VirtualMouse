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

            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Settings persistence
            services.AddSingleton<SettingsService>();
            services.AddSingleton<TrackingSettings>(sp =>
                sp.GetRequiredService<SettingsService>().Load());

            // Core
            services.AddSingleton<MarkerGrouper>();
            services.AddSingleton<GestureRecognizer>();

            // Vision
            services.AddSingleton<CameraCapture>();
            services.AddSingleton<MarkerDetector>();
            services.AddSingleton<CameraEnumerator>();

            // Input
            services.AddSingleton<WindowsMouseController>();

            // UI
            services.AddSingleton<MainWindow>();

            Services = services.BuildServiceProvider();
            Services.GetRequiredService<MainWindow>().Show();
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
