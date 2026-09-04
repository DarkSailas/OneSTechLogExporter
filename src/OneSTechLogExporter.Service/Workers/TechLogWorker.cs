using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using OneSTechLogExporter.Core.Services;
using OneSTechLogExporter.Core.State;

namespace OneSTechLogExporter.Service.Workers;

/// <summary>
/// Фоновый воркер регулярного мониторинга и инкрементального экспорта Технологического Журнала 1С по таймеру.
/// </summary>
public sealed class TechLogWorker(
    IOptions<ExporterOptions> options,
    ElasticPublisher publisher,
    FileDumper fileDumper,
    StateTracker stateTracker,
    ILogger<TechLogWorker> logger) : BackgroundService
{
    private readonly ExporterOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Немедленно отдаем управление SCM хосту Windows, чтобы служба мгновенно рапортовала статус SERVICE_RUNNING
        await Task.Yield();

        if (!_options.TechLog.Enabled || string.IsNullOrWhiteSpace(_options.TechLog.DirectoryPath))
        {
            logger.LogDebug("Мониторинг Технологического Журнала отключен (TechLog.Enabled = false или не указан DirectoryPath).");
            return;
        }

        var intervalSec = Math.Max(1, _options.PollingIntervalSeconds);
        logger.LogInformation("Запущен регулярный таймер мониторинга Технологического Журнала 1С. Интервал опроса: {Interval} сек. Режим: Инкрементальный (только новые данные).", intervalSec);
        await stateTracker.LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTechLogsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в цикле обработки Технологического Журнала 1С");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Функция сканирования файлов ТЖ с произвольной вложенностью каталогов и инкрементальной отправки новых записей.
    /// </summary>
    private async ValueTask ProcessTechLogsAsync(CancellationToken ct)
    {
        var rootDir = _options.TechLog.DirectoryPath;
        if (string.IsNullOrWhiteSpace(rootDir))
            return;

        if (!Directory.Exists(rootDir) && !File.Exists(rootDir))
        {
            logger.LogWarning("Каталог/файл Технологического Журнала не найден: {RootDir}", rootDir);
            return;
        }

        var logItems = LogDiscovery.FindTechLogFiles(rootDir).ToList();
        if (logItems.Count == 0 && File.Exists(rootDir))
        {
            var (pName, pId) = LogDiscovery.ParseProcessInfo(rootDir);
            logItems.Add((rootDir, pName, pId, Path.GetFileName(Path.GetDirectoryName(rootDir) ?? "default") ?? "default"));
        }

        if (logItems.Count == 0)
        {
            logger.LogDebug("Файлы Технологического Журнала (*.log) не найдены в {RootDir}", rootDir);
            return;
        }

        foreach (var (filePath, processName, processId, folderName) in logItems)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    logger.LogDebug("Файл ТЖ {FilePath} не существует (удален или ротирован 1С), пропускаем.", filePath);
                    continue;
                }

                if (!stateTracker.HasFileGrown(filePath, fileInfo.Length))
                    continue;

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (fileName.Length < 8) continue;

                var daySuffix = "20" + fileName[..6];
                var indexName = $"{_options.Elastic.TechLogIndexPrefix}_{_options.TechLog.IndexId}_{daySuffix}";

                var lastPos = stateTracker.GetLastPosition(filePath);
                logger.LogDebug("Инкрементальный разбор файла ТЖ {FileName} (процесс {ProcessName}_{ProcessId}) со смещения {LastPos} байт...", Path.GetFileName(filePath), processName, processId, lastPos);

                var (newDocs, newPos) = await TechLogParser.ParseFileFromOffsetAsync(filePath, processName, processId, lastPos, ct).ConfigureAwait(false);

                if (newDocs.Count > 0)
                {
                    var totalSuccess = 0;
                    var totalFailed = 0;

                    if (_options.FileDump.Enabled)
                    {
                        await fileDumper.DumpTechLogsAsync(folderName, newDocs, ct).ConfigureAwait(false);
                    }

                    if (_options.Elastic.Enabled)
                    {
                        var (success, failed) = await publisher.BulkIndexTechLogAsync(indexName, newDocs, ct).ConfigureAwait(false);
                        totalSuccess += success;
                        totalFailed += failed;
                    }
                    else
                    {
                        totalSuccess += newDocs.Count;
                    }

                    logger.LogInformation("Файл ТЖ {FileName} (папка {FolderName}): отправлено {Count} новых записей [смещение {OldPos} -> {NewPos} байт]. Успешно: {Success}, ошибок: {Failed}", Path.GetFileName(filePath), folderName, newDocs.Count, lastPos, newPos, totalSuccess, totalFailed);
                }

                stateTracker.MarkFilePosition(filePath, newPos, fileInfo.Length);
                await stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                logger.LogWarning("Файл ТЖ {FilePath} недоступен или удален 1С во время обработки: {Message}", filePath, ex.Message);
            }
        }
    }
}
