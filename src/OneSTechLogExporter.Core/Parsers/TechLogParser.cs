using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OneSTechLogExporter.Core.Models;

namespace OneSTechLogExporter.Core.Parsers;

/// <summary>
/// Высокопроизводительный неблокирующий парсер файлов Технологического Журнала 1С (.log).
/// Оптимизирован под экстремальную скорость разбора сверхбольших файлов через ReadOnlySpan&lt;char&gt; и SIMD.
/// </summary>
public static partial class TechLogParser
{
    private const int BufferSize = 131_072; // 128 КБ буфер потокового чтения
    public const int MaxFieldLength = 65_536; // 64 КБ лимит на строковое свойство

    private static readonly SearchValues<char> KeySeparators = SearchValues.Create(['=', ',']);
    private static readonly SearchValues<char> ValueEndSeparators = SearchValues.Create([',', '\r', '\n']);

    [GeneratedRegex(@"^(\d{2}):(\d{2})\.(\d+)-(\d+),([\w_]+),(\d+)", RegexOptions.Compiled)]
    private static partial Regex HeaderRegex();

    /// <summary>
    /// Инкрементальный разбор файла Технологического Журнала с байтового смещения startOffset.
    /// </summary>
    public static ValueTask<(List<TechLogDoc> Documents, long NewPosition)> ParseFileFromOffsetAsync(
        string filePath,
        string processName,
        string processId,
        long startOffset,
        CancellationToken ct) => ParseFileFromOffsetAsync(filePath, processName, processId, startOffset, null, ct);

    /// <summary>
    /// Инкрементальный разбор файла Технологического Журнала с байтового смещения startOffset.
    /// Поддерживает оповещение о прогрессе чтения файла в байтах.
    /// </summary>
    public static async ValueTask<(List<TechLogDoc> Documents, long NewPosition)> ParseFileFromOffsetAsync(
        string filePath,
        string processName,
        string processId,
        long startOffset = 0,
        IProgress<(long BytesRead, long TotalBytes)>? progress = null,
        CancellationToken ct = default)
    {
        var docs = new List<TechLogDoc>();
        if (!File.Exists(filePath))
            return (docs, startOffset);

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        int year, month, day, hour;

        var nameSpan = fileName.AsSpan();
        if (nameSpan.Length >= 8 &&
            int.TryParse(nameSpan[..2], out var yy) &&
            int.TryParse(nameSpan.Slice(2, 2), out month) &&
            int.TryParse(nameSpan.Slice(4, 2), out day) &&
            int.TryParse(nameSpan.Slice(6, 2), out hour))
        {
            year = 2000 + yy;
        }
        else
        {
            var fileTime = File.GetLastWriteTimeUtc(filePath);
            year = fileTime.Year;
            month = fileTime.Month;
            day = fileTime.Day;
            hour = fileTime.Hour;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
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
        var blockBuilder = new StringBuilder(8192);
        string? line;
        long lastReportedPos = stream.Position;

        // При частичном возобновлении чтения с середины файла пропускаем недочитанный остаток предыдущей строки
        if (startOffset > 0)
        {
            var firstDiscard = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (firstDiscard != null && IsHeaderLine(firstDiscard.AsSpan()))
            {
                blockBuilder.AppendLine(firstDiscard);
            }
        }

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                break;

            var span = line.AsSpan().Trim();
            if (span.IsEmpty)
                continue;

            if (IsHeaderLine(span))
            {
                if (blockBuilder.Length > 0)
                {
                    var doc = ParseBlock(blockBuilder.ToString(), year, month, day, hour, processName, processId);
                    if (doc != null)
                        docs.Add(doc);

                    blockBuilder.Clear();
                }
            }

            if (blockBuilder.Length < 524_288)
            {
                blockBuilder.AppendLine(line);
            }

            var currentPos = stream.Position;
            if (currentPos - lastReportedPos >= 2_097_152)
            {
                lastReportedPos = currentPos;
                progress?.Report((currentPos, fileLength));
            }
        }

        if (blockBuilder.Length > 0)
        {
            var doc = ParseBlock(blockBuilder.ToString(), year, month, day, hour, processName, processId);
            if (doc != null)
                docs.Add(doc);
        }

        progress?.Report((stream.Length, stream.Length));
        return (docs, stream.Position);
    }

