using Bashkiria.Events.Parser.Api;
using Bashkiria.Events.Parser.Clients;
using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Data;
using Bashkiria.Events.Parser.Jobs;
using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;
using Bashkiria.Events.Parser.Processing;
using Bashkiria.Events.Parser.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.RegularExpressions;
using Bashkiria.Events.Parser.Ingestion;
using Bashkiria.Events.Parser.References;

namespace Bashkiria.Events.Parser.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddParser(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<IngestionOptions>().Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<EventInputReader>();
        services.AddSingleton<EventInputMapper>();
        services.AddSingleton<EventIngestionService>();
        services.AddOptions<ParserOptions>().Bind(configuration.GetSection(ParserOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => environment.IsDevelopment() || !string.IsNullOrWhiteSpace(options.ApiKey), "Parser:ApiKey is required outside Development.")
            .Validate(options => options.Sources.Count is > 0 and <= 20 &&
                options.Sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == options.Sources.Count, "Source IDs must be unique.")
            .Validate(ValidSources, "Sources must have valid IDs, HTTPS URLs, parser kinds and event path patterns.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<ParserDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IConfiguration>();
            var connection = settings.GetConnectionString("ParserDatabase");
            if (string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(settings["PGHOST"]))
                connection = new NpgsqlConnectionStringBuilder
                {
                    Host = settings["PGHOST"],
                    Port = int.Parse(settings["PGPORT"] ?? "5432", System.Globalization.CultureInfo.InvariantCulture),
                    Database = settings["PGDATABASE"],
                    Username = settings["PGUSER"],
                    Password = settings["PGPASSWORD"],
                    Timeout = 5,
                    CommandTimeout = 10
                }.ConnectionString;
            if (string.IsNullOrWhiteSpace(connection)) throw new ParserStorageException("Configure ConnectionStrings:ParserDatabase.");
            options.UseNpgsql(connection);
        });
        services.AddSingleton<IRawDocumentStore, EfRawDocumentStore>();
        services.AddSingleton<IEventStore, EfEventStore>();
        services.AddSingleton<CandidateNormalizer>();
        services.AddSingleton<VenueResolver>();
        services.AddSingleton<ReferenceCatalog>();
        services.AddSingleton<ReferenceEventNormalizer>();
        services.AddSingleton<IEventParser, GkzParser>();
        services.AddSingleton<GkzListingParser>();
        services.AddSingleton<StructuredEventParser>();
        services.AddSingleton<ISourceEventParser, GkzSourceParser>();
        services.AddSingleton<ISourceEventParser>(provider => provider.GetRequiredService<StructuredEventParser>());
        services.AddSingleton<ISourceEventParser, GorodzovetParser>();
        services.AddSingleton<ISourceEventParser, BashgfParser>();
        services.AddSingleton<ISourceEventParser, BashoperaParser>();
        services.AddSingleton<SourceRegistry>();
        services.AddSingleton<CollectionRunStore>();
        services.AddSingleton<EventReviewService>();
        services.AddTransient<SourceCollector>();
        services.AddTransient<GkzCollector>();
        services.AddHostedService<CollectionWorker>();
        services.AddExceptionHandler<ParserExceptionHandler>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            ApiErrors.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddHttpClient<SourcePageClient>(ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);
        services.AddHttpClient<GkzClient>(ConfigureHttpClient)
            .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);
        return services;

        static void ConfigureHttpClient(IServiceProvider provider, HttpClient client)
        {
            var options = provider.GetRequiredService<IOptions<ParserOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        }
    }

    private static HttpClientHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
    };

    private static bool ValidSources(ParserOptions options)
    {
        try
        {
            foreach (var source in options.Sources)
            {
                if (!Regex.IsMatch(source.Id, "^[a-z][a-z0-9_-]{0,63}$") ||
                    !Uri.TryCreate(source.ListingUrl, UriKind.Absolute, out var url) || !Common.SourceUrls.IsAllowed(url, url) ||
                    !SourceKinds.Allows(source.Kind, url)) return false;
                using var request = new HttpRequestMessage();
                if (!string.IsNullOrWhiteSpace(source.UserAgent)) request.Headers.UserAgent.ParseAdd(source.UserAgent);
                _ = new Regex(source.EventPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { return false; }
    }
}
