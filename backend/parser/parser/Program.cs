using Bashkiria.Events.Parser.Parsing;
using Bashkiria.Events.Parser.Parsing.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IEventParser, GkzParser>();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
