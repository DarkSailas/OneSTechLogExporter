using System.Text.Json.Serialization;

namespace OneSTechLogExporter.Core.Models;

/// <summary>
/// Модель структурированного документа записи Журнала Регистрации 1С для экспорта.
/// </summary>
public sealed record EventLogDoc
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    public required DateTime Date { get; init; }

    public required string DateFormatted { get; init; } // Наглядный формат даты (например "2026-07-30 08:23:48")

    public string? Event { get; init; }

    public string? User { get; init; }

    public string? Meta { get; init; }

    public string? Tran { get; init; }

    public string? App { get; init; }

    public string? Comment { get; init; }

    public string? Importance { get; init; }

    public string? Data { get; init; }

    public string? Session { get; init; }
}

/// <summary>
/// Модель структурированного документа записи Технологического Журнала 1С для экспорта.
/// </summary>
public sealed record TechLogDoc
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    public required DateTime Date { get; init; }

    public required string DateFormatted { get; init; } // Наглядный формат даты (например "2026-07-30 08:23:48.384")

    public required long Duration { get; init; } // Длительность в микросекундах (мкс)

    public required double DurationMs { get; init; } // Длительность в миллисекундах (мс)

    public required double DurationSec { get; init; } // Длительность в секундах (с)

    public required string DurationFormatted { get; init; } // Наглядная строка длительности (например "31.98 ms")

    public required string Event { get; init; }

    public required int Level { get; init; }

    public string? ProcessName { get; init; }

    public string? ProcessId { get; init; }

    public string? Spid { get; init; }            // Идентификатор серверного процесса / потока СУБД (spid / dbpid)

    public string? OSThread { get; init; }        // Идентификатор потока ОС (OSThread)

    public string? SessionId { get; init; }       // Номер сеанса 1С (SessionID / t_clientID)

    public string? LongInfoName { get; init; }    // Целевое действие для LONGDURATIONINFO (DBMSSQL, CALL, TLOCK и др.)

    public long? LongInfoWait { get; init; }      // Время выполнения на момент среза (мкс)

    public string? User { get; init; }

    public string? App { get; init; }

    public string? ConnectId { get; init; }

    public string? ClientId { get; init; }

    public string? Context { get; init; }

    public string? Sql { get; init; }

    // Поля 100% паритета с Magnit pipeline.json
    public string? Locks { get; init; }          // Данные о блокировках ресурсов (TLOCK/TDEADLOCK)

    public string? WaitConnections { get; init; } // Ожидающие соединения при блокировках (TTIMEOUT/TDEADLOCK)

    public string? LkSrc { get; init; }           // Идентификатор источника блокировки

    public string? Descr { get; init; }           // Полный текст описания ошибки (EXCP / EXCPCNTX)

    public long? Rows { get; init; }              // Количество обрабатываемых строк СУБД

    public long? InBytes { get; init; }           // Входящий сетевой/HTTP трафик (байт)

    public long? OutBytes { get; init; }          // Исходящий сетевой/HTTP трафик (байт)

    public string? Method { get; init; }          // Метод REST / HTTP (GET, POST и т.д.)

    public string? Url { get; init; }             // Вызываемый URI / URL адрес

    public Dictionary<string, string> Properties { get; init; } = [];

    /// <summary>
    /// Флаг активного (незавершенного, длящегося на момент записи) события (LONGDURATIONINFO).
    /// </summary>
    [JsonIgnore]
    public bool IsActiveOperation => string.Equals(Event, "LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(LongInfoName)
        || LongInfoWait.HasValue;

    /// <summary>
    /// Статус выполнения операции (Выполняется / Завершено).
    /// </summary>
    [JsonIgnore]
    public string ExecutionStatus => IsActiveOperation ? "Выполняется" : "Завершено";
}

/// <summary>
/// Словарь данных Журнала Регистрации (пользователи, приложения, события, метаданные).
/// </summary>
public sealed class LgfDictionary
{
    public Dictionary<string, string> Users { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Apps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Metas { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Главные параметры конфигурации службы экспорта.
/// </summary>
public sealed class ExporterOptions
{
    public const string SectionName = "Exporter";

    public int PollingIntervalSeconds { get; set; } = 30;
    public string StateFilePath { get; set; } = "state.json";
    public FileDumpSettings FileDump { get; set; } = new();
    public EventLogSettings EventLog { get; set; } = new();
    public TechLogSettings TechLog { get; set; } = new();
    public ElasticSettings Elastic { get; set; } = new();
    public KibanaSettings Kibana { get; set; } = new();
}

/// <summary>
/// Настройки выгрузки распарсенных логов 1С в локальные JSON-файлы (JSONL / append-режим с ротацией по размеру и количеству).
/// </summary>
public sealed class FileDumpSettings
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "parsed_logs"; // Переименовываемый каталог выгрузки в корне приложения

    /// <summary>
    /// Шаблон маски имени файла дампа ТЖ (поддерживает макросы {N}, {PREFIX}). Настраивается в appsettings.json. Например: data_tglog_{N}.json
    /// </summary>
    public string TechLogFileNamePattern { get; set; } = "data_tglog_{N}.json";

    /// <summary>
    /// Шаблон маски имени файла дампа ЖР (поддерживает макросы {N}, {PREFIX}). Настраивается в appsettings.json. Например: data_evlog_{N}.json
    /// </summary>
    public string EventLogFileNamePattern { get; set; } = "data_evlog_{N}.json";

    /// <summary>
    /// Лимит количества хранящихся файлов/итераций дампов (по умолчанию 30 итераций, старые файлы автоматически удаляются).
    /// </summary>
    public int RetainedFileCountLimit { get; set; } = 30;

    /// <summary>
    /// Максимальный размер одного файла дампа в мегабайтах (0 = без лимита по размеру).
    /// </summary>
    public int MaxFileSizeMb { get; set; } = 0;

    /// <summary>
    /// Максимальное количество записей/строк в одном файле дампа (0 = без лимита по строкам).
    /// </summary>
    public int MaxFileRecordCount { get; set; } = 0;

    /// <summary>
    /// Стратегия ротации файлов дампов: "Size" (по размеру МБ), "RecordCount" (по числу записей), "SizeOrRecordCount" (по размеру или числу записей).
    /// </summary>
    public string RollStrategy { get; set; } = "SizeOrRecordCount";
}

/// <summary>
/// Настройки мониторинга Журнала Регистрации 1С.
/// </summary>
public sealed class EventLogSettings
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = string.Empty;
    public string IndexId { get; set; } = "prod";
    public string Periodicity { get; set; } = "h"; // "h" (часовой) или "d" (дневной)
    public int HourDelta { get; set; } = 1;
    public string FileName { get; set; } = string.Empty;
}

/// <summary>
/// Настройки мониторинга Технологического Журнала 1С.
/// </summary>
public sealed class TechLogSettings
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = string.Empty;
    public string IndexId { get; set; } = "prod";
    public int MaxAgeHours { get; set; } = 24;
}

/// <summary>
/// Настройки подключения и авторизации Elasticsearch / OpenSearch.
/// </summary>
public sealed class ElasticSettings
{
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "http://localhost:9200";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string EventLogIndexPrefix { get; set; } = "events";
    public string TechLogIndexPrefix { get; set; } = "techlog";
    public int BulkBatchSize { get; set; } = 1000;
}

/// <summary>
/// Настройки веб-интерфейса Kibana / OpenSearch Dashboards.
/// </summary>
public sealed class KibanaSettings
{
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "http://localhost:5601";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string IndexPatternEventLog { get; set; } = "events_*";
    public string IndexPatternTechLog { get; set; } = "techlog_*";
}
