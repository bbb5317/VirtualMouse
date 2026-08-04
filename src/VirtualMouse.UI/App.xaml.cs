using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using VirtualMouse.Core;
using VirtualMouse.Input;
using VirtualMouse.Vision;

namespace VirtualMouse.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Configuration (default settings; will be overridden by calibration)
        services.AddSingleton<TrackingSettings>();

        // Core
        services.AddSingleton<MarkerGrouper>();
        services.AddSingleton<GestureRecognizer>();

        // Vision
        services.AddSingleton<CameraCapture>();
        services.AddSingleton<MarkerDetector>();

        // Input
        services.AddSingleton<WindowsMouseController>();

        // UI
        services.AddSingleton<MainWindow>();

        Services = services.BuildServiceProvider();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ensure mouse buttons are released on exit
        Services.GetService<WindowsMouseController>()?.ReleaseAll();
        base.OnExit(e);
    }
}
