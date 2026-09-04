using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using OneSTechLogExporter.Core.Models;

namespace OneSTechLogExporter.Core.Serialization;

/// <summary>
/// Высокопроизводительный Source Generator JSON-контекст (.NET 10).
/// Исключает рефлексию при сериализации сотен тысяч записей логов в Elastic / JSON.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(TechLogDoc))]
[JsonSerializable(typeof(EventLogDoc))]
[JsonSerializable(typeof(List<TechLogDoc>))]
[JsonSerializable(typeof(List<EventLogDoc>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(KeyValuePair<string, string>))]
public partial class LogJsonContext : JsonSerializerContext
{
    private static LogJsonContext? _pretty;

    /// <summary>
    /// Контекст для читаемого форматированного JSON с поддержкой Unicode (кириллицы).
    /// </summary>
    public static LogJsonContext Pretty => _pretty ??= new LogJsonContext(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    });
}