    /// <summary>
    /// Потоковое чтение и асинхронный разбор файла Технологического Журнала 1С.
    /// </summary>
    public static async IAsyncEnumerable<TechLogDoc> ParseFileAsync(
        string filePath,
        string processName,
        string processId,
        IProgress<(long BytesRead, long TotalBytes)>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            yield break;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        int year, month, day, hour;

        var nameSpan = fileName.AsSpan();
        if (nameSpan.Length >= 8 &&
            int.TryParse(nameSpan[..2], out var yy) &&
            int.TryParse(nameSpan.Slice(2, 2), out month) &&
            int.TryParse(nameSpan.Slice(4, 2), out day) &&
            int.TryParse(nameSpan.Slice(6, 2), out hour))
        {
            year = 2000 + yy;
        }
        else
        {
            var fileTime = File.GetLastWriteTimeUtc(filePath);
            year = fileTime.Year;
            month = fileTime.Month;
            day = fileTime.Day;
            hour = fileTime.Hour;
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, useAsync: true);
        var fileLength = stream.Length;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize, leaveOpen: true);
        var blockBuilder = new StringBuilder(8192);
        string? line;
        long lastReportedPos = 0;

        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested)
                yield break;

            var span = line.AsSpan().Trim();
            if (span.IsEmpty)
                continue;

            if (IsHeaderLine(span))
            {
                if (blockBuilder.Length > 0)
                {
                    var doc = ParseBlock(blockBuilder.ToString(), year, month, day, hour, processName, processId);
                    blockBuilder.Clear();
                    if (doc != null)
                        yield return doc;
                }
            }

            if (blockBuilder.Length < 524_288)
            {
                blockBuilder.AppendLine(line);
            }

            var currentPos = stream.Position;
            if (currentPos - lastReportedPos >= 2_097_152)
            {
                lastReportedPos = currentPos;
                progress?.Report((currentPos, fileLength));
            }
        }

        if (blockBuilder.Length > 0)
        {
            var doc = ParseBlock(blockBuilder.ToString(), year, month, day, hour, processName, processId);
            if (doc != null)
                yield return doc;
        }

        progress?.Report((stream.Length, stream.Length));
    }

    /// <summary>
    /// Быстрая проверка строки заголовка события ТЖ через ReadOnlySpan (Zero-Allocation).
    /// Формат заголовка: MM:SS.micro-dur,EventName,Level
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool IsHeaderLine(ReadOnlySpan<char> span)
    {
        if (span.Length < 10)
            return false;

        // Символы: 0..1 - минуты, 2 - ':', 3..4 - секунды, 5 - '.'
        if (!char.IsAsciiDigit(span[0]) || !char.IsAsciiDigit(span[1]) || span[2] != ':' ||
            !char.IsAsciiDigit(span[3]) || !char.IsAsciiDigit(span[4]) || span[5] != '.')
        {
            return false;
        }

        // Проверяем наличие дефиса '-' и запятой ','
        var dashIdx = span.IndexOf('-');
        if (dashIdx <= 6 || dashIdx >= span.Length - 2)
            return false;

        var commaIdx = span.Slice(dashIdx).IndexOf(',');
        return commaIdx > 0;
    }

    private static readonly Dictionary<string, string> KnownEventPool = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DBMSSQL"] = "DBMSSQL",
        ["DBPOSTGRS"] = "DBPOSTGRS",
        ["DBORACLE"] = "DBORACLE",
        ["DBV8DBENG"] = "DBV8DBENG",
        ["EXCP"] = "EXCP",
        ["EXCPCNTX"] = "EXCPCNTX",
        ["QERR"] = "QERR",
        ["CALL"] = "CALL",
        ["SCALL"] = "SCALL",
        ["SDBL"] = "SDBL",
        ["TLOCK"] = "TLOCK",
        ["TTIMEOUT"] = "TTIMEOUT",
        ["TDEADLOCK"] = "TDEADLOCK",
        ["SESN"] = "SESN",
        ["CONN"] = "CONN",
        ["ADMIN"] = "ADMIN",
        ["VRSREQUEST"] = "VRSREQUEST",
        ["VRSRESPONSE"] = "VRSRESPONSE",
        ["HASP"] = "HASP",
        ["MEM"] = "MEM",
        ["LEAKS"] = "LEAKS",
        ["ATTN"] = "ATTN",
        ["LONGDURATIONINFO"] = "LONGDURATIONINFO"
    };

    /// <summary>
    /// Разбор отдельного многострочного блока записи Технологического Журнала.
    /// </summary>
    public static TechLogDoc? ParseBlock(string blockText, int year, int month, int day, int hour, string processName, string processId)
    {
        if (string.IsNullOrWhiteSpace(blockText))
            return null;

        if (blockText.Length > 262_144)
        {
            blockText = blockText[..262_144] + "\ndescr='... [TRUNCATED MASSIVE BLOCK TO 256KB]'";
        }

        var firstLineEnd = blockText.IndexOf('\n');
        var firstLine = firstLineEnd > 0 ? blockText[..firstLineEnd].TrimEnd('\r') : blockText.TrimEnd('\r');

        var match = HeaderRegex().Match(firstLine);
        if (!match.Success)
            return null;

        var minute = int.Parse(match.Groups[1].Value);
        var second = int.Parse(match.Groups[2].Value);
        var rawMicro = match.Groups[3].Value;
        var microStr = rawMicro.Length >= 7 ? rawMicro[..7] : rawMicro.PadRight(7, '0');
        var ticks = long.Parse(microStr);

        var baseDate = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        var eventDate = baseDate.AddTicks(ticks);

        var duration = long.Parse(match.Groups[4].Value);
        var durationMs = Math.Round(duration / 1000.0, 3);
        var durationSec = Math.Round(duration / 1000000.0, 6);
        var durationFormatted = FormatDuration(duration);

        var rawEvent = match.Groups[5].Value;
        if (string.IsNullOrWhiteSpace(rawEvent))
            return null;

        var eventName = KnownEventPool.TryGetValue(rawEvent, out var pooledEvent) ? pooledEvent : rawEvent;
        var level = int.Parse(match.Groups[6].Value);
        var restOfBlock = blockText[(match.Groups[6].Index + match.Groups[6].Length)..];

        var properties = ParseKeyValues(restOfBlock);

        properties.Remove("Usr", out var user);
        if (string.IsNullOrEmpty(user)) properties.Remove("user", out user);
        if (string.IsNullOrEmpty(user)) properties.Remove("p_processName", out user);

        properties.Remove("t_applicationName", out var app);
        if (string.IsNullOrEmpty(app)) properties.Remove("App", out app);

        properties.Remove("t_connectID", out var connectId);
        if (string.IsNullOrEmpty(connectId)) properties.Remove("connectID", out connectId);

        properties.Remove("t_clientID", out var clientId);
        if (string.IsNullOrEmpty(clientId)) properties.Remove("clientID", out clientId);

        properties.Remove("spid", out var spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("SPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("dbpid", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("DBPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("dbmsServerPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("dbmsServerPid", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("DbmsServerPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("serverPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("ServerPID", out spid);
        if (string.IsNullOrEmpty(spid)) properties.Remove("dbpid_1c", out spid);

        properties.Remove("OSThread", out var osThread);
        if (string.IsNullOrEmpty(osThread)) properties.Remove("osthread", out osThread);

        properties.Remove("LongInfoName", out var longInfoName);
        if (string.IsNullOrEmpty(longInfoName)) properties.Remove("longInfoName", out longInfoName);

        properties.Remove("LongInfoWait", out var longInfoWaitStr);
        if (string.IsNullOrEmpty(longInfoWaitStr)) properties.Remove("longInfoWait", out longInfoWaitStr);
        long? longInfoWait = long.TryParse(longInfoWaitStr, out var liwVal) ? liwVal : null;

        properties.Remove("SessionID", out var sessionId);
        if (string.IsNullOrEmpty(sessionId)) properties.Remove("sessionID", out sessionId);
        if (string.IsNullOrEmpty(sessionId)) properties.Remove("SessionId", out sessionId);
        if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(clientId)) sessionId = clientId;

        properties.Remove("Context", out var context);
        properties.Remove("Sql", out var sql);

        properties.Remove("Locks", out var locks);
        properties.Remove("WaitConnections", out var waitConnections);
        properties.Remove("LkSrc", out var lkSrc);
        properties.Remove("descr", out var descr);
        if (string.IsNullOrEmpty(descr)) properties.Remove("Descr", out descr);

        properties.Remove("Rows", out var rowsStr);
        long? rows = long.TryParse(rowsStr, out var rVal) ? rVal : null;

        properties.Remove("InBytes", out var inBytesStr);
        long? inBytes = long.TryParse(inBytesStr, out var inVal) ? inVal : null;

        properties.Remove("OutBytes", out var outBytesStr);
        long? outBytes = long.TryParse(outBytesStr, out var outVal) ? outVal : null;

        properties.Remove("Method", out var method);
        properties.Remove("URI", out var url);
        if (string.IsNullOrEmpty(url)) properties.Remove("Url", out url);

        context = SanitizeText(CleanQuotes(context));
        sql = SanitizeText(CleanQuotes(sql));
        locks = SanitizeText(CleanQuotes(locks));
        descr = SanitizeText(CleanQuotes(descr));
        user = FastStringPool.Intern(SanitizeText(user));
        app = FastStringPool.Intern(SanitizeText(app));
        processName = FastStringPool.Intern(processName);
        processId = FastStringPool.Intern(processId);
        spid = FastStringPool.Intern(SanitizeText(spid));
        osThread = FastStringPool.Intern(SanitizeText(osThread));
        longInfoName = FastStringPool.Intern(SanitizeText(longInfoName));
        sessionId = FastStringPool.Intern(SanitizeText(sessionId));
        eventName = FastStringPool.Intern(eventName);
        connectId = FastStringPool.Intern(SanitizeText(connectId));
        clientId = FastStringPool.Intern(SanitizeText(clientId));
        waitConnections = SanitizeText(waitConnections);
        lkSrc = FastStringPool.Intern(SanitizeText(lkSrc));
        method = FastStringPool.Intern(SanitizeText(method));
        url = SanitizeText(url);

        var cleanProps = new Dictionary<string, string>(properties.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in properties)
        {
            cleanProps[FastStringPool.Intern(k)] = FastStringPool.Intern(SanitizeText(v));
        }

        var idSource = $"{eventDate:O}_{processName}_{processId}_{eventName}_{duration}_{user}_{connectId}_{context?.GetHashCode()}_{sql?.GetHashCode()}";
        var idDoc = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(idSource)));

        return new TechLogDoc
        {
            Id = idDoc,
            Date = eventDate,
            DateFormatted = eventDate.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Duration = duration,
            DurationMs = durationMs,
            DurationSec = durationSec,
            DurationFormatted = FastStringPool.Intern(durationFormatted),
            Event = eventName,
            Level = level,
            ProcessName = processName,
            ProcessId = processId,
            Spid = spid,
            OSThread = osThread,
            LongInfoName = longInfoName,
            LongInfoWait = longInfoWait,
            SessionId = sessionId,
            User = user,
            App = app,
            ConnectId = connectId,
            ClientId = clientId,
            Context = context,
            Sql = sql,
            Locks = locks,
            WaitConnections = waitConnections,
            LkSrc = lkSrc,
            Descr = descr,
            Rows = rows,
            InBytes = inBytes,
            OutBytes = outBytes,
            Method = method,
            Url = url,
            Properties = cleanProps
        };
    }

    /// <summary>
    /// Форматирование микросекунд в наглядный человекочитаемый вид.
    /// </summary>
    public static string FormatDuration(long microseconds)
    {
        if (microseconds < 1000)
            return $"{microseconds} μs";

        var ms = microseconds / 1000.0;
        if (ms < 1000)
            return $"{ms.ToString("F2", CultureInfo.InvariantCulture)} ms";

        var sec = ms / 1000.0;
        if (sec < 60)
            return $"{sec.ToString("F2", CultureInfo.InvariantCulture)} s";

        var min = (int)(sec / 60);
        var remSec = sec % 60;
        return $"{min}m {remSec.ToString("F1", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>
    /// Высокопроизводительное потоковое считывание пар ключ=значение из содержимого блока ТЖ.
    /// Обрабатывает сложные экранированные строки с кавычками '...' и "...".
    /// </summary>
    public static Dictionary<string, string> ParseKeyValues(string input)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input))
            return dict;

        var len = input.Length;
        var i = 0;

        while (i < len)
        {
            while (i < len && (char.IsWhiteSpace(input[i]) || input[i] == ','))
                i++;

            if (i >= len) break;

            var remaining = input.AsSpan(i);
            var nextSep = remaining.IndexOfAny(KeySeparators);
            if (nextSep < 0) break;

            var key = remaining[..nextSep].Trim().ToString().Replace(":", "_");
            i += nextSep;

            if (i >= len || input[i] != '=')
            {
                i++;
                continue;
            }

            i++; // Пропускаем '='

            while (i < len && char.IsWhiteSpace(input[i]))
                i++;

            if (i >= len)
            {
                dict[key] = string.Empty;
                break;
            }

            string value;
            var quoteChar = input[i];

            if (quoteChar == '\'' || quoteChar == '"')
            {
                var valStart = i + 1;
                i++;
                var valSb = new StringBuilder();

                var closed = false;
                while (i < len)
                {
                    if (input[i] == quoteChar)
                    {
                        if (i + 1 < len && input[i + 1] == quoteChar)
                        {
                            valSb.Append(input[valStart..i]);
                            valSb.Append(quoteChar);
                            i += 2;
                            valStart = i;
                            continue;
                        }

                        valSb.Append(input[valStart..i]);
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }

                if (!closed && valStart < len)
                {
                    valSb.Append(input[valStart..]);
                }

                value = valSb.ToString();
            }
            else
            {
                var valSpan = input.AsSpan(i);
                var endSep = valSpan.IndexOfAny(ValueEndSeparators);
                if (endSep >= 0)
                {
                    value = valSpan[..endSep].Trim().ToString();
                    i += endSep;
                }
                else
                {
                    value = valSpan.Trim().ToString();
                    i = len;
                }
            }

            dict[key] = value;
        }

        return dict;
    }

    /// <summary>
    /// Очистка текста от обрамляющих кавычек.
    /// </summary>
    public static string? CleanQuotes(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var trimmed = input.Trim();
        if ((trimmed.StartsWith('\'') && trimmed.EndsWith('\'')) ||
            (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            if (trimmed.Length >= 2)
                return trimmed[1..^1];
        }
        return trimmed;
    }

    /// <summary>
    /// Очистка текста от непечатных управляющих символов и ограничение длины полей.
    /// </summary>
    public static string SanitizeText(string? input, int maxLength = MaxFieldLength)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

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
            if (ch == '\uFEFF' || ch == '\0' || ch == '\r') continue;
            if (ch == '\n') { sb.Append(' '); continue; }
            if (char.IsControl(ch) && ch != '\t') continue;

            sb.Append(ch);
        }

        if (isTruncated)
        {
            sb.Append($" ... [TRUNCATED: {originalLength} -> {maxLength} chars]");
        }

        return sb.ToString();
    }
}
