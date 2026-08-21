using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace OneSTechLogExporter.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory);

            if (File.Exists(configPath))
            {
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            }

            var configuration = builder.Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .WriteTo.File(
                    path: "logs/ones_gui_.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10485760,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("Графическое приложение OneSTechLogExporter GUI успешно запущено.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка инициализации логгера GUI: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
