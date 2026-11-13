using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Storage;

var builder = WebApplication.CreateBuilder(args);

// load env vars
if (builder.Environment.IsDevelopment())
{
    var envPath = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, "..", ".env"));

    if (File.Exists(envPath))
    {
        DotNetEnv.Env.NoClobber().Load(envPath);

        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
    }
}

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IEventParser, GkzParser>();
builder.Services.AddSingleton<GkzListingParser>();
builder.Services.AddTransient<GkzCollector>();
builder.Services.AddSingleton<IRawDocumentStore, PostgresRawDocumentStore>();

builder.Services
    .AddHttpClient<GkzClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "BashkiriaEventsParser/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RawDocumentStorageException exception)
    {
        app.Logger.LogError(exception, "Failed to persist a raw source document");
        await Results.Problem(
            title: "Не удалось сохранить исходную страницу. Проверьте подключение к БД и миграции.",
            statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
    }
});

app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapGet("/parser/preview/gkz/collect",
        async (
            GkzCollector collector,
            ILogger<GkzCollector> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await collector.CollectAsync(cancellationToken));
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Failed to download GKZ listing for collection");
                return Results.Problem(
                    title: "Не удалось загрузить афишу ГКЗ.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "GKZ listing download timed out during collection");
                return Results.Problem(
                    title: "Источник не ответил вовремя.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

    app.MapGet("/parser/preview/gkz/links",
        async (
            GkzClient client,
            GkzListingParser listingParser,
            ILogger<GkzClient> logger,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var document = await client.GetListingPageAsync(
                    cancellationToken);

                var links = listingParser.ParseLinks(
                    document.Content,
                    GkzClient.ListingUrl);

                return Results.Ok(new
                {
                    rawDocumentId = document.Id,
                    count = links.Count,
                    links
                });
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception,
                    "Failed to download GKZ listing");

                return Results.Problem(
                    title: "Не удалось загрузить афишу ГКЗ.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception,
                    "GKZ listing download timed out");

                return Results.Problem(
                    title: "Источник не ответил вовремя.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

    app.MapGet("/parser/preview/gkz",
        async (
            GkzClient client,
            IEventParser parser,
            ILogger<GkzClient> logger,
            CancellationToken cancellationToken) =>
        {
            var pageUrl = new Uri(
                "https://gkzbashkortostan.ru/events/" +
                "yubilejnyj-gala-konczert-studii-diva/");

            try
            {
                var document = await client.GetEventPageAsync(
                    pageUrl,
                    cancellationToken);

                var items = parser.Parse(document.Content, document.Url)
                    .Select(item => item with { RawDocumentId = document.Id });

                return Results.Ok(items);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception,
                    "Failed to download GKZ event page {PageUrl}", pageUrl);

                return Results.Problem(
                    title: "Не удалось загрузить карточку ГКЗ.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception,
                    "GKZ event page download timed out {PageUrl}", pageUrl);

                return Results.Problem(
                    title: "Источник не ответил вовремя.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });

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
