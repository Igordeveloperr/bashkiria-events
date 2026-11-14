using Bashkiria.Events.Parser.Collection;
using Bashkiria.Events.Parser.Contracts;
using Bashkiria.Events.Parser.Parsing;
using Microsoft.OpenApi;

namespace Bashkiria.Events.Parser.Api;

public static class ParserOpenApi
{
    public static IServiceCollection AddParserOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            // Keep the service contract independent of Development-only preview and health endpoints.
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/parser/", StringComparison.Ordinal) == true;
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // Publish canonical JSON numbers; accepting numeric strings is a server parsing tolerance.
                if (schema.Type is { } type && (type & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0)
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null;
                }
                if (schema.Enum is { Count: > 0 } && (Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type).IsEnum)
                    schema.Type = (schema.Type ?? 0) | JsonSchemaType.String;

                // All properties of these response-only objects are serialized, including null/default values.
                if (schema.Properties is not null && (context.JsonTypeInfo.Type == typeof(EventResponse) ||
                    context.JsonTypeInfo.Type == typeof(EventCandidate) || context.JsonTypeInfo.Type == typeof(CollectionResult)))
                    schema.Required = schema.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Bashkiria Events Parser API",
                    Version = "1.0.0",
                    Description = "HTTP-контракт сервиса парсера. JSON: camelCase; enum: строки camelCase. " +
                        "Локальные даты и время мероприятий: Asia/Yekaterinburg; временные метки: ISO 8601 с UTC-смещением. " +
                        "null означает отсутствие данных; неизвестная цена не равна бесплатному входу. " +
                        "Общий лимит: 60 запросов за фиксированное минутное окно на экземпляр, без очереди. " +
                        "В Development без настроенного ключа проверка X-Parser-Key отключена."
                };
                // Consumers configure the address of their environment rather than an export-time host.
                document.Servers = [];
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ParserApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Parser-Key"
                };
                document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
                document.Components.Schemas["ParserProblemDetails"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalPropertiesAllowed = true,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["type"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                        ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["instance"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String }
                    }
                };

                foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations!.Values))
                {
                    operation.Security = [new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("ParserApiKey", document)] = []
                    }];
                    operation.Responses ??= new OpenApiResponses();
                    AddProblemResponse("400", "Некорректные параметры, JSON или данные. Тело может отсутствовать; прямые проверки могут возвращать application/json с message.");
                    AddProblemResponse("401", "Отсутствует или неверен X-Parser-Key.");
                    AddProblemResponse("500", "Необработанная ошибка сервиса.");
                    AddProblemResponse("503", "База данных недоступна или схема не обновлена.");
                    if (operation.Responses.ContainsKey("404")) AddProblemResponse("404", "Запись или источник не найдены. Тело может отсутствовать.");
                    if (operation.Responses.ContainsKey("409")) AddProblemResponse("409", "Конфликт версии, уникальности или состояния задания. Прямая отмена возвращает application/json с message.");
                    operation.Responses["429"] = new OpenApiResponse { Description = "Превышен общий лимит API. Пустое тело." };

                    if (operation.OperationId == "StartCollectionRun" && operation.Responses["202"] is OpenApiResponse accepted)
                        accepted.Headers = new Dictionary<string, IOpenApiHeader>
                        {
                            ["Location"] = new OpenApiHeader
                            {
                                Description = "Относительный путь для получения состояния задания.",
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                            }
                        };

                    void AddProblemResponse(string status, string description)
                    {
                        var response = operation.Responses.TryGetValue(status, out var existing) && existing is OpenApiResponse inline
                            ? inline : new OpenApiResponse();
                        operation.Responses[status] = response;
                        response.Description = description;
                        response.Content ??= new Dictionary<string, OpenApiMediaType>();
                        response.Content["application/problem+json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchemaReference("ParserProblemDetails", document)
                        };
                    }
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
