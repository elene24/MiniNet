using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Hosting;
using MiniNet.Logging;

// The same two endpoints as KestrelHello, so the load test compares the frameworks and nothing else.
var builder = MiniApp.CreateBuilder(args);
builder.Services.AddSingleton<ILogger>(new ConsoleLogger(LogLevel.Warning)); // no per-request logging
var app = builder.Build();

app.MapGet("/hello/{name}", (string name) => $"Hello, {name}!");
app.MapGet("/json", () => new { message = "Hello, World!", time = DateTimeOffset.UtcNow });

await app.RunAsync();
