// ASP.NET Core minimal API with the same endpoints as MiniNetHello, used as the baseline in the load test.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "8081"}");
var app = builder.Build();

app.MapGet("/hello/{name}", (string name) => $"Hello, {name}!");
app.MapGet("/json", () => new { message = "Hello, World!", time = DateTimeOffset.UtcNow });

app.Run();
