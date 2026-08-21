using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Services;
using OneSTechLogExporter.Core.State;
using OneSTechLogExporter.Service.Workers;
using Serilog;
using Serilog.Events;

var baseDir = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(baseDir);

var logsDir = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(logsDir);
var emergencyLogPath = Path.Combine(logsDir, "service_startup.log");

// Пишем только сбои и ошибки (Error / Fatal), исключая засорение диска инфо-логами
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Error()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Error)
    .MinimumLevel.Override("System", LogEventLevel.Error)
    .Enrich.FromLogContext()
    .WriteTo.File(
        path: Path.Combine(logsDir, "ones_exporter_errors_.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10485760,
        rollOnFileSizeLimit: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{

    var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "--eventlog", "Exporter:EventLog:Enabled" },
        { "--techlog", "Exporter:TechLog:Enabled" },
        { "--filedump", "Exporter:FileDump:Enabled" },
        { "--filedump-dir", "Exporter:FileDump:DirectoryPath" },
        { "--es", "Exporter:Elastic:ServerUrl" },
        { "--index-id", "Exporter:EventLog:IndexId" }
    };

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = baseDir
    });

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "OneSTechLogExporter";
    });

    builder.Configuration.SetBasePath(baseDir);
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    builder.Configuration.AddCommandLine(args, switchMappings);

    builder.Services.AddSerilog((services, loggerConfiguration) =>
    {
        loggerConfiguration.ReadFrom.Configuration(builder.Configuration);
    });

    var config = builder.Configuration.GetSection(ExporterOptions.SectionName);
    builder.Services.Configure<ExporterOptions>(config);

    var exporterOptions = config.Get<ExporterOptions>() ?? new ExporterOptions();

    builder.Services.AddSingleton(sp => exporterOptions.Elastic);
    builder.Services.AddSingleton(sp => exporterOptions.FileDump);
    builder.Services.AddSingleton<ElasticPublisher>();
    builder.Services.AddSingleton<FileDumper>();
    builder.Services.AddSingleton(sp => new StateTracker(Path.Combine(baseDir, exporterOptions.StateFilePath)));

    builder.Services.AddHostedService<EventLogWorker>();
    builder.Services.AddHostedService<TechLogWorker>();

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Критический сбой при запуске службы OneSTechLogExporter");
    try
    {
        File.AppendAllText(emergencyLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex}\n");
    }
    catch { }
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
