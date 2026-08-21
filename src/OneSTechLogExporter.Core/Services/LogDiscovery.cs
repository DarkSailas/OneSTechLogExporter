namespace OneSTechLogExporter.Core.Services;

/// <summary>
/// Универсальный сервис гибкого поиска файлов логов 1С (Журнала Регистрации и Технологического Журнала) при любой вложенности каталогов.
/// </summary>
public static class LogDiscovery
{
    /// <summary>
    /// Гибкий поиск файлов Технологического Журнала (*.log) с извлечением имени и ID процесса при любой вложенности каталогов.
    /// </summary>
    public static IEnumerable<(string FilePath, string ProcessName, string ProcessId, string FolderName)> FindTechLogFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) yield break;

        if (File.Exists(rootPath) && rootPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(rootPath) ?? "default";
            var (pName, pId) = ParseProcessInfo(parent);
            yield return (rootPath, pName, pId, Path.GetFileName(parent));
            yield break;
        }

        if (!Directory.Exists(rootPath)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootPath, "*.log", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            files = Directory.EnumerateFiles(rootPath, "*.log", SearchOption.TopDirectoryOnly);
        }

        foreach (var file in files)
        {
            var parentDir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(parentDir)) continue;

            var dirName = Path.GetFileName(parentDir);
            var (pName, pId) = ParseProcessInfo(parentDir);
            yield return (file, pName, pId, dirName);
        }
    }

    /// <summary>
    /// Парсинг имени и ID процесса из пути каталога (rphost_1234, COMMON, TLOCK и т.д.).
    /// </summary>
    private static (string ProcessName, string ProcessId) ParseProcessInfo(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName)) return ("default", "");

        if (dirName.Contains('_'))
        {
            var parts = dirName.Split('_', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "");
        }

        // Если текущая папка без подчеркивания (например COMMON, TLOCK), проверяем родительскую папку
        var parentDir = Path.GetDirectoryName(dirPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            var parentName = Path.GetFileName(parentDir);
            if (!string.IsNullOrEmpty(parentName) && parentName.Contains('_'))
            {
                var parts = parentName.Split('_', 2);
                return (parts[0], parts.Length > 1 ? parts[1] : "");
            }
        }

        return (dirName, "");
    }

    /// <summary>
    /// Гибкий поиск файла словаря 1Cv8.lgf в указанном каталоге и любых его подкаталогах.
    /// </summary>
    public static string? FindEventLogDictionary(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;

        if (File.Exists(rootPath))
        {
            if (Path.GetFileName(rootPath).Equals("1Cv8.lgf", StringComparison.OrdinalIgnoreCase))
                return rootPath;

            rootPath = Path.GetDirectoryName(rootPath) ?? rootPath;
        }

        if (!Directory.Exists(rootPath)) return null;

        var topLgf = Path.Combine(rootPath, "1Cv8.lgf");
        if (File.Exists(topLgf)) return topLgf;

        try
        {
            return Directory.EnumerateFiles(rootPath, "1Cv8.lgf", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Гибкий поиск файлов Журнала Регистрации (*.lgp) в указанном каталоге и подкаталогах.
    /// </summary>
    public static IEnumerable<string> FindEventLogFiles(string rootPath, string targetFileName = "")
    {
        if (string.IsNullOrWhiteSpace(rootPath)) yield break;

        if (File.Exists(rootPath) && rootPath.EndsWith(".lgp", StringComparison.OrdinalIgnoreCase))
        {
            yield return rootPath;
            yield break;
        }

        var dirPath = File.Exists(rootPath) ? Path.GetDirectoryName(rootPath) : rootPath;
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) yield break;

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
                foundTarget = Directory.EnumerateFiles(dirPath, targetFileName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (Exception)
            {
                // Игнорируем ошибки доступа
            }

            if (foundTarget != null)
            {
                yield return foundTarget;
                yield break;
            }
        }

        IEnumerable<string> lgpFiles;
        try
        {
            lgpFiles = Directory.EnumerateFiles(dirPath, "*.lgp", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            lgpFiles = Directory.EnumerateFiles(dirPath, "*.lgp", SearchOption.TopDirectoryOnly);
        }

        foreach (var file in lgpFiles)
        {
            yield return file;
        }
    }
}
