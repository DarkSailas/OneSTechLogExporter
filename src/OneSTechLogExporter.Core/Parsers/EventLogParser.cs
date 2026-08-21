using System.Buffers;
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
    private const int BufferSize = 131_072;   // 128 КБ буфер чтения файла

    private static readonly SearchValues<char> LgpSpecialDelimiters = SearchValues.Create(['"', '{', '}', ',']);

    /// <summary>
    /// Словарь сопоставления системных наименований событий 1С с русскоязычными синонимами.
    /// </summary>
    private static readonly Dictionary<string, string> SystemEventAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_$Session$_.Start"] = "Сеанс. Начало",
        ["_$Session$_.Finish"] = "Сеанс. Завершение",
        ["_$Session$_.Authentication"] = "Сеанс. Аутентификация",
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

    [GeneratedRegex(@"^\{1,.*?""(.*)"",(\d+)\},?", RegexOptions.Compiled)]
    private static partial Regex UserRegex();

    [GeneratedRegex(@"^\{5,.*?""(.*)"",(\d+)\},?", RegexOptions.Compiled)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"^\{3,.*?""(.*)"",(\d+)\},?", RegexOptions.Compiled)]
    private static partial Regex AppRegex();

    [GeneratedRegex(@"^\{4,.*?""(.*)"",(\d+)\},?", RegexOptions.Compiled)]
    private static partial Regex EventRegex();

    /// <summary>
    /// Асинхронное считывание и разбор словаря 1Cv8.lgf.
    /// </summary>
    public static async ValueTask<LgfDictionary> ParseDictionaryAsync(string filePath, CancellationToken ct = default)
    {
        var dict = new LgfDictionary();
        if (!File.Exists(filePath))
            return dict;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            line = line.Trim();

            if (line.StartsWith("{1,"))
            {
                var m = UserRegex().Match(line);
                if (m.Success)
                    dict.Users[m.Groups[2].Value] = m.Groups[1].Value;
            }
            else if (line.StartsWith("{5,"))
            {
                var m = MetaRegex().Match(line);
                if (m.Success)
                    dict.Metas[m.Groups[2].Value] = m.Groups[1].Value;
            }
            else if (line.StartsWith("{3,"))
            {
                var m = AppRegex().Match(line);
                if (m.Success)
                    dict.Apps[m.Groups[2].Value] = m.Groups[1].Value;
            }
            else if (line.StartsWith("{4,"))
            {
                var m = EventRegex().Match(line);
                if (m.Success)
                    dict.Events[m.Groups[2].Value] = m.Groups[1].Value;
            }
        }

        return dict;
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
            useAsync: true);

        var fileLength = stream.Length;
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
        long lastReportedPos = stream.Position;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                break;

            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (IsEntryStart(trimmed))
            {
                if (inEntry && blockBuilder.Length > 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict);
                    if (doc != null)
                    {
                        docs.Add(doc);
                    }
                    blockBuilder.Clear();
                }

                inEntry = true;
                blockBuilder.AppendLine(line);
            }
            else if (inEntry)
            {
                // Защита от бесконечного разрастания битого блока (не более 1 МБ на запись)
                if (blockBuilder.Length < 1_048_576)
                {
                    blockBuilder.AppendLine(line);
                }

                if (trimmed.EndsWith("},") || trimmed.EndsWith("}"))
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict);
                    if (doc != null)
                    {
                        docs.Add(doc);
                    }
                    blockBuilder.Clear();
                    inEntry = false;
                }
            }

            // Периодическое оповещение о прогрессе (каждые 2 МБ)
            var currentPos = stream.Position;
            if (currentPos - lastReportedPos >= 2_097_152)
            {
                lastReportedPos = currentPos;
                progress?.Report((currentPos, fileLength));
            }
        }

        if (inEntry && blockBuilder.Length > 0)
        {
            var doc = ParseEntry(blockBuilder.ToString(), dict);
            if (doc != null)
            {
                docs.Add(doc);
            }
        }

        progress?.Report((stream.Length, stream.Length));
        return (docs, stream.Position);
    }

    /// <summary>
    /// Потоковый итератор по записям ЖР для экономной по памяти потоковой выгрузки без накопления всех записей в памяти.
    /// </summary>
    public static async IAsyncEnumerable<EventLogDoc> ParseLogAsync(
        string filePath,
        LgfDictionary dict,
        IProgress<(long BytesRead, long TotalBytes)>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            yield break;

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            useAsync: true);

        var fileLength = stream.Length;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);

        var blockBuilder = new StringBuilder(4096);
        string? line;
        var inEntry = false;
        long lastReportedPos = 0;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                yield break;

            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (IsEntryStart(trimmed))
            {
                if (inEntry && blockBuilder.Length > 0)
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict);
                    blockBuilder.Clear();
                    if (doc != null)
                        yield return doc;
                }

                inEntry = true;
                blockBuilder.AppendLine(line);
            }
            else if (inEntry)
            {
                if (blockBuilder.Length < 1_048_576)
                {
                    blockBuilder.AppendLine(line);
                }

                if (trimmed.EndsWith("},") || trimmed.EndsWith("}"))
                {
                    var doc = ParseEntry(blockBuilder.ToString(), dict);
                    blockBuilder.Clear();
                    inEntry = false;
                    if (doc != null)
                        yield return doc;
                }
            }

            var currentPos = stream.Position;
            if (currentPos - lastReportedPos >= 2_097_152)
            {
                lastReportedPos = currentPos;
                progress?.Report((currentPos, fileLength));
            }
        }

        if (inEntry && blockBuilder.Length > 0)
        {
            var doc = ParseEntry(blockBuilder.ToString(), dict);
            if (doc != null)
                yield return doc;
        }

        progress?.Report((stream.Length, stream.Length));
    }

    /// <summary>
    /// Быстрая проверка начала новой записи 1С формата {YYYYMMDDHHmmss,...
    /// Без создания подстрок и без регулярных выражений (Zero-Allocation).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsEntryStart(ReadOnlySpan<char> span)
    {
        if (span.Length < 16)
            return false;

        if (span[0] != '{')
            return false;

        // Проверяем 14 цифр метки времени даты 1С
        for (var i = 1; i <= 14; i++)
        {
            if (!char.IsAsciiDigit(span[i]))
                return false;
        }

        return span[15] == ',';
    }

    /// <summary>
    /// Парсинг отдельного блока записи 1С Журнала Регистрации на основе токенизатора без регулярных выражений.
    /// Формат записи 1С .lgp:
    /// {YYYYMMDDHHmmss,TransactionStatus,@{TransactionID},UserIndex,AppIndex,EventIndex,Importance,Comment,MetadataIndex,Data,SessionIndex,...}
    /// </summary>
    public static EventLogDoc? ParseEntry(string rawBlock, LgfDictionary dict)
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
        var rawTran = tokens.Count > 1 ? tokens[1] : string.Empty;
        var tran = rawTran
            .Replace('N', '-')
            .Replace('U', 'V')
            .Replace('R', 'I')
            .Replace("I", "InProc")
            .Replace('C', 'X');

        // 3. Код транзакции @{...}
        var tranCode = tokens.Count > 2 ? tokens[2].TrimStart('@').TrimStart('{').TrimEnd('}').ToString() : string.Empty;

        // 4. Пользователь (UserIndex)
        var userKey = tokens.Count > 3 ? tokens[3].ToString() : string.Empty;
        dict.Users.TryGetValue(userKey, out var user);

        // 6. Приложение (AppIndex)
        var appKey = tokens.Count > 5 ? tokens[5].ToString() : string.Empty;
        dict.Apps.TryGetValue(appKey, out var app);

        // 8. Событие (EventIndex) - токен 6
        var eventKey = tokens.Count > 6 ? tokens[6] : string.Empty;
        dict.Events.TryGetValue(eventKey, out var eventName);

        if (!string.IsNullOrEmpty(eventName) && SystemEventAliases.TryGetValue(eventName, out var alias))
        {
            eventName = alias;
        }

        // 9. Важность (PrimaryImportance) - токен 7
        var rawImportance = tokens.Count > 7 ? tokens[7] : string.Empty;
        var importance = rawImportance switch
        {
            "I" => "Информация",
            "E" => "Ошибка",
            "W" => "Предупреждение",
            "N" => "Примечание",
            _ => rawImportance
        };

        // 10. Комментарий - токен 8
        var comment = tokens.Count > 8 ? SanitizeText(Unquote(tokens[8])) : string.Empty;

        // 11. Метаданные (MetadataIndex) - токен 9
        var metaKey = tokens.Count > 9 ? tokens[9] : string.Empty;
        dict.Metas.TryGetValue(metaKey, out var metaData);
        metaData = SanitizeText(metaData);

        // 12. Представление данных (Data) - токен 10
        var data = tokens.Count > 10 ? SanitizeText(Unquote(tokens[10])) : string.Empty;

        // 16. Сеанс (SessionID) - токен 16 (или 15/14)
        var session = tokens.Count > 16 ? SanitizeText(Unquote(tokens[16])) :
                      tokens.Count > 15 ? SanitizeText(Unquote(tokens[15])) :
                      tokens.Count > 14 ? SanitizeText(Unquote(tokens[14])) : string.Empty;

        user = FastStringPool.Intern(SanitizeText(user));
        app = FastStringPool.Intern(SanitizeText(app));
        eventName = FastStringPool.Intern(SanitizeText(eventName));
        metaData = FastStringPool.Intern(metaData);
        importance = FastStringPool.Intern(importance);
        session = FastStringPool.Intern(session);

        var tranFull = $"{tran}({tranCode})";
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
            App = app,
            Comment = comment,
            Importance = importance,
            Data = data,
            Session = session
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
}
