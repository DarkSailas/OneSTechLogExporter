namespace OneSTechLogExporter.Core.Services;

/// <summary>
/// Универсальный сервис гибкого поиска файлов логов 1С (Журнала Регистрации и Технологического Журнала),
/// полностью независимый от структуры каталогов, с поддержкой одиночных файлов и произвольной вложенности.
/// </summary>
public static class LogDiscovery
{
    private static readonly EnumerationOptions SafeRecurseOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly EnumerationOptions SafeTopOnlyOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    /// <summary>
    /// Гибкий поиск файлов Технологического Журнала:
    /// - Если передан путь к файлу — возвращает его независимо от расширения и папки.
    /// - Если передан каталог — ищет *.log и *.txt файлы на любой глубине вложенности.
    /// </summary>
    public static IEnumerable<(string FilePath, string ProcessName, string ProcessId, string FolderName)> FindTechLogFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) yield break;

        // 1. Одиночный файл
        if (File.Exists(rootPath))
        {
            var (pName, pId) = ParseProcessInfo(rootPath);
            var folderName = Path.GetFileName(Path.GetDirectoryName(rootPath) ?? "") ?? "default";
            yield return (rootPath, pName, pId, folderName);
            yield break;
        }

        if (!Directory.Exists(rootPath)) yield break;

        // 2. Каталог: рекурсивный поиск *.log
        List<string> foundFiles = [];
        try
        {
            foundFiles.AddRange(Directory.EnumerateFiles(rootPath, "*.log", SafeRecurseOptions));
        }
        catch
        {
            try { foundFiles.AddRange(Directory.EnumerateFiles(rootPath, "*.log", SafeTopOnlyOptions)); } catch { }
        }

        // Если *.log не найдены, пробуем искать *.txt
        if (foundFiles.Count == 0)
        {
            try
            {
                foundFiles.AddRange(Directory.EnumerateFiles(rootPath, "*.txt", SafeRecurseOptions));
            }
            catch { }
        }

        foreach (var file in foundFiles)
        {
            var (pName, pId) = ParseProcessInfo(file);
            var parentDir = Path.GetDirectoryName(file);
            var dirName = !string.IsNullOrEmpty(parentDir) ? Path.GetFileName(parentDir) : "default";
            yield return (file, pName, pId, dirName);
        }
    }

    /// <summary>
    /// Извлечение имени процесса и ID процесса из пути к файлу или каталогу.
    /// Анализирует имя файла, имя родительской папки и цепочку предков.
    /// </summary>
    public static (string ProcessName, string ProcessId) ParseProcessInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("rphost", "");

        // 1. Проверяем имя файла (например "rphost_1234_26083114.log", "rphost_1234.log")
        if (File.Exists(path) || path.Contains('.'))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var fileMatch = System.Text.RegularExpressions.Regex.Match(
                fileName,
                @"(rphost|rmngr|ragent|1cv8c?|crserver|ras|common|tlock|excp)(?:_(\d+))?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (fileMatch.Success)
            {
                return (fileMatch.Groups[1].Value.ToLowerInvariant(), fileMatch.Groups[2].Success ? fileMatch.Groups[2].Value : "");
            }
        }

        // 2. Проверяем каталог файла или переданный каталог
        var currentDir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        var depth = 0;

        while (!string.IsNullOrEmpty(currentDir) && depth < 4)
        {
            var dirName = Path.GetFileName(currentDir);
            if (!string.IsNullOrEmpty(dirName))
            {
                // Стандартный шаблон 1С: rphost_1234
                if (dirName.Contains('_'))
                {
                    var parts = dirName.Split('_', 2);
                    return (parts[0], parts.Length > 1 ? parts[1] : "");
                }

                // Именованные каталоги: COMMON, TLOCK, EXCP, etc.
                var dirMatch = System.Text.RegularExpressions.Regex.Match(
                    dirName,
                    @"^(rphost|rmngr|ragent|1cv8c?|crserver|ras|common|tlock|excp)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (dirMatch.Success)
                {
                    return (dirMatch.Groups[1].Value.ToLowerInvariant(), "");
                }
            }

            currentDir = Path.GetDirectoryName(currentDir);
            depth++;
        }

        // Если имя каталога не стандартное (например, "Desktop", "Logs", "Temp"), берем имя каталога
        var fallbackDir = File.Exists(path) ? Path.GetFileName(Path.GetDirectoryName(path) ?? "") : Path.GetFileName(path);
        return (!string.IsNullOrEmpty(fallbackDir) ? fallbackDir : "rphost", "");
    }

    /// <summary>
    /// Глубокий интеллектуальный поиск словаря 1Cv8.lgf:
    /// - Проверяет сам путь (если указан прямо на 1Cv8.lgf).
    /// - Проверяет текущую директорию и все ее подкаталоги.
    /// - Ищет вверх по иерархии (в родительских каталогах и соседних папках 1Cv8Log).
    /// </summary>
    public static string? FindEventLogDictionary(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;

        // 1. Передан непосредственно файл словаря
        if (File.Exists(rootPath) && Path.GetFileName(rootPath).Equals("1Cv8.lgf", StringComparison.OrdinalIgnoreCase))
        {
            return rootPath;
        }

        var startDir = File.Exists(rootPath) ? Path.GetDirectoryName(rootPath) : rootPath;
        if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir)) return null;

        // 2. Проверяем словарь прямо в текущей папке
        var directLgf = Path.Combine(startDir, "1Cv8.lgf");
        if (File.Exists(directLgf)) return directLgf;

        // 3. Рекурсивный поиск во всех подкаталогах
        try
        {
            var subLgf = Directory.EnumerateFiles(startDir, "1Cv8.lgf", SafeRecurseOptions).FirstOrDefault();
            if (subLgf != null) return subLgf;
        }
        catch { }

        // 4. Поиск вверх по иерархии предков (до 4 уровней вверх)
        var ancestor = Path.GetDirectoryName(startDir);
        var level = 0;
        while (!string.IsNullOrEmpty(ancestor) && Directory.Exists(ancestor) && level < 4)
        {
            // Проверяем 1Cv8.lgf в самом предке
            var ancestorLgf = Path.Combine(ancestor, "1Cv8.lgf");
            if (File.Exists(ancestorLgf)) return ancestorLgf;

            // Проверяем соседние папки 1Cv8Log
            var siblingLogDir = Path.Combine(ancestor, "1Cv8Log", "1Cv8.lgf");
            if (File.Exists(siblingLogDir)) return siblingLogDir;

            ancestor = Path.GetDirectoryName(ancestor);
            level++;
        }

        return null;
    }

    /// <summary>
    /// Гибкий поиск файлов событий Журнала Регистрации:
    /// - Если передан одиночный файл (не 1Cv8.lgf) — возвращает его (для .lgx находит парный .lgp).
    /// - Если передан 1Cv8.lgf — ищет все файлы *.lgp и *.lgd в каталоге словаря и подкаталогах.
    /// - Если передан каталог — ищет все *.lgp и *.lgd файлы на любой глубине вложенности.
    /// </summary>
    public static IEnumerable<string> FindEventLogFiles(string rootPath, string targetFileName = "")
    {
        if (string.IsNullOrWhiteSpace(rootPath)) yield break;

        // 1. Одиночный файл
        if (File.Exists(rootPath))
        {
            // Если передан индексный файл .lgx, пробуем найти парный файл данных .lgp
            if (Path.GetExtension(rootPath).Equals(".lgx", StringComparison.OrdinalIgnoreCase))
            {
                var companionLgp = Path.ChangeExtension(rootPath, ".lgp");
                if (File.Exists(companionLgp))
                {
                    yield return companionLgp;
                    yield break;
                }
            }

            // Если передан именно 1Cv8.lgf, ищем .lgp/.lgd в его каталоге
            if (Path.GetFileName(rootPath).Equals("1Cv8.lgf", StringComparison.OrdinalIgnoreCase))
            {
                var dictDir = Path.GetDirectoryName(rootPath);
                if (!string.IsNullOrEmpty(dictDir) && Directory.Exists(dictDir))
                {
                    foreach (var f in FindEventLogInDirectory(dictDir, targetFileName))
                    {
                        yield return f;
                    }
                }
                yield break;
            }

            // Передан любой файл событий (.lgp, .lgd, .txt или любой другой)
            yield return rootPath;
            yield break;
        }

        if (!Directory.Exists(rootPath)) yield break;

        // 2. Каталог
        foreach (var f in FindEventLogInDirectory(rootPath, targetFileName))
        {
            yield return f;
        }
    }

    private static IEnumerable<string> FindEventLogInDirectory(string dirPath, string targetFileName)
    {
        if (!string.IsNullOrEmpty(targetFileName))
        {
            var directFile = Path.Combine(dirPath, targetFileName);
            if (File.Exists(directFile))
            {
                yield return directFile;
                yield break;
            }

            string? foundTarget = null;
            try
            {
                foundTarget = Directory.EnumerateFiles(dirPath, targetFileName, SafeRecurseOptions).FirstOrDefault();
            }
            catch { }

            if (foundTarget != null)
            {
                yield return foundTarget;
                yield break;
            }
        }

        // Поиск файлов баз SQLite (.lgd)
        List<string> foundFiles = [];
        try
        {
            foundFiles.AddRange(Directory.EnumerateFiles(dirPath, "*.lgd", SafeRecurseOptions));
        }
        catch { }

        // Поиск классических файлов событий (.lgp)
        try
        {
            foundFiles.AddRange(Directory.EnumerateFiles(dirPath, "*.lgp", SafeRecurseOptions));
        }
        catch
        {
            try
            {
                foundFiles.AddRange(Directory.EnumerateFiles(dirPath, "*.lgp", SafeTopOnlyOptions));
            }
            catch { }
        }

        foreach (var file in foundFiles)
        {
            yield return file;
        }
    }

    /// <summary>
    /// Извлечение даты из имени файла ЖР 1С (YYYYMMDDHHmmss.lgp).
    /// </summary>
    public static DateTime? TryExtractEventLogFileDate(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(?<!\d)(20\d{2})(\d{2})(\d{2})");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var yyyy) &&
            int.TryParse(m.Groups[2].Value, out var mm) && mm is >= 1 and <= 12 &&
            int.TryParse(m.Groups[3].Value, out var dd) && dd is >= 1 and <= 31)
        {
            try { return new DateTime(yyyy, mm, dd); } catch { }
        }
        try { return File.GetLastWriteTime(filePath).Date; } catch { return null; }
    }

    /// <summary>
    /// Интеллектуальная фильтрация файлов ЖР 1С (.lgp) по интервалу времени [filterFrom, filterTo].
    /// В 1С каждый файл .lgp покрывает интервал от даты в имени файла до даты начала следующего файла.
    /// </summary>
    public static List<string> FilterEventLogFilesByDate(IEnumerable<string> files, DateTime? filterFrom, DateTime? filterTo)
    {
        var fileList = files.ToList();
        if (!filterFrom.HasValue && !filterTo.HasValue)
            return fileList.OrderByDescending(f => TryExtractEventLogFileDate(f) ?? DateTime.MinValue).ToList();

        if (fileList.Count <= 1)
            return fileList;

        var minDate = filterFrom?.Date ?? DateTime.MinValue;
        var maxDate = filterTo.HasValue ? filterTo.Value.Date.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

        var parsed = fileList.Select(f => (File: f, Date: TryExtractEventLogFileDate(f))).ToList();

        if (parsed.All(p => !p.Date.HasValue))
            return fileList;

        var sorted = parsed.OrderBy(p => p.Date ?? DateTime.MinValue).ToList();
        var selected = new List<string>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            if (!cur.Date.HasValue)
            {
                selected.Add(cur.File);
                continue;
            }

            var start = cur.Date.Value;
            var end = (i + 1 < sorted.Count && sorted[i + 1].Date.HasValue)
                ? sorted[i + 1].Date!.Value
                : DateTime.MaxValue;

            if (start <= maxDate && end >= minDate)
            {
                selected.Add(cur.File);
            }
        }

        var result = selected.Count > 0 ? selected : fileList;
        return result.OrderByDescending(f => TryExtractEventLogFileDate(f) ?? DateTime.MinValue).ToList();
    }
}
