using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IEventParser, GkzParser>();

var app = builder.Build();
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapPost("/parser/preview",
        (ParsePreviewRequest request, IEventParser parser) =>
        {
            if (string.IsNullOrWhiteSpace(request.Html))
            {
                return Results.BadRequest(
                    new { message = "HTML не должен быть пустым." });
            }

            if (request.PageUrl is null ||
                !request.PageUrl.IsAbsoluteUri ||
                request.PageUrl.Scheme != Uri.UriSchemeHttps ||
                request.PageUrl.Host != "gkzbashkortostan.ru" ||
                !request.PageUrl.AbsolutePath.StartsWith(
                    "/events/",
                    StringComparison.Ordinal))
            {
                return Results.BadRequest(
                    new { message = "Укажи HTTPS-адрес карточки ГКЗ." });
            }

            var items = parser.Parse(
                request.Html,
                request.PageUrl);

            return Results.Ok(items);
        });
}

app.Run();
