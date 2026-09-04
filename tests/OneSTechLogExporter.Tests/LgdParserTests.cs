using FluentAssertions;
using Microsoft.Data.Sqlite;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using Xunit;

namespace OneSTechLogExporter.Tests;

public sealed class LgdParserTests : IDisposable
{
    private readonly string _tempDbPath;

    public LgdParserTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), "1Cv8Test_" + Guid.NewGuid().ToString("N") + ".lgd");
        CreateSampleLgdDatabase(_tempDbPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
                File.Delete(_tempDbPath);
        }
        catch { }
    }

    private static void CreateSampleLgdDatabase(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE UserCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO UserCodes VALUES (1, 'Администратор');

CREATE TABLE AppCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO AppCodes VALUES (1, '1CV8C');

CREATE TABLE EventCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO EventCodes VALUES (1, '_$Session$_.Start');

CREATE TABLE MetadataCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO MetadataCodes VALUES (1, 'Справочник.Номенклатура');

CREATE TABLE EventLog (
    rowID INTEGER PRIMARY KEY,
    severity INTEGER,
    date INTEGER,
    connectID INTEGER,
    session INTEGER,
    transactionStatus INTEGER,
    transactionID INTEGER,
    userCode INTEGER,
    appCode INTEGER,
    eventCode INTEGER,
    comment TEXT,
    dataPresentation TEXT,
    metadataCodes TEXT
);

-- 2026-08-17 12:30:45 UTC = 639230562450000000 ticks in .NET. Divided by 1000 = 639230562450000
INSERT INTO EventLog VALUES (
    1,
    0,
    639230562450000,
    48187,
    12345,
    1,
    999,
    1,
    1,
    1,
    'Вход в систему',
    'ДанныеСеанса',
    '1'
);

INSERT INTO EventLog VALUES (
    2,
    2,
    639230562450000,
    48188,
    12345,
    2,
    1000,
    1,
    1,
    1,
    'Критическая ошибка базы данных',
    'ОбъектНеНайден',
    '1'
);
";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task ParseLgdAsync_ShouldReadAndDecodeAllRecords()
    {
        var docs = new List<EventLogDoc>();
        await foreach (var doc in LgdParser.ParseLgdAsync(_tempDbPath))
        {
            docs.Add(doc);
        }

        docs.Should().HaveCount(2);

        // Сортировка по убыванию (сначала новые)
        var errDoc = docs[0];
        errDoc.Importance.Should().Be("Ошибка");
        errDoc.Comment.Should().Be("Критическая ошибка базы данных");
        errDoc.User.Should().Be("Администратор");
        errDoc.App.Should().Be("1CV8C");
        errDoc.Event.Should().Be("Сеанс. Начало");
        errDoc.Tran.Should().Be("R(1000)");

        var infoDoc = docs[1];
        infoDoc.Importance.Should().Be("Информация");
        infoDoc.Comment.Should().Be("Вход в систему");
        infoDoc.Tran.Should().Be("X(999)");
    }
}
