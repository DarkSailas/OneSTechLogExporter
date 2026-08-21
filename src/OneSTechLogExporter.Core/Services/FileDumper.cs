using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Serialization;

namespace OneSTechLogExporter.Core.Services;

/// <summary>
/// Сервис инкрементальной дозаписи (append) распарсенных логов 1С в локальные JSON-файлы с ротацией по размеру (МБ), числу записей и лимиту файлов.
/// </summary>
public sealed class FileDumper
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FileDumpSettings _settings;
    private readonly ILogger<FileDumper> _logger;

    public FileDumper(FileDumpSettings settings, ILogger<FileDumper> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Инкрементальная дозапись пачки записей Журнала Регистрации в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpEventLogsAsync(string prefix, IEnumerable<EventLogDoc> docs, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return;

        var docList = docs.ToList();
        if (docList.Count == 0) return;

        try
        {
            var targetDir = ResolveTargetDirectory(_settings.DirectoryPath);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var pattern = string.IsNullOrWhiteSpace(_settings.EventLogFileNamePattern) ? "data_evlog_{N}.json" : _settings.EventLogFileNamePattern;
            var filePath = GetTargetFilePath(targetDir, pattern, prefix);

            var writtenCount = 0;
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                foreach (var doc in docList)
                {
                    if (doc == null || string.IsNullOrWhiteSpace(doc.Event))
                        continue;

                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(doc, LogJsonContext.Default.EventLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка при сериализации записи ЖР [{DocId}]. Пропускаем некорректную запись.", doc.Id);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(json) || json.Length < 10 || json == "{}")
                        continue;

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    writtenCount++;
                }
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ЖР в локальный файл дампа: {FilePath}", writtenCount, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Журнала Регистрации");
        }
    }

    /// <summary>
    /// Инкрементальная выгрузка пачки записей Технологического Журнала в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpTechLogsAsync(string prefix, IEnumerable<TechLogDoc> docs, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return;

        var docList = docs.ToList();
        if (docList.Count == 0) return;

        try
        {
            var targetDir = ResolveTargetDirectory(_settings.DirectoryPath);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var pattern = string.IsNullOrWhiteSpace(_settings.TechLogFileNamePattern) ? "data_tglog_{N}.json" : _settings.TechLogFileNamePattern;
            var filePath = GetTargetFilePath(targetDir, pattern, prefix);

            var writtenCount = 0;
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                foreach (var doc in docList)
                {
                    if (doc == null || string.IsNullOrWhiteSpace(doc.Event))
                        continue;

                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(doc, LogJsonContext.Default.TechLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Превышение размера полей записи ТЖ [{DocId}] (сообщение: {Msg}). Применяется резервное усечение объекта.", doc.Id, ex.Message);
                        try
                        {
                            var fallbackDoc = SanitizeDocFallback(doc);
                            json = JsonSerializer.Serialize(fallbackDoc, CompactJsonOptions);
                        }
                        catch (Exception fallbackEx)
                        {
                            _logger.LogWarning(fallbackEx, "Критическая ошибка сериализации ТЖ [{DocId}]. Запись пропущена.", doc.Id);
                            continue;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(json) || json.Length < 10 || json == "{}")
                        continue;

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    writtenCount++;
                }
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ТЖ в локальный файл дампа: {FilePath}", writtenCount, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Технологического Журнала");
        }
    }

    /// <summary>
    /// Определение абсолютного пути каталога относительно корня исполнения приложения.
    /// </summary>
    private static string ResolveTargetDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
            dirPath = "parsed_logs";

        return Path.IsPathRooted(dirPath)
            ? dirPath
            : Path.Combine(AppContext.BaseDirectory, dirPath);
    }

    /// <summary>
    /// Генерация пути к файлу дампа в пределах фиксированного кольцевого буфера слотов 1..N (перезапись наистарейшего).
    /// </summary>
    private string GetTargetFilePath(string targetDir, string pattern, string prefix)
    {
        var now = DateTime.Now;
        var basePattern = pattern
            .Replace("{PREFIX}", prefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{DATE}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{TIME}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{TIMESTAMP}", now.ToString("yyyyMMdd_HHmmss"), StringComparison.OrdinalIgnoreCase);

        if (basePattern.Contains("{N}", StringComparison.OrdinalIgnoreCase))
        {
            var limit = _settings.RetainedFileCountLimit > 0 ? _settings.RetainedFileCountLimit : 30;

            // 1. Поиск первого свободно отсутствующего слота N от 1 до limit
            for (var i = 1; i <= limit; i++)
            {
                var fileName = basePattern.Replace("{N}", i.ToString(), StringComparison.OrdinalIgnoreCase);
                var filePath = Path.Combine(targetDir, fileName);

                if (!File.Exists(filePath))
                    return filePath;
            }

            // 2. Если все слоты 1..limit уже созданы — выбираем самый старый по времени из слотов 1..limit для перезаписи
            FileInfo? oldestFile = null;
            var oldestTime = DateTime.MaxValue;

            for (var i = 1; i <= limit; i++)
            {
                var fileName = basePattern.Replace("{N}", i.ToString(), StringComparison.OrdinalIgnoreCase);
                var filePath = Path.Combine(targetDir, fileName);
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    if (info.LastWriteTimeUtc < oldestTime)
                    {
                        oldestTime = info.LastWriteTimeUtc;
                        oldestFile = info;
                    }
                }
            }

            if (oldestFile != null)
            {
                return oldestFile.FullName;
            }

            return Path.Combine(targetDir, basePattern.Replace("{N}", "1", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var ext = Path.GetExtension(basePattern);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(basePattern);

            var primaryPath = Path.Combine(targetDir, basePattern);
            if (!File.Exists(primaryPath))
                return primaryPath;

            for (var i = 1; i <= 99999; i++)
            {
                var indexedFileName = $"{nameWithoutExt}_{i}{ext}";
                var indexedPath = Path.Combine(targetDir, indexedFileName);

                if (!File.Exists(indexedPath))
                    return indexedPath;
            }

            return Path.Combine(targetDir, basePattern);
        }
    }

    /// <summary>
    /// Проверка необходимости ротации файла дампа по размеру (МБ) и/или числу записей (строк).
    /// </summary>
    private bool ShouldRollFile(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        var maxFileSizeMb = _settings.MaxFileSizeMb;
        var maxRecords = _settings.MaxFileRecordCount;
        var strategy = _settings.RollStrategy ?? "SizeOrRecordCount";

        var isSizeExceeded = false;
        if (maxFileSizeMb > 0 &&
            (strategy.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            var maxBytes = (long)maxFileSizeMb * 1024 * 1024;
            var info = new FileInfo(filePath);
            if (info.Length >= maxBytes)
            {
                isSizeExceeded = true;
            }
        }

        var isRecordCountExceeded = false;
        if (maxRecords > 0 &&
            (strategy.Equals("RecordCount", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var currentLineCount = File.ReadLines(filePath).Count();
                if (currentLineCount >= maxRecords)
                {
                    isRecordCountExceeded = true;
                }
            }
            catch
            {
                // Безопасное проглатывание исключений при доступе к файлу
            }
        }

        if (strategy.Equals("Size", StringComparison.OrdinalIgnoreCase))
            return isSizeExceeded;

        if (strategy.Equals("RecordCount", StringComparison.OrdinalIgnoreCase))
            return isRecordCountExceeded;

        return isSizeExceeded || isRecordCountExceeded;
    }

    /// <summary>
    /// Автоматическое удаление старых файлов дампов при превышении лимита RetainedFileCountLimit.
    /// </summary>
    private void CleanupOldDumps(string targetDir)
    {
        var limit = _settings.RetainedFileCountLimit;
        if (limit <= 0) return; // 0 = без ограничений по ротации и удалению старых дампов

        try
        {
            if (!Directory.Exists(targetDir)) return;

            var files = new DirectoryInfo(targetDir)
                .GetFiles("*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count > limit)
            {
                var filesToDelete = files.Take(files.Count - limit);
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        if (file.IsReadOnly)
                        {
                            file.IsReadOnly = false;
                        }

                        file.Delete();
                        _logger.LogInformation("Ротация дампов: удален старый файл дампа {FileName}", file.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Устаревший файл дампа {FileName} временно не может быть удален (заблокирован или занят).", file.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ошибка при выполнении ротации файлов дампов в {TargetDir}", targetDir);
        }
    }

    /// <summary>
    /// Резервное принудительное усечение текстовых полей документа ТЖ до 4 КБ при возникновении переполнения буфера сериализатора.
    /// </summary>
    private static TechLogDoc SanitizeDocFallback(TechLogDoc doc)
    {
        const int cap = 4096;
        var cleanProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in doc.Properties)
        {
            cleanProps[k] = Parsers.TechLogParser.SanitizeText(v, cap);
        }

        return doc with
        {
            Context = Parsers.TechLogParser.SanitizeText(doc.Context, cap),
            Sql = Parsers.TechLogParser.SanitizeText(doc.Sql, cap),
            Locks = Parsers.TechLogParser.SanitizeText(doc.Locks, cap),
            Descr = Parsers.TechLogParser.SanitizeText(doc.Descr, cap),
            WaitConnections = Parsers.TechLogParser.SanitizeText(doc.WaitConnections, cap),
            Properties = cleanProps
        };
    }
}
