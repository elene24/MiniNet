using BenchmarkDotNet.Attributes;
using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Hosting;
using MiniNet.Http;
using MiniNet.Logging;
using MiniNet.Middleware;

namespace MiniNet.Benchmarks;

/// <summary>
/// The whole framework minus the socket: routing, parameter binding, DI scope, handler, JSON.
/// Shows how much time MiniNet itself adds to a request.
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    public sealed record Doctor(int Id, string Name);

    public sealed class DoctorStore
    {
        public Doctor Get(int id) => new(id, "Dr. Nino Beridze");
    }

    private MiniApp _app = null!;
    private HttpRequest _request = null!;
    private HttpRequest _jsonRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        var builder = MiniApp.CreateBuilder();
        builder.Services.AddSingleton<ILogger>(new InMemoryLogger());
        builder.Services.AddSingleton<DoctorStore>();
        _app = builder.Build();
        _app.UseExceptionHandling();
        _app.MapGet("/doctors/{id:int}", (int id, DoctorStore store) => store.Get(id));
        _app.MapGet("/plain", () => "ok");
        _app.BuildPipeline();

        _request = HttpRequest.Create("GET", "/plain");
        _jsonRequest = HttpRequest.Create("GET", "/doctors/42");
    }

    [Benchmark(Baseline = true)]
    public Task<HttpResponse> PlainTextEndpoint() => _app.SendAsync(_request);

    [Benchmark]
    public Task<HttpResponse> RouteValueServiceAndJson() => _app.SendAsync(_jsonRequest);
}
