using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OneSTechLogExporter.Core.Models;

namespace OneSTechLogExporter.Core.Parsers;

/// <summary>
/// Экстремально производительный потоковый парсер Журнала Регистрации 1С (.lgf словарей и .lgp файлов событий 100+ ГБ).
/// Разработан по стандарту Zero/Low-Allocation на ReadOnlySpan&lt;char&gt; с SIMD-векторизацией SearchValues.
/// </summary>
public static partial class EventLogParser
{
    public const int MaxFieldLength = 65_536; // 64 КБ лимит на отдельное текстовое поле лога
    public const int BufferSize = 4_194_304;   // 4 МБ высокоскоростной буфер для сетевых SMB и 100+ ГБ файлов

    private static readonly SearchValues<char> LgpSpecialDelimiters = SearchValues.Create(['"', '{', '}', ',']);

    /// <summary>
    /// Словарь сопоставления системных наименований событий 1С с русскоязычными синонимами.
    /// </summary>
    public static readonly Dictionary<string, string> SystemEventAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_$Session$_.Start"] = "Сеанс. Начало",
        ["_$Session$_.Finish"] = "Сеанс. Завершение",
        ["_$Session$_.Authentication"] = "Сеанс. Аутентификация",
        ["_$Session$_.AuthenticationError"] = "Сеанс. Ошибка аутентификации",
        ["_$Access$_.Access"] = "Доступ. Доступ",
        ["_$Access$_.AccessDenied"] = "Доступ. Отказ в доступе",
        ["_$OpenID$_.Authentication"] = "OpenID. Аутентификация",
        ["_$OpenID$_.AuthenticationError"] = "OpenID. Ошибка аутентификации",
        ["_$InfoBase$_.ConfigUpdate"] = "Информационная база. Изменение конфигурации",
        ["_$InfoBase$_.DBConfigUpdate"] = "Информационная база. Изменение конфигурации базы данных",
        ["_$InfoBase$_.EventLogSettingsUpdate"] = "Информационная база. Изменение параметров журнала регистрации",
        ["_$InfoBase$_.InfoBaseAdmParamsUpdate"] = "Информационная база. Изменение параметров информационной базы",
        ["_$InfoBase$_.MasterNodeUpdate"] = "Информационная база. Изменение главного узла",
        ["_$InfoBase$_.RegionalSettingsUpdate"] = "Информационная база. Изменение региональных установок",
        ["_$InfoBase$_.TARInfo"] = "Тестирование и исправление. Сообщение",
        ["_$InfoBase$_.TARMess"] = "Тестирование и исправление. Предупреждение",
        ["_$InfoBase$_.TARImportant"] = "Тестирование и исправление. Ошибка",
        ["_$Data$_.New"] = "Данные. Добавление",
        ["_$Data$_.Update"] = "Данные. Изменение",
        ["_$Data$_.Delete"] = "Данные. Удаление",
        ["_$Data$_.TotalsPeriodUpdate"] = "Данные. Изменение периода рассчитанных итогов",
        ["_$Data$_.Post"] = "Данные. Проведение",
        ["_$Data$_.Unpost"] = "Данные. Отмена проведения",
        ["_$User$_.New"] = "Пользователи. Добавление",
        ["_$User$_.Update"] = "Пользователи. Изменение",
        ["_$User$_.Delete"] = "Пользователи. Удаление",
        ["_$Job$_.Start"] = "Фоновое задание. Запуск",
        ["_$Job$_.Succeed"] = "Фоновое задание. Успешное завершение",
        ["_$Job$_.Fail"] = "Фоновое задание. Ошибка выполнения",
        ["_$Job$_.Cancel"] = "Фоновое задание. Отмена",
        ["_$PerformError$_"] = "Ошибка выполнения",
        ["_$Transaction$_.Begin"] = "Транзакция. Начало",
        ["_$Transaction$_.Commit"] = "Транзакция. Фиксация",
        ["_$Transaction$_.Rollback"] = "Транзакция. Отмена"
    };

    /// <summary>
    /// Асинхронное считывание и сверхбыстрый безаллокационный разбор словаря 1Cv8.lgf.
    /// Поддерживает оповещение о прогрессе чтения и оптимизирован под работу по сети (SMB/NAS).
    /// </summary>
    public static async ValueTask<LgfDictionary> ParseDictionaryAsync(
        string filePath,
        IProgress<(long BytesRead, long TotalBytes)>? progress,
        CancellationToken ct = default)
    {
        var dict = new LgfDictionary();
        if (!File.Exists(filePath))
            return dict;

        var fileLength = 0L;
        try { fileLength = new FileInfo(filePath).Length; } catch { }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize);

        string? line;
        long lastReported = 0;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var span = line.AsSpan().Trim();

            if (span.StartsWith("{1,") || span.StartsWith("{2,") || span.StartsWith("{3,") ||
                span.StartsWith("{4,") || span.StartsWith("{5,") || span.StartsWith("{6,") ||
                span.StartsWith("{7,") || span.StartsWith("{8,"))
            {
                ParseDictionaryLine(span, dict);
            }

            var pos = stream.Position;
            if (pos - lastReported >= 2_097_152)
            {
                lastReported = pos;
                progress?.Report((pos, fileLength > 0 ? fileLength : pos));
            }
        }

        if (fileLength > 0)
        {
            progress?.Report((fileLength, fileLength));
        }

        return dict;
    }

    /// <summary>
    /// Перегрузка для обратной совместимости.
    /// </summary>
    public static ValueTask<LgfDictionary> ParseDictionaryAsync(string filePath, CancellationToken ct = default)
        => ParseDictionaryAsync(filePath, null, ct);

    /// <summary>
    /// Разбор строки словаря 1Cv8.lgf без регулярных выражений на ReadOnlySpan (Zero-Allocation).
    /// Корректно извлекает наименование объекта даже при наличии UUID в кавычках перед именем.
    /// </summary>
    private static void ParseDictionaryLine(ReadOnlySpan<char> span, LgfDictionary dict)
    {
        // 1. Для секций портов без кавычек: {7, 1560, 3} или {8, 1561, 4}
        if (span.StartsWith("{7,") || span.StartsWith("{8,"))
        {
            var trimmedContent = span.TrimStart('{').TrimEnd("}, \r\n");
            var c1 = trimmedContent.IndexOf(',');
            if (c1 > 0)
            {
                var restSection = trimmedContent[(c1 + 1)..].Trim();
                var c2 = restSection.IndexOf(',');
                if (c2 > 0)
                {
                    var portVal = restSection[..c2].Trim().Trim('"').ToString();
                    var codeVal = restSection[(c2 + 1)..].Trim().Trim('"').ToString();
                    if (!string.IsNullOrEmpty(codeVal) && !string.IsNullOrEmpty(portVal))
                    {
                        dict.Ports[codeVal] = portVal;
                        return;
                    }
                }
            }
        }

        // 2. Для строковых секций с кавычками: {1,...}, {2,...}, {3,...}, {4,...}, {5,...}, {6,...}
        var lastQuote = span.LastIndexOf('"');
        if (lastQuote <= 0) return;

        var secondToLastQuote = span[..lastQuote].LastIndexOf('"');
        if (secondToLastQuote < 0 || secondToLastQuote >= lastQuote) return;

        var name = span.Slice(secondToLastQuote + 1, lastQuote - secondToLastQuote - 1).ToString();
        if (name.Contains("\"\""))
        {
            name = name.Replace("\"\"", "\"");
        }

        var rest = span[(lastQuote + 1)..].Trim();
        var commaIdx = rest.IndexOf(',');
        if (commaIdx < 0) return;

        var codeSpan = rest[(commaIdx + 1)..].Trim().TrimEnd("}, \r\n");
        if (codeSpan.IsEmpty) return;

        var code = codeSpan.ToString();

        if (span.StartsWith("{1,"))
        {
            dict.Users[code] = name;
        }
        else if (span.StartsWith("{2,"))
        {
            dict.Computers[code] = name;
        }
        else if (span.StartsWith("{3,"))
        {
            dict.Apps[code] = name;
        }
        else if (span.StartsWith("{4,"))
        {
            dict.Events[code] = name;
        }
        else if (span.StartsWith("{5,"))
        {
            dict.Metas[code] = name;
        }
        else if (span.StartsWith("{6,"))
        {
            dict.Servers[code] = name;
        }
        else if (span.StartsWith("{7,") || span.StartsWith("{8,"))
        {
            dict.Ports[code] = name;
        }
    }

    /// <summary>
    /// Высокопроизводительный инкрементальный разбор файла записей Журнала Регистрации (.lgp) со смещения startOffset.
    /// </summary>
    public static ValueTask<(List<EventLogDoc> Documents, long NewPosition)> ParseLogFromOffsetAsync(
        string filePath,
        LgfDictionary dict,
        long startOffset,
        CancellationToken ct) => ParseLogFromOffsetAsync(filePath, dict, startOffset, null, ct);

    /// <summary>
    /// Высокопроизводительный инкрементальный разбор файла записей Журнала Регистрации (.lgp) со смещения startOffset с поддержкой Progress.
    /// Обрабатывает файлы любого размера (100+ ГБ) в потоковом режиме с минимальным расходом памяти.
    /// </summary>
    public static async ValueTask<(List<EventLogDoc> Documents, long NewPosition)> ParseLogFromOffsetAsync(
        string filePath,
        LgfDictionary dict,
        long startOffset = 0,
        IProgress<(long BytesRead, long TotalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        var docs = new List<EventLogDoc>();
        if (!File.Exists(filePath))
            return (docs, startOffset);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var fileLength = stream.Length;
        var fileName = FastStringPool.Intern(Path.GetFileName(filePath));
        var fileSize = fileLength;
        var fileSizeFormatted = FastStringPool.Intern(FormatFileSize(fileLength));

        if (startOffset > 0 && startOffset < fileLength)
        {
            stream.Seek(startOffset, SeekOrigin.Begin);
        }
        else if (startOffset >= fileLength)
        {
            progress?.Report((fileLength, fileLength));
            return (docs, fileLength);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);

        var blockBuilder = new StringBuilder(4096);
        string? line;
        var inEntry = false;
        var braceBalance = 0;
        var inQuotes = false;
        long parsedBytes = startOffset;
        long lastReportTick = Stopwatch.GetTimestamp();

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                break;

            parsedBytes += Encoding.UTF8.GetByteCount(line) + 2;

            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (IsEntryStart(trimmed))
            {
                if (inEntry && blockBuilder.Length > 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    if (doc != null)
                    {
                        docs.Add(doc);
                    }
                    blockBuilder.Clear();
                }

                inEntry = true;
                inQuotes = false;
                braceBalance = CalculateBraceBalance(trimmed, ref inQuotes);
                blockBuilder.AppendLine(line);

                if (braceBalance <= 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    if (doc != null)
                    {
                        docs.Add(doc);
                    }
                    blockBuilder.Clear();
                    inEntry = false;
                }
            }
            else if (inEntry)
            {
                // Защита от бесконечного разрастания битого блока (не более 1 МБ на запись)
                if (blockBuilder.Length < 1_048_576)
                {
                    blockBuilder.AppendLine(line);
                }

                braceBalance += CalculateBraceBalance(trimmed, ref inQuotes);

                if (braceBalance <= 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    if (doc != null)
                    {
                        docs.Add(doc);
                    }
                    blockBuilder.Clear();
                    inEntry = false;
                }
            }

            var now = Stopwatch.GetTimestamp();
            if ((now - lastReportTick) * 1000.0 / Stopwatch.Frequency >= 80)
            {
                lastReportTick = now;
                var reportBytes = Math.Min(fileLength, Math.Max(parsedBytes, startOffset));
                progress?.Report((reportBytes, fileLength));
            }
        }

        if (inEntry && blockBuilder.Length > 0)
        {
            var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
            if (doc != null)
            {
                docs.Add(doc);
            }
        }

        progress?.Report((stream.Length, stream.Length));
        return (docs, stream.Position);
    }

    /// <summary>
    /// Информация о прогрессе чтения лога, включая объем пропущенных данных при быстром переходе к дате.
    /// </summary>
    public readonly record struct LogReadProgress(long BytesRead, long TotalBytes, long SkippedBytes = 0);

    /// <summary>
    /// Потоковый итератор по записям ЖР для экономной по памяти потоковой выгрузки без накопления всех записей в памяти.
    /// </summary>
    public static async IAsyncEnumerable<EventLogDoc> ParseLogAsync(
        string filePath,
        LgfDictionary dict,
        IProgress<LogReadProgress>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        DateTime? filterDateFrom = null,
        DateTime? filterDateTo = null)
    {
        if (!File.Exists(filePath))
            yield break;

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var fileLength = stream.Length;
        var fileName = FastStringPool.Intern(Path.GetFileName(filePath));
        var fileSize = fileLength;
        var fileSizeFormatted = FastStringPool.Intern(FormatFileSize(fileLength));

        long fastOffset = 0;
        if (filterDateFrom.HasValue && fileLength > 20_971_520)
        {
            fastOffset = await FindFastStartOffsetAsync(stream, filterDateFrom.Value, ct).ConfigureAwait(false);
            if (fastOffset > 0 && fastOffset < fileLength)
            {
                stream.Seek(fastOffset, SeekOrigin.Begin);
            }
            else
            {
                fastOffset = 0;
            }
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: stream.Position == 0, BufferSize, leaveOpen: true);

        var blockBuilder = new StringBuilder(4096);
        string? line;
        var inEntry = false;
        var skipCurrentEntry = false;
        var braceBalance = 0;
        var inQuotes = false;
        long parsedBytes = fastOffset;
        long lastReportTick = Stopwatch.GetTimestamp();

        var hasDateFilter = filterDateFrom.HasValue || filterDateTo.HasValue;
        var minDate = filterDateFrom?.Date ?? DateTime.MinValue;
        var maxDate = filterDateTo.HasValue ? filterDateTo.Value.Date.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

        if (fastOffset > 0)
        {
            progress?.Report(new LogReadProgress(fastOffset, fileLength, fastOffset));
        }

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                yield break;

            parsedBytes += Encoding.UTF8.GetByteCount(line) + 2;

            var now = Stopwatch.GetTimestamp();
            if ((now - lastReportTick) * 1000.0 / Stopwatch.Frequency >= 80)
            {
                lastReportTick = now;
                var reportBytes = Math.Min(fileLength, Math.Max(parsedBytes, fastOffset));
                progress?.Report(new LogReadProgress(reportBytes, fileLength, fastOffset));
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (IsEntryStart(line))
            {
                if (inEntry && blockBuilder.Length > 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    blockBuilder.Clear();
                    inEntry = false;
                    if (doc != null)
                        yield return doc;
                }

                if (hasDateFilter && TryParseEntryDate(line, out var entryDate))
                {
                    // Если лог ушел позже maxDate (с запасом 2 часа на коммиты параллельных транзакций) — досрочно завершаем чтение файла!
                    if (entryDate > maxDate.AddHours(2))
                    {
                        yield break;
                    }

                    if (entryDate.Date < minDate || entryDate.Date > maxDate.Date)
                    {
                        skipCurrentEntry = true;
                        inEntry = false;
                        continue;
                    }
                }

                skipCurrentEntry = false;
                inEntry = true;
                inQuotes = false;
                braceBalance = CalculateBraceBalance(line, ref inQuotes);
                blockBuilder.AppendLine(line);

                if (braceBalance <= 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    blockBuilder.Clear();
                    inEntry = false;
                    if (doc != null)
                        yield return doc;
                }
            }
            else if (inEntry && !skipCurrentEntry)
            {
                if (blockBuilder.Length < 1_048_576)
                {
                    blockBuilder.AppendLine(line);
                }

                braceBalance += CalculateBraceBalance(line, ref inQuotes);

                if (braceBalance <= 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
                    blockBuilder.Clear();
                    inEntry = false;
                    if (doc != null)
                        yield return doc;
                }
            }
        }

        if (inEntry && !skipCurrentEntry && blockBuilder.Length > 0)
        {
            var doc = ParseEntry(blockBuilder.ToString(), dict, fileName, fileSize, fileSizeFormatted);
            if (doc != null)
                yield return doc;
        }

        progress?.Report(new LogReadProgress(stream.Length, stream.Length, fastOffset));
    }

    /// <summary>
    /// Извлечение метки даты/времени из заголовка записи {YYYYMMDDHHmmss,... без аллокаций памяти.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool TryParseEntryDate(ReadOnlySpan<char> span, out DateTime date)
    {
        date = default;
        var trimmed = span.TrimStart();
        if (trimmed.Length < 16 || trimmed[0] != '{') return false;

        int year = (trimmed[1] - '0') * 1000 + (trimmed[2] - '0') * 100 + (trimmed[3] - '0') * 10 + (trimmed[4] - '0');
        int month = (trimmed[5] - '0') * 10 + (trimmed[6] - '0');
        int day = (trimmed[7] - '0') * 10 + (trimmed[8] - '0');
        int hour = (trimmed[9] - '0') * 10 + (trimmed[10] - '0');
        int minute = (trimmed[11] - '0') * 10 + (trimmed[12] - '0');
        int second = (trimmed[13] - '0') * 10 + (trimmed[14] - '0');

        if (month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
            return false;

        try
        {
            date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Поиск даты и относительного смещения первой записи {YYYYMMDDHHmmss,... в сыром буфере байт UTF-8.
    /// Работает без аллокаций памяти (Zero-Allocation).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool TryFindEntryDateInBuffer(ReadOnlySpan<byte> buffer, out DateTime date, out int relativeOffset, bool isAtFileStart = false)
    {
        date = default;
        relativeOffset = -1;

        if (buffer.Length < 16) return false;

        var maxIdx = buffer.Length - 16;
        for (var i = 0; i <= maxIdx; i++)
        {
            if (buffer[i] != (byte)'{') continue;

            // Запись 1С в файле .lgp всегда начинается с новой строки (или с нулевого байта файла)
            if (i > 0)
            {
                if (buffer[i - 1] != (byte)'\n' && buffer[i - 1] != (byte)'\r')
                    continue;
            }
            else if (!isAtFileStart)
            {
                // Если буфер начинается с байта '{', но мы не в начале файла, это может быть кусок середины строки
                continue;
            }

            // Проверяем 14 цифр даты YYYYMMDDHHmmss
            var isDigits = true;
            for (var d = 1; d <= 14; d++)
            {
                var b = buffer[i + d];
                if (b < (byte)'0' || b > (byte)'9')
                {
                    isDigits = false;
                    break;
                }
            }
            if (!isDigits) continue;

            if (buffer[i + 15] != (byte)',') continue;

            int year = (buffer[i + 1] - '0') * 1000 + (buffer[i + 2] - '0') * 100 + (buffer[i + 3] - '0') * 10 + (buffer[i + 4] - '0');
            int month = (buffer[i + 5] - '0') * 10 + (buffer[i + 6] - '0');
            int day = (buffer[i + 7] - '0') * 10 + (buffer[i + 8] - '0');
            int hour = (buffer[i + 9] - '0') * 10 + (buffer[i + 10] - '0');
            int minute = (buffer[i + 11] - '0') * 10 + (buffer[i + 12] - '0');
            int second = (buffer[i + 13] - '0') * 10 + (buffer[i + 14] - '0');

            if (month is < 1 or > 12 || day is < 1 or > 31 || hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
                continue;

            try
            {
                date = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                relativeOffset = i;
                return true;
            }
            catch
            {
                continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Быстрый бинарный поиск смещения в файле .lgp для перехода к целевой дате без чтения гигабайт предшествующих данных.
    /// За ~25-30 итераций seek находит точное смещение начала целевого диапазона с запасом на транзакции.
    /// </summary>
    public static async ValueTask<long> FindFastStartOffsetAsync(
        Stream stream,
        DateTime targetDate,
        CancellationToken ct = default)
    {
        var fileLength = stream.Length;
        if (fileLength <= 20_971_520)
            return 0;

        // Поиск с запасом в 20 минут назад для учета параллельных незавершенных транзакций в 1С
        var searchTarget = targetDate.AddMinutes(-20);
        if (searchTarget < DateTime.MinValue.AddMinutes(20))
            searchTarget = DateTime.MinValue;

        long low = 0;
        long high = fileLength;
        long bestOffset = 0;
        const int probeSize = 65536;
        var probeBuffer = new byte[probeSize];
        var maxIterations = 35;

        while (low < high && maxIterations-- > 0)
        {
            if (ct.IsCancellationRequested)
                break;

            // Если окно поиска сжалось меньше 128 КБ — останавливаемся на найденном bestOffset
            if (high - low <= 131072)
                break;

            var mid = low + (high - low) / 2;
            long foundEntryPos = -1;
            DateTime entryDate = default;
            var probeMid = mid;

            // Сканируем вперед блоками по 64 КБ (до 512 КБ), пока не найдем заголовок записи
            for (var chunk = 0; chunk < 8 && probeMid < fileLength; chunk++)
            {
                stream.Seek(probeMid, SeekOrigin.Begin);
                var bytesRead = await stream.ReadAsync(probeBuffer.AsMemory(0, probeSize), ct).ConfigureAwait(false);
                if (bytesRead < 16) break;

                if (TryFindEntryDateInBuffer(probeBuffer.AsSpan(0, bytesRead), out entryDate, out var relOffset, isAtFileStart: probeMid == 0))
                {
                    foundEntryPos = probeMid + relOffset;
                    break;
                }

                probeMid += probeSize - 16;
            }

            if (foundEntryPos >= 0)
            {
                if (entryDate < searchTarget)
                {
                    bestOffset = foundEntryPos;
                    low = foundEntryPos + 16;
                }
                else
                {
                    high = mid;
                }
            }
            else
            {
                low = mid + 524288;
            }
        }

        return bestOffset;
    }

    /// <summary>
    /// Быстрая проверка начала новой записи 1С формата {YYYYMMDDHHmmss,...
    /// Без создания подстрок и без регулярных выражений (Zero-Allocation).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsEntryStart(ReadOnlySpan<char> span)
    {
        var trimmed = span.TrimStart();
        if (trimmed.Length < 16)
            return false;

        if (trimmed[0] != '{')
            return false;

        // Проверяем 14 цифр метки времени даты 1С
        for (var i = 1; i <= 14; i++)
        {
            if (!char.IsAsciiDigit(trimmed[i]))
                return false;
        }

        return trimmed[15] == ',';
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsImportanceChar(string s)
    {
        var trimmed = s.AsSpan().Trim().Trim('"');
        return trimmed.Length == 1 && (trimmed[0] == 'I' || trimmed[0] == 'E' || trimmed[0] == 'W' || trimmed[0] == 'N');
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int CalculateBraceBalance(ReadOnlySpan<char> text, ref bool inQuotes)
    {
        var balance = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i++; // Пропуск экранированной кавычки ""
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (ch == '{') balance++;
                else if (ch == '}') balance--;
            }
        }
        return balance;
    }

    private static string? ExtractAuthenticationAccount(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;

        var sIdx = data.IndexOf("\"S\"", StringComparison.OrdinalIgnoreCase);
        if (sIdx < 0) return null;

        var commaIdx = data.IndexOf(',', sIdx + 3);
        if (commaIdx < 0) return null;

        var q1 = data.IndexOf('"', commaIdx + 1);
        if (q1 < 0) return null;

        var q2 = data.IndexOf('"', q1 + 1);
        if (q2 <= q1) return null;

        var account = data[(q1 + 1)..q2].Trim();
        return account.Length > 0 ? account : null;
    }

    /// <summary>
    /// Декодирование значений поля Data 1С (ссылки {"R", ...}, примитивные типы, системные маркеры).
    /// Преобразует внутренний 32-символьный hex GUID 1C в канонический вид UUID (RFC 4122).
    /// </summary>
    private static string FormatDataValue(string data, string metaData, LgfDictionary dict)
    {
        if (string.IsNullOrWhiteSpace(data))
            return string.Empty;

        var trimmed = data.Trim();

        // 1. Маркер отсутствия объектной ссылки {"U"}
        if (trimmed == "{\"U\"}" || trimmed == "{ \"U\" }" || trimmed == "{U}")
        {
            return string.IsNullOrEmpty(metaData) ? string.Empty : "(без объектной ссылки)";
        }

        // 2. Ссылочный тип 1C: {"R", 9345:ab7a005056bbe0b411f15039d5c3f43d} или {"R", "9345:..."}
        if (trimmed.StartsWith("{\"R\",", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
        {
            var inner = trimmed[5..^1].Trim().Trim('"');
            var colonIdx = inner.IndexOf(':');
            var metaId = colonIdx > 0 ? inner[..colonIdx].Trim() : string.Empty;
            var rawGuid = colonIdx >= 0 ? inner[(colonIdx + 1)..].Trim() : inner;

            // Форматируем 32-значный hex GUID 1C в стандартный вид XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX
            var guidFormatted = rawGuid;
            if (rawGuid.Length == 32 && Guid.TryParseExact(rawGuid, "N", out var parsedGuid))
            {
                guidFormatted = parsedGuid.ToString();
            }

            // Проверяем метаданные ссылки (если отличаются от текущего объекта)
            string? refMeta = null;
            if (!string.IsNullOrEmpty(metaId) && dict.Metas.TryGetValue(metaId, out var resolvedRef))
            {
                refMeta = resolvedRef;
            }

            if (!string.IsNullOrEmpty(refMeta) && !refMeta.Equals(metaData, StringComparison.OrdinalIgnoreCase))
            {
                return $"Ссылка: {refMeta} ({guidFormatted})";
            }

            return $"Ссылка: {guidFormatted}";
        }

        // 3. Строковый тип 1C: {"S", "..."}
        if (trimmed.StartsWith("{\"S\",", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
        {
            return trimmed[5..^1].Trim().Trim('"');
        }

        // 4. Булево 1C: {"B", 0} или {"B", 1}
        if (trimmed.StartsWith("{\"B\",", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
        {
            var val = trimmed[5..^1].Trim();
            return val == "1" ? "Истина" : "Ложь";
        }

        // 5. Число 1C: {"N", 123}
        if (trimmed.StartsWith("{\"N\",", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
        {
            return trimmed[5..^1].Trim();
        }

        // 6. Дата 1C: {"D", 20260817000245}
        if (trimmed.StartsWith("{\"D\",", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('}'))
        {
            var dVal = trimmed[5..^1].Trim().Trim('"');
            if (dVal.Length >= 14 && DateTime.TryParseExact(dVal[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return dVal;
        }

        return trimmed;
    }

    /// <summary>
    /// Очистка строкового представления DataPresentation от артефактов 1С (вида "<?>", пустых секций, концевых разделителей).
    /// </summary>
    private static string CleanDataPresentation(string presentation)
    {
        if (string.IsNullOrWhiteSpace(presentation))
            return string.Empty;

        var parts = presentation.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return string.Empty;

        var cleaned = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var p = part.Trim();
            if (p.Equals("<?>", StringComparison.Ordinal) || p.Equals("<?>;", StringComparison.Ordinal))
                continue;

            if (p.StartsWith("<?>", StringComparison.Ordinal))
                p = p[3..].Trim();

            if (!string.IsNullOrEmpty(p))
                cleaned.Add(p);
        }

        return string.Join("; ", cleaned);
    }

    /// <summary>
    /// Парсинг отдельного блока записи 1С Журнала Регистрации на основе токенизатора без регулярных выражений.
    /// Формат записи 1С .lgp:
    /// {YYYYMMDDHHmmss,TransactionStatus,@{TransactionID},UserIndex,AppIndex,EventIndex,Importance,Comment,MetadataIndex,Data,SessionIndex,...}
    /// </summary>
    public static EventLogDoc? ParseEntry(
        string rawBlock,
        LgfDictionary dict,
        string? fileName = null,
        long fileSize = 0,
        string? fileSizeFormatted = null)
    {
        if (string.IsNullOrWhiteSpace(rawBlock))
            return null;

        var span = rawBlock.AsSpan().Trim();
        if (!span.StartsWith("{") || span.Length < 16)
            return null;

        // Токенизация элементов верхнего уровня записи 1С
        var tokens = TokenizeLgpEntry(span);
        if (tokens.Count < 9)
            return null;

        // 1. Дата: YYYYMMDDHHmmss
        var rawDateSpan = tokens[0].AsSpan();
        DateTime parsedDate = DateTime.UtcNow;
        string dateStr;

        if (rawDateSpan.Length >= 14 &&
            int.TryParse(rawDateSpan[..4], out var year) &&
            int.TryParse(rawDateSpan.Slice(4, 2), out var month) &&
            int.TryParse(rawDateSpan.Slice(6, 2), out var day) &&
            int.TryParse(rawDateSpan.Slice(8, 2), out var hour) &&
            int.TryParse(rawDateSpan.Slice(10, 2), out var minute) &&
            int.TryParse(rawDateSpan.Slice(12, 2), out var second))
        {
            try
            {
                parsedDate = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            }
            catch
            {
                parsedDate = DateTime.UtcNow;
            }
            dateStr = parsedDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            dateStr = parsedDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        // 2. Статус транзакции
        var rawTran = tokens.Count > 1 ? tokens[1].Trim().Trim('"') : string.Empty;
        var tran = rawTran switch
        {
            "C" => "C",
            "R" => "R",
            "U" => "U",
            "N" => "-",
            _ => rawTran
        };

        var tranStatusText = rawTran switch
        {
            "C" => "Зафиксирована",
            "R" => "Отменена",
            "U" => "В процессе",
            "N" => "Вне транзакции",
            _ => rawTran
        };

        // 3. Код транзакции @{...}
        var tranCode = tokens.Count > 2 ? tokens[2].TrimStart('@').TrimStart('{').TrimEnd('}').ToString() : string.Empty;

        // 4. Пользователь (UserIndex) - токен 3
        var userKey = tokens.Count > 3 ? Unquote(tokens[3]).Trim() : string.Empty;
        dict.Users.TryGetValue(userKey, out var user);
        if (string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(userKey) && userKey != "0")
        {
            user = $"User #{userKey}";
        }

        // 5. Компьютер (ComputerIndex) - токен 4
        var compKey = tokens.Count > 4 ? Unquote(tokens[4]).Trim() : string.Empty;
        dict.Computers.TryGetValue(compKey, out var computer);
        if (string.IsNullOrEmpty(computer) && !string.IsNullOrEmpty(compKey) && compKey != "0")
        {
            computer = compKey;
        }

        // 6. Приложение (AppIndex) - токен 5
        var appKey = tokens.Count > 5 ? Unquote(tokens[5]).Trim() : string.Empty;
        dict.Apps.TryGetValue(appKey, out var app);
        if (string.IsNullOrEmpty(app) && !string.IsNullOrEmpty(appKey) && appKey != "0")
        {
            app = $"App #{appKey}";
        }

        string eventKey;
        string rawImportance;
        string comment;
        string metaKey;
        string data;
        string dataPresentation;
        string connection = string.Empty;
        string server = string.Empty;
        string port = string.Empty;

        if (tokens.Count > 8 && IsImportanceChar(tokens[8]))
        {
            // Формат 1С 8.2 / 8.3
            connection = tokens.Count > 6 ? Unquote(tokens[6]).Trim() : string.Empty;
            eventKey = tokens.Count > 7 ? Unquote(tokens[7]).Trim() : string.Empty;
            rawImportance = tokens[8].Trim().Trim('"');
            comment = tokens.Count > 9 ? SanitizeText(Unquote(tokens[9])) : string.Empty;
            metaKey = tokens.Count > 10 ? Unquote(tokens[10]).Trim() : string.Empty;
            data = tokens.Count > 11 && tokens[11] != "0" && tokens[11] != "\"\"" ? SanitizeText(Unquote(tokens[11])) : string.Empty;
            dataPresentation = tokens.Count > 12 && !string.IsNullOrWhiteSpace(tokens[12]) && tokens[12] != "\"\""
                ? SanitizeText(Unquote(tokens[12]))
                : string.Empty;

            var serverKey = tokens.Count > 13 ? Unquote(tokens[13]).Trim() : string.Empty;
            if (!string.IsNullOrEmpty(serverKey) && serverKey != "0")
            {
                dict.Servers.TryGetValue(serverKey, out server);
                if (string.IsNullOrEmpty(server)) server = serverKey;
            }

            var portKey = tokens.Count > 14 ? Unquote(tokens[14]).Trim() : string.Empty;
            if (!string.IsNullOrEmpty(portKey) && portKey != "0")
            {
                dict.Ports.TryGetValue(portKey, out port);
                if (string.IsNullOrEmpty(port)) port = portKey;
            }
        }
        else if (tokens.Count > 7 && IsImportanceChar(tokens[7]))
        {
            // Формат 1С 8.1 (без отдельного поля соединения)
            eventKey = tokens.Count > 6 ? Unquote(tokens[6]).Trim() : string.Empty;
            rawImportance = tokens[7].Trim().Trim('"');
            comment = tokens.Count > 8 ? SanitizeText(Unquote(tokens[8])) : string.Empty;
            metaKey = tokens.Count > 9 ? Unquote(tokens[9]).Trim() : string.Empty;
            data = tokens.Count > 10 ? SanitizeText(Unquote(tokens[10])) : string.Empty;
            dataPresentation = tokens.Count > 11 && !string.IsNullOrWhiteSpace(tokens[11]) && tokens[11] != "\"\""
                ? SanitizeText(Unquote(tokens[11]))
                : string.Empty;
        }
        else
        {
            // Фоллбэк
            connection = tokens.Count > 6 ? Unquote(tokens[6]).Trim() : string.Empty;
            eventKey = tokens.Count > 7 ? Unquote(tokens[7]).Trim() : (tokens.Count > 6 ? Unquote(tokens[6]).Trim() : string.Empty);
            rawImportance = tokens.Count > 8 ? tokens[8].Trim().Trim('"') : (tokens.Count > 7 ? tokens[7].Trim().Trim('"') : string.Empty);
            comment = tokens.Count > 9 ? SanitizeText(Unquote(tokens[9])) : (tokens.Count > 8 ? SanitizeText(Unquote(tokens[8])) : string.Empty);
            metaKey = tokens.Count > 10 ? Unquote(tokens[10]).Trim() : (tokens.Count > 9 ? Unquote(tokens[9]).Trim() : string.Empty);
            data = tokens.Count > 11 ? SanitizeText(Unquote(tokens[11])) : string.Empty;
            dataPresentation = tokens.Count > 12 ? SanitizeText(Unquote(tokens[12])) : string.Empty;
        }

        // 8. Событие (EventIndex)
        dict.Events.TryGetValue(eventKey, out var eventName);
        if (!string.IsNullOrEmpty(eventName) && SystemEventAliases.TryGetValue(eventName, out var alias))
        {
            eventName = alias;
        }
        else if (string.IsNullOrEmpty(eventName) && !string.IsNullOrEmpty(eventKey) && eventKey != "0")
        {
            eventName = $"Event #{eventKey}";
        }

        // 9. Важность
        var importance = rawImportance switch
        {
            "I" => "Информация",
            "E" => "Ошибка",
            "W" => "Предупреждение",
            "N" => "Примечание",
            _ => rawImportance
        };

        // 11. Метаданные (MetadataIndex)
        dict.Metas.TryGetValue(metaKey, out var metaData);
        if (string.IsNullOrEmpty(metaData) && !string.IsNullOrEmpty(metaKey) && metaKey != "0" && metaKey != "\"\"")
        {
            metaData = $"Meta #{metaKey}";
        }
        else if (metaKey == "0" || metaKey == "\"\"")
        {
            metaData = string.Empty;
        }
        metaData = SanitizeText(metaData);

        // 12-13. Декодирование Данных и Представления данных
        data = FormatDataValue(data, metaData, dict);
        dataPresentation = CleanDataPresentation(dataPresentation);

        if (string.IsNullOrEmpty(metaKey) || metaKey == "0")
        {
            if (data == "(без объектной ссылки)")
            {
                data = string.Empty;
                dataPresentation = string.Empty;
            }
        }
        else if (data == "(без объектной ссылки)" && string.IsNullOrEmpty(dataPresentation))
        {
            dataPresentation = $"Запись регистра: {metaData}";
        }

        // Декодирование параметров аутентификации {"P", {1, {"S", "UserNameOrAccount"}}}
        if (eventName.Contains("Аутентификац", StringComparison.OrdinalIgnoreCase) ||
            eventKey.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            var authAccount = ExtractAuthenticationAccount(data);
            if (!string.IsNullOrEmpty(authAccount))
            {
                if (string.IsNullOrEmpty(user))
                {
                    user = authAccount;
                }
                dataPresentation = $"Пользователь ОС: {authAccount}";
                data = authAccount;
            }
        }

        // 16. Сеанс (SessionID)
        var session = tokens.Count > 16 ? SanitizeText(Unquote(tokens[16])) :
                      tokens.Count > 15 ? SanitizeText(Unquote(tokens[15])) :
                      tokens.Count > 14 ? SanitizeText(Unquote(tokens[14])) : string.Empty;

        var appTypeName = app switch
        {
            "1CV8C" => "Тонкий клиент",
            "1CV8" => "Толстый клиент",
            "BackgroundJob" => "Фоновое задание",
            "WebClient" => "Веб-клиент",
            "COMConnector" => "COM-соединение",
            "WSConnection" => "Web-сервис",
            "HTTPServiceConnection" => "HTTP-сервис",
            "Designer" => "Конфигуратор",
            "RAS" => "Сервер администрирования (RAS)",
            "RAC" => "Консоль администрирования (RAC)",
            "WebServerExtension" => "Расширение веб-сервера",
            "OData" => "Интерфейс OData",
            "MobileClient" => "Мобильный клиент",
            "MobileServer" => "Мобильный сервер",
            "System" => "Системный процесс",
            _ => app
        };

        user = FastStringPool.Intern(SanitizeText(user));
        computer = FastStringPool.Intern(SanitizeText(computer));
        app = FastStringPool.Intern(SanitizeText(app));
        eventName = FastStringPool.Intern(SanitizeText(eventName));
        metaData = FastStringPool.Intern(metaData);
        importance = FastStringPool.Intern(importance);
        session = FastStringPool.Intern(session);
        server = FastStringPool.Intern(SanitizeText(server));
        port = FastStringPool.Intern(port);
        connection = FastStringPool.Intern(connection);
        tranStatusText = FastStringPool.Intern(tranStatusText);
        appTypeName = FastStringPool.Intern(appTypeName);

        var tranFull = string.IsNullOrEmpty(tranCode) || tranCode == "0,0" ? tran : $"{tran}({tranCode})";
        var docStr = $"{dateStr}{eventName}{user}{metaData}{tranFull}{app}{comment}{importance}{data}{session}";
        var idDoc = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(docStr)));

        return new EventLogDoc
        {
            Id = idDoc,
            Date = parsedDate,
            DateFormatted = parsedDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Event = eventName,
            User = user,
            Meta = metaData,
            Tran = tranFull,
            TranStatusText = tranStatusText,
            App = app,
            AppTypeName = appTypeName,
            Comment = comment,
            Importance = importance,
            Data = data,
            DataPresentation = dataPresentation,
            Computer = computer,
            Server = server,
            Connection = connection,
            Port = port,
            Session = session,
            FileName = fileName,
            FileSize = fileSize,
            FileSizeFormatted = fileSizeFormatted
        };
    }

    /// <summary>
    /// Токенизатор структуры скобок 1С уровня записи без аллокаций.
    /// Корректно обрабатывает экранированные строки в двойных кавычках и вложенные структуры {@{...}}.
    /// </summary>
    private static List<string> TokenizeLgpEntry(ReadOnlySpan<char> span)
    {
        var tokens = new List<string>(18);

        // Пропускаем начальную открывающую скобку '{'
        var content = span;
        if (content.StartsWith("{"))
            content = content[1..];
        if (content.EndsWith("},"))
            content = content[..^2];
        else if (content.EndsWith("}"))
            content = content[..^1];

        var i = 0;
        var len = content.Length;
        var tokenStart = 0;
        var inQuotes = false;
        var braceDepth = 0;

        while (i < len)
        {
            if (!inQuotes && braceDepth == 0)
            {
                var nextSpecial = content.Slice(i).IndexOfAny(LgpSpecialDelimiters);
                if (nextSpecial > 0)
                {
                    i += nextSpecial;
                }
                else if (nextSpecial < 0)
                {
                    break;
                }
            }

            var ch = content[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < len && content[i + 1] == '"')
                {
                    // Экранированная двойная кавычка ""
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (ch == '{')
                {
                    braceDepth++;
                }
                else if (ch == '}')
                {
                    if (braceDepth > 0)
                        braceDepth--;
                }
                else if (ch == ',' && braceDepth == 0)
                {
                    tokens.Add(content[tokenStart..i].Trim().ToString());
                    tokenStart = i + 1;
                }
            }

            i++;
        }

        if (tokenStart <= len)
        {
            tokens.Add(content[tokenStart..len].Trim().ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Снятие обрамляющих кавычек 1С и разэкранирование сдвоенных кавычек "".
    /// </summary>
    private static string Unquote(string str)
    {
        var trimmed = str.AsSpan().Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
        {
            trimmed = trimmed[1..^1];
        }

        var s = trimmed.ToString();
        return s.Contains("\"\"") ? s.Replace("\"\"", "\"") : s;
    }

    /// <summary>
    /// Очистка текста от непечатных управляющих символов (\0, \uFEFF BOM, ASCII 0..31), сырых возвратов каретки \r
    /// и безопасная обрезка сверхбольших дамп-строк для предотвращения падений JsonSerializer и исчерпания RAM.
    /// </summary>
    public static string SanitizeText(string? input, int maxLength = MaxFieldLength)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var isTruncated = false;
        var originalLength = input.Length;
        if (maxLength > 0 && input.Length > maxLength)
        {
            input = input[..maxLength];
            isTruncated = true;
        }

        var sb = new StringBuilder(input.Length + (isTruncated ? 64 : 0));
        foreach (var ch in input)
        {
            if (ch == '\uFEFF' || ch == '\0' || ch == '\r')
                continue;
            if (ch == '\n')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(ch) && ch != '\t')
                continue;

            sb.Append(ch);
        }

        if (isTruncated)
        {
            sb.Append($" ... [TRUNCATED: {originalLength} -> {maxLength} chars]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Форматирование размера файла в байтах в человекочитаемую строку (B, KB, MB, GB).
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / 1024:F1} KB");
        if (bytes < 1024 * 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / (1024 * 1024):F2} MB");
        return string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / (1024 * 1024 * 1024):F2} GB");
    }
}
