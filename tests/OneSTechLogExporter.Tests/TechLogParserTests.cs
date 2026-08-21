using FluentAssertions;
using OneSTechLogExporter.Core.Models;
using OneSTechLogExporter.Core.Parsers;
using Xunit;

namespace OneSTechLogExporter.Tests;

/// <summary>
/// Автономные юнит-тесты парсинга Технологического Журнала 1С.
/// </summary>
public sealed class TechLogParserTests
{
    private const string SampleLine = "23:48.384002-31985,DBMSSQL,5,p:processName=MultiFront,t:clientID=27,t:applicationName=WebServerExtension,t:connectID=48187,Usr=Администратор,Context='HTTPСервис.API.Модуль : 125'";

    [Fact]
    public void ParseBlock_ShouldExtractCorrectFields()
    {
        var doc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000");

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("DBMSSQL");
        doc.Level.Should().Be(5);
        doc.DateFormatted.Should().Be("2026-07-30 08:23:48.384");
        doc.Duration.Should().Be(31985);
        doc.DurationMs.Should().Be(31.985);
        doc.DurationSec.Should().Be(0.031985);
        doc.DurationFormatted.Should().Be("31.98 ms");
        doc.ProcessName.Should().Be("rphost");
        doc.ProcessId.Should().Be("9000");
        doc.User.Should().Be("Администратор");
        doc.ClientId.Should().Be("27");
        doc.ConnectId.Should().Be("48187");
        doc.App.Should().Be("WebServerExtension");
        doc.Context.Should().Be("HTTPСервис.API.Модуль : 125");
    }

    [Fact]
    public async Task ParseFileAsync_WithTempFile_ShouldStreamDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TechLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "26073008.log");

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLine + "\n" + SampleLine + "\n");

            var docs = new List<TechLogDoc>();
            await foreach (var doc in TechLogParser.ParseFileAsync(tempFile, "rphost", "9000"))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(2);
            docs[0].Event.Should().Be("DBMSSQL");
            docs[0].ProcessName.Should().Be("rphost");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileFromOffsetAsync_WithIncrementalOffset_ShouldReturnOnlyNewDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TechLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "26073008.log");

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLine + "\n");

            // 1. Первая итерация: считываем со смещения 0
            var (firstBatch, midPos) = await TechLogParser.ParseFileFromOffsetAsync(tempFile, "rphost", "9000", 0);
            firstBatch.Should().NotBeEmpty();
            midPos.Should().BeGreaterThan(0);

            // 2. Вторая итерация со смещения midPos (конец файла) не должна вернуть дубликатов
            var (secondBatch, finalPos) = await TechLogParser.ParseFileFromOffsetAsync(tempFile, "rphost", "9000", midPos);
            secondBatch.Should().BeEmpty();
            finalPos.Should().Be(midPos);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SanitizeText_WithOversizedField_ShouldTruncateSafely()
    {
        var hugeString = new string('A', 1500);
        var result = TechLogParser.SanitizeText(hugeString, maxLength: 1000);

        result.Should().StartWith(new string('A', 1000));
        result.Should().Contain("[TRUNCATED: 1500 -> 1000 chars]");
    }

    [Fact]
    public void ParseBlock_WithLongDurationInfo_ShouldExtractActiveStatusAndFields()
    {
        const string longLine = "45:07.345000-10000000,LONGDURATIONINFO,4,process=rphost,p:processName=Base,OSThread=1234,LongInfoName=DBMSSQL,LongInfoWait=10000000,Context='Справочник.Номенклатура.МодульОбъекта : 45'";
        var doc = TechLogParser.ParseBlock(longLine, 2026, 8, 18, 10, "rphost", "1234");

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("LONGDURATIONINFO");
        doc.IsActiveOperation.Should().BeTrue();
        doc.ExecutionStatus.Should().Be("Выполняется");
        doc.OSThread.Should().Be("1234");
        doc.LongInfoName.Should().Be("DBMSSQL");
        doc.LongInfoWait.Should().Be(10000000);
    }
}
