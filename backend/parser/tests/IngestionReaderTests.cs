using Bashkiria.Events.Parser.Common;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Ingestion;
using Bashkiria.Events.Parser.Processing;
using Microsoft.AspNetCore.Http;
using System.Text;

namespace Parser.Tests;

public sealed class IngestionReaderTests
{
    private readonly EventInputReader _reader = new();
    private readonly EventInputMapper _mapper = new(new CandidateNormalizer(TimeProvider.System));

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    public void CsvHandlesBomQuotedDelimitersMultilineTextAndDecimalComma(string delimiter)
    {
        var csv = "\uFEFFexternal_id" + delimiter + "title" + delimiter + "description" + delimiter + "price_min" + delimiter + "tags\r\n" +
            "one" + delimiter + "\"Концерт, \"\"Вечер\"\"; музыка\"" + delimiter + "\"Первая строка\r\nВторая строка\"" + delimiter + "\"300,50\"" + delimiter + "джаз|дети\r\n";
        var row = Assert.Single(_reader.ReadCsv(csv, 10));
        Assert.Empty(row.Errors);
        Assert.Equal(2, row.Row);
        Assert.Equal("Концерт, \"Вечер\"; музыка", row.Value!.Title);
        Assert.Contains("\r\n", row.Value.Description);
        Assert.Equal(300.50m, row.Value.PriceMin);
        Assert.Equal(new[] { "джаз", "дети" }, row.Value.Tags);
    }

    [Theory]
    [InlineData("title,title\nA,B")]
    [InlineData("unknown,title\nA,B")]
    [InlineData("externalId\none")]
    [InlineData("title,city\n\"unclosed,Ufa")]
    [InlineData("title,city\nA,Ufa,extra")]
    public void InvalidCsvStructureIsRejectedBeforeAnyRecordsAreSaved(string csv) =>
        Assert.Throws<ParserValidationException>(() => _reader.ReadCsv(csv, 10));

    [Fact]
    public void SingleColumnCsvAndJsonHaveTheSameMinimalContract()
    {
        var csv = Assert.Single(_reader.ReadCsv("title\nFixture\n", 10)).Value;
        var json = Assert.Single(_reader.ReadJson("[{\"title\":\"Fixture\"}]", 10)).Value;
        Assert.Equal(json, csv);
        Assert.Null(json!.AgeLimit);
        Assert.Null(json.PriceMin);
    }

    [Fact]
    public void InvalidFieldRejectsOnlyItsRecord()
    {
        var rows = _reader.ReadJson("""[{"title":"Good"},{"title":"Bad","startDate":"15.11.2036"},{"title":"Unknown field","status":"published"}]""", 10);
        Assert.NotNull(rows[0].Value);
        Assert.Null(rows[1].Value);
        Assert.Equal("startDate", Assert.Single(rows[1].Errors).Field);
        Assert.Null(rows[2].Value);
        Assert.Equal("status", Assert.Single(rows[2].Errors).Field);
    }

    [Fact]
    public void JsonDuplicateFieldsAndInvalidRootAreRejected()
    {
        var row = Assert.Single(_reader.ReadJson("""[{"title":"A","Title":"B"}]""", 10));
        Assert.Equal("duplicate_field", Assert.Single(row.Errors).Code);
        Assert.Throws<ParserValidationException>(() => _reader.ReadJson("{}", 10));
        Assert.Throws<ParserValidationException>(() => _reader.ReadJson("[]", 10));
        Assert.Throws<ParserValidationException>(() => _reader.ReadJson("[{", 10));
    }

    [Fact]
    public void RowLimitsApplyToBothFormats()
    {
        Assert.Throws<ParserValidationException>(() => _reader.ReadJson("[{},{}]", 1));
        Assert.Throws<ParserValidationException>(() => _reader.ReadCsv("title\nA\nB", 1));
    }

    [Fact]
    public void CsvAcceptsTimeWithoutSecondsAndRejectsInvalidTime()
    {
        var rows = _reader.ReadCsv("title;startTime\nFixture;17:30\nInvalid;25:00", 10);
        Assert.Equal(new TimeOnly(17, 30), rows[0].Value!.StartTime);
        Assert.Null(rows[1].Value);
        Assert.Equal("startTime", Assert.Single(rows[1].Errors).Field);
    }

    [Fact]
    public void ValidationDoesNotSilentlyCorrectInvalidBusinessValues()
    {
        var errors = _mapper.Validate(new EventInput
        {
            Title = "Fixture", AgeLimit = 5, PriceMin = -1, PriceMax = 0.001m,
            Latitude = 91, Longitude = 10.12345678m,
            SourceUrl = new Uri("file:///C:/private"), Description = "bad\0text"
        });
        foreach (var field in new[] { "ageLimit", "priceMin", "priceMax", "latitude", "longitude", "sourceUrl", "description" })
            Assert.Contains(errors, error => error.Field == field);
    }

    [Fact]
    public void EmptyFieldsRemainUnknownAndFreeEntryRequiresAnExplicitValue()
    {
        var candidate = _mapper.Map(new EventInput { Title = "Fixture" }, "import:fixture");
        Assert.Null(candidate.StartDate);
        Assert.Null(candidate.StartTime);
        Assert.Null(candidate.PriceMin);
        Assert.Null(candidate.IsFree);
        Assert.Null(candidate.AgeLimit);
        Assert.Equal("urn", candidate.SourceUrl.Scheme);
        Assert.Contains(candidate.Issues, issue => issue.Code == "input_requires_review");
        var free = _mapper.Map(new EventInput { Title = "Fixture", IsFree = true }, "import:fixture");
        Assert.Equal(0, free.PriceMin);
        Assert.Equal(0, free.PriceMax);
    }

    [Fact]
    public async Task BodyLimitIsEnforcedWithoutContentLengthAndInvalidUtf8IsRejected()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        var oversized = await Assert.ThrowsAsync<IngestionRequestException>(() => InputBodyReader.ReadAsync(context.Request, "application/json", 4, CancellationToken.None));
        Assert.Equal(413, oversized.StatusCode);
        context.Request.Body = new MemoryStream([0xFF]);
        Assert.Equal(400, (await Assert.ThrowsAsync<IngestionRequestException>(() => InputBodyReader.ReadAsync(context.Request, "application/json", 4, CancellationToken.None))).StatusCode);
        context.Request.ContentType = "text/csv";
        Assert.Equal(415, (await Assert.ThrowsAsync<IngestionRequestException>(() => InputBodyReader.ReadAsync(context.Request, "application/json", 4, CancellationToken.None))).StatusCode);
    }
}
