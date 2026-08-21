using FluentAssertions;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using Xunit;

namespace OneSTechLogExporter.Tests;

/// <summary>
/// Автономные юнит-тесты потокового парсинга Журнала Регистрации 1С.
/// </summary>
public sealed class EventLogParserTests
{
    private const string SampleLgfContent = @"{1,
{1,0,""Администратор"",1},
{3,""HTTPServiceConnection"",1},
{4,""_$Session$_.Start"",1},
{5,""Справочник.Номенклатура"",1}
}";

    private const string SampleLgpEntry = @"{20260817123045,N,
{0,0},1,1,1,1,
I,
""Успешный вход в систему""""1С:Предприятие"""" с клиента"",
1,
""ДанныеСеанса"",
1,
1,
1,
1,
1,
""12345""
},";

    [Fact]
    public async Task ParseDictionaryAsync_WithTempFile_ShouldLoadEntries()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLgfContent);

            var dict = await EventLogParser.ParseDictionaryAsync(tempFile);

            dict.Should().NotBeNull();
            dict.Users.Should().NotBeEmpty();
            dict.Apps.Should().NotBeEmpty();
            dict.Events.Should().NotBeEmpty();
            dict.Metas.Should().NotBeEmpty();

            dict.Users.Values.Should().Contain("Администратор");
            dict.Apps.Values.Should().Contain("HTTPServiceConnection");
            dict.Events.Values.Should().Contain("_$Session$_.Start");
            dict.Metas.Values.Should().Contain("Справочник.Номенклатура");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseEntry_ShouldExtractLgpFieldsCorrectly()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "Администратор";
        dict.Apps["1"] = "HTTPServiceConnection";
        dict.Events["1"] = "_$Session$_.Start";
        dict.Metas["1"] = "Справочник.Номенклатура";

        var doc = EventLogParser.ParseEntry(SampleLgpEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Сеанс. Начало");
        doc.User.Should().Be("Администратор");
        doc.App.Should().Be("HTTPServiceConnection");
        doc.Importance.Should().Be("Информация");
        doc.Meta.Should().Be("Справочник.Номенклатура");
        doc.Comment.Should().Be("Успешный вход в систему\"1С:Предприятие\" с клиента");
        doc.Data.Should().Be("ДанныеСеанса");
        doc.Session.Should().Be("12345");
        doc.DateFormatted.Should().Be("2026-08-17 12:30:45");
    }

    [Fact]
    public async Task ParseLogAsync_WithProgressReporting_ShouldStreamAllDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EventLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "20260817000000.lgp");

        var dict = new LgfDictionary();
        dict.Users["1"] = "Администратор";
        dict.Events["1"] = "_$Session$_.Start";

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLgpEntry + "\n" + SampleLgpEntry + "\n");

            var progressReports = 0;
            var progress = new Progress<(long BytesRead, long TotalBytes)>(_ => Interlocked.Increment(ref progressReports));

            var docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, progress))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(2);
            docs[0].Event.Should().Be("Сеанс. Начало");
            docs[0].User.Should().Be("Администратор");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
