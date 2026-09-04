using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using OneSTechLogExporter.Core.Services;
using OneSTechLogExporter.Core.State;

namespace OneSTechLogExporter.Service.Workers;

/// <summary>
/// Фоновый воркер регулярного мониторинга и инкрементального экспорта Журнала Регистрации 1С по таймеру.
/// </summary>
public sealed class EventLogWorker(
    IOptions<ExporterOptions> options,
    ElasticPublisher publisher,
    FileDumper fileDumper,
    StateTracker stateTracker,
    ILogger<EventLogWorker> logger) : BackgroundService
{
    private readonly ExporterOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Немедленно отдаем управление SCM хосту Windows, чтобы служба мгновенно рапортовала статус SERVICE_RUNNING
        await Task.Yield();

        if (!_options.EventLog.Enabled || string.IsNullOrWhiteSpace(_options.EventLog.DirectoryPath))
        {
            logger.LogDebug("Мониторинг Журнала Регистрации отключен (EventLog.Enabled = false или не указан DirectoryPath).");
            return;
        }

        var intervalSec = Math.Max(1, _options.PollingIntervalSeconds);
        logger.LogInformation("Запущен регулярный таймер мониторинга Журнала Регистрации 1С. Интервал опроса: {Interval} сек. Режим: Инкрементальный (только новые данные).", intervalSec);
        await stateTracker.LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessEventLogsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в цикле обработки Журнала Регистрации 1С");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Функция сканирования файлов Журнала Регистрации с гибким рекурсивным поиском словаря и файлов событий.
    /// </summary>
    private async ValueTask ProcessEventLogsAsync(CancellationToken ct)
    {
        var rootDir = _options.EventLog.DirectoryPath;
        if (string.IsNullOrWhiteSpace(rootDir))
            return;

        if (!Directory.Exists(rootDir) && !File.Exists(rootDir))
        {
            logger.LogWarning("Каталог/файл Журнала Регистрации не найден: {RootDir}", rootDir);
            return;
        }

        var dictPath = LogDiscovery.FindEventLogDictionary(rootDir);
        LgfDictionary dictionary;
        if (string.IsNullOrEmpty(dictPath) || !File.Exists(dictPath))
        {
            logger.LogInformation("Словарь Журнала Регистрации (1Cv8.lgf) не найден в {RootDir}. Разбор выполняется в автономном режиме.", rootDir);
            dictionary = new LgfDictionary();
        }
        else
        {
            logger.LogDebug("Парсинг словаря 1Cv8.lgf [{DictPath}]...", dictPath);
            dictionary = await EventLogParser.ParseDictionaryAsync(dictPath, ct).ConfigureAwait(false);
        }

        var fileNameFilter = _options.EventLog.FileName;
        var lgpFiles = LogDiscovery.FindEventLogFiles(rootDir, fileNameFilter).ToList();
        if (lgpFiles.Count == 0 && File.Exists(rootDir))
        {
            lgpFiles.Add(rootDir);
        }

        if (lgpFiles.Count == 0)
        {
            logger.LogDebug("Файлы событий Журнала Регистрации (*.lgp) не найдены в {RootDir}", rootDir);
            return;
        }

        foreach (var targetFilePath in lgpFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(targetFilePath);
                if (!fileInfo.Exists)
                {
                    logger.LogDebug("Файл ЖР {FilePath} не существует, пропускаем.", targetFilePath);
                    continue;
                }

                if (!stateTracker.HasFileGrown(targetFilePath, fileInfo.Length))
                {
                    logger.LogDebug("Файл ЖР {FileName} без изменений, пропускаем.", Path.GetFileName(targetFilePath));
                    continue;
                }

                var fileName = Path.GetFileName(targetFilePath);
                var daySuffix = fileName.Length >= 8 ? fileName[..8] : DateTime.UtcNow.ToString("yyyyMMdd");
                var indexName = $"{_options.Elastic.EventLogIndexPrefix}_{_options.EventLog.IndexId}_{daySuffix}";

                var lastPos = stateTracker.GetLastPosition(targetFilePath);
                logger.LogInformation("Инкрементальная обработка файла Журнала Регистрации {FileName} со смещения {LastPos} байт...", fileName, lastPos);

                var (newDocs, newPos) = await EventLogParser.ParseLogFromOffsetAsync(targetFilePath, dictionary, lastPos, ct).ConfigureAwait(false);

                if (newDocs.Count > 0)
                {
                    var totalSuccess = 0;
                    var totalFailed = 0;

                    if (_options.FileDump.Enabled)
                    {
                        await fileDumper.DumpEventLogsAsync(_options.EventLog.IndexId, newDocs, ct).ConfigureAwait(false);
                    }

                    if (_options.Elastic.Enabled)
                    {
                        var (success, failed) = await publisher.BulkIndexEventLogAsync(indexName, newDocs, ct).ConfigureAwait(false);
                        totalSuccess += success;
                        totalFailed += failed;
                    }
                    else
                    {
                        totalSuccess += newDocs.Count;
                    }

                    logger.LogInformation("Файл ЖР {FileName}: отправлено {Count} новых записей [смещение {OldPos} -> {NewPos} байт]. Успешно: {Success}, ошибок: {Failed}", fileName, newDocs.Count, lastPos, newPos, totalSuccess, totalFailed);
                }

                stateTracker.MarkFilePosition(targetFilePath, newPos, fileInfo.Length);
                await stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                logger.LogWarning("Файл ЖР {FilePath} недоступен или удален во время обработки: {Message}", targetFilePath, ex.Message);
            }
        }
    }
}
