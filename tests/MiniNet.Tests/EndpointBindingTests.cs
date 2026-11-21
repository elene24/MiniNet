using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Http;

namespace MiniNet.Tests;

public class EndpointBindingTests
{
    public interface IGreeter
    {
        string Greet(string name);
    }

    public sealed class Greeter : IGreeter
    {
        public string Greet(string name) => $"Hello, {name}!";
    }

    public sealed class CreateOrder
    {
        [Required, StringLength(50, MinimumLength = 2)]
        public string Customer { get; set; } = "";

        [Range(1, 100)]
        public int Quantity { get; set; }
    }

    public sealed record OrderCreated(int StoreId, string Customer, int Quantity, bool Express);

    public enum Size
    {
        Small,
        Large,
    }

    [Fact]
    public async Task Binds_route_query_body_and_services_by_convention()
    {
        await using var app = TestApp.Create(b => b.Services.AddSingleton<IGreeter, Greeter>());
        app.MapPost("/stores/{storeId:int}/orders", (int storeId, bool express, CreateOrder order, IGreeter greeter) =>
            Results.Created($"/stores/{storeId}/orders/1", new OrderCreated(storeId, greeter.Greet(order.Customer), order.Quantity, express)));

        var response = await app.PostJsonAsync("/stores/12/orders?express=true", """{"customer":"Elene","quantity":3}""");

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("/stores/12/orders/1", response.Headers["Location"]);
        var body = JsonSerializer.Deserialize<OrderCreated>(response.BodyText(), MiniJson.Options)!;
        Assert.Equal(new OrderCreated(12, "Hello, Elene!", 3, true), body);
    }

    [Fact]
    public async Task Optional_query_parameters_fall_back_to_their_default()
    {
        await using var app = TestApp.Create();
        app.MapGet("/search", (string q, int page = 1, Size? size = null) => $"{q}:{page}:{size?.ToString() ?? "any"}");

        Assert.Equal("shoes:1:any", (await app.GetAsync("/search?q=shoes")).BodyText());
        Assert.Equal("shoes:3:Large", (await app.GetAsync("/search?q=shoes&page=3&size=large")).BodyText());
    }

    [Fact]
    public async Task Missing_or_unparseable_values_produce_a_400_listing_every_problem()
    {
        await using var app = TestApp.Create();
        app.MapGet("/search", (string q, int page) => "ok");

        var response = await app.GetAsync("/search?page=two");

        Assert.Equal(400, response.StatusCode);
        var problem = JsonSerializer.Deserialize<ProblemDetails>(response.BodyText(), MiniJson.Options)!;
        Assert.Contains("required", problem.Errors!["q"][0]);
        Assert.Contains("valid integer", problem.Errors["page"][0]);
    }

    [Fact]
    public async Task Data_annotations_on_the_body_are_validated()
    {
        await using var app = TestApp.Create();
        app.MapPost("/orders", (CreateOrder order) => "created");

        var response = await app.PostJsonAsync("/orders", """{"customer":"E","quantity":500}""");

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.ContentType);
        var problem = JsonSerializer.Deserialize<ProblemDetails>(response.BodyText(), MiniJson.Options)!;
        Assert.True(problem.Errors!.ContainsKey("customer")); // camelCase, as the client sent it
        Assert.True(problem.Errors.ContainsKey("quantity"));
    }

    [Fact]
    public async Task Malformed_JSON_is_a_400_and_a_non_JSON_body_is_a_415()
    {
        await using var app = TestApp.Create();
        app.MapPost("/orders", (CreateOrder order) => "created");

        var malformed = await app.PostJsonAsync("/orders", "{\"customer\": ");
        var wrongType = await app.SendAsync(HttpRequest.Create("POST", "/orders", "customer=x", ("Content-Type", "application/x-www-form-urlencoded")));

        Assert.Equal(400, malformed.StatusCode);
        Assert.Contains("not valid JSON", malformed.BodyText());
        Assert.Equal(415, wrongType.StatusCode);
    }

    [Fact]
    public async Task Explicit_attributes_override_inference()
    {
        await using var app = TestApp.Create();
        app.MapGet("/whoami", ([FromHeader("X-User")] string user, [FromQuery("v")] int version) => $"{user}/v{version}");

        var response = await app.SendAsync(HttpRequest.Create("GET", "/whoami?v=2", null, ("X-User", "elene")));

        Assert.Equal("elene/v2", response.BodyText());
    }

    [Fact]
    public async Task Return_values_are_unwrapped_and_written_by_type()
    {
        await using var app = TestApp.Create();
        app.MapGet("/text", () => "plain");
        app.MapGet("/object", () => new { id = 1, name = "Nino" });
        app.MapGet("/task", async () =>
        {
            await Task.Yield();
            return new[] { 1, 2, 3 };
        });
        app.MapGet("/valuetask", () => ValueTask.FromResult(42));
        app.MapGet("/nocontent", () => Results.NoContent());

        var text = await app.GetAsync("/text");
        Assert.Equal("text/plain; charset=utf-8", text.ContentType);
        Assert.Equal("plain", text.BodyText());

        Assert.Equal("""{"id":1,"name":"Nino"}""", (await app.GetAsync("/object")).BodyText());
        Assert.Equal("[1,2,3]", (await app.GetAsync("/task")).BodyText());
        Assert.Equal("42", (await app.GetAsync("/valuetask")).BodyText());
        Assert.Equal(204, (await app.GetAsync("/nocontent")).StatusCode);
    }

    [Fact]
    public async Task A_handler_that_writes_the_response_itself_is_left_alone()
    {
        await using var app = TestApp.Create();
        app.MapGet("/manual", async (HttpContext context) =>
        {
            context.Response.StatusCode = 202;
            await context.Response.WriteAsync("accepted");
        });

        var response = await app.GetAsync("/manual");

        Assert.Equal(202, response.StatusCode);
        Assert.Equal("accepted", response.BodyText());
    }

    [Fact]
    public void Two_body_parameters_are_rejected_when_the_endpoint_is_mapped()
    {
        var app = TestApp.Create();

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.MapPost("/bad", (CreateOrder first, CreateOrder second) => "never"));

        Assert.Contains("more than one parameter bound from the request body", error.Message);
    }

    [Fact]
    public void An_unregistered_interface_parameter_is_rejected_when_mapped()
    {
        var app = TestApp.Create();

        var error = Assert.Throws<InvalidOperationException>(() => app.MapGet("/bad", (IGreeter greeter) => "never"));

        Assert.Contains("not a registered service", error.Message);
    }

    [Fact]
    public async Task Ambiguous_routes_fail_when_the_app_starts_not_on_a_request()
    {
        var app = TestApp.Create();
        app.MapGet("/users/{id}", (string id) => id);
        app.MapGet("/users/{name}", (string name) => name);

        await Assert.ThrowsAsync<InvalidOperationException>(app.StartAsync);
    }

    [Fact]
    public async Task Unknown_paths_get_404_and_wrong_methods_get_405_with_Allow()
    {
        await using var app = TestApp.Create();
        app.MapGet("/items/{id:int}", (int id) => id);
        app.MapDelete("/items/{id:int}", (int id) => Results.NoContent());

        var notFound = await app.GetAsync("/nothing-here");
        var notAllowed = await app.SendAsync(HttpRequest.Create("PUT", "/items/1"));

        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal(405, notAllowed.StatusCode);
        Assert.Equal("DELETE, GET, HEAD", notAllowed.Headers["Allow"]);
    }
}
