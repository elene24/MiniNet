using MiniNet.Routing;

namespace MiniNet.Tests;

public class RouterTests
{
    private static Endpoint E(string method, string template) =>
        new(method, RouteTemplate.Parse(template), _ => Task.CompletedTask);

    private static Router RouterWith(params Endpoint[] endpoints) => new(endpoints);

    [Fact]
    public void Matches_literal_routes_case_insensitively()
    {
        var router = RouterWith(E("GET", "/health"));

        Assert.NotNull(router.Match("/HEALTH", "GET").Endpoint);
        Assert.Null(router.Match("/healthz", "GET").Endpoint);
    }

    [Fact]
    public void Captures_route_parameters()
    {
        var router = RouterWith(E("GET", "/users/{userId}/orders/{orderId}"));

        var match = router.Match("/users/42/orders/abc", "GET");

        Assert.Equal("42", match.Values["userId"]);
        Assert.Equal("abc", match.Values["orderId"]);
    }

    [Fact]
    public void Constraints_reject_values_that_do_not_fit()
    {
        var router = RouterWith(E("GET", "/orders/{id:int}"));

        Assert.NotNull(router.Match("/orders/12", "GET").Endpoint);
        Assert.Null(router.Match("/orders/twelve", "GET").Endpoint);
        Assert.False(router.Match("/orders/twelve", "GET").PathMatched); // 404, not 405
    }

    [Fact]
    public void A_literal_segment_wins_over_a_parameter()
    {
        var me = E("GET", "/users/me");
        var byId = E("GET", "/users/{id}");
        var router = RouterWith(byId, me);

        Assert.Same(me, router.Match("/users/me", "GET").Endpoint);
        Assert.Same(byId, router.Match("/users/7", "GET").Endpoint);
    }

    [Fact]
    public void A_constrained_parameter_wins_over_an_unconstrained_one()
    {
        var byId = E("GET", "/items/{id:int}");
        var bySlug = E("GET", "/items/{slug}");
        var router = RouterWith(bySlug, byId);

        Assert.Same(byId, router.Match("/items/5", "GET").Endpoint);
        Assert.Same(bySlug, router.Match("/items/blue-shirt", "GET").Endpoint);
    }

    [Fact]
    public void Backtracks_when_a_more_specific_branch_fails_further_down()
    {
        // "/a/b/c": the literal "b" branch only knows "/a/b/d", so the matcher must back up and try "{x}".
        var literal = E("GET", "/a/b/d");
        var parameter = E("GET", "/a/{x}/c");
        var router = RouterWith(literal, parameter);

        var match = router.Match("/a/b/c", "GET");

        Assert.Same(parameter, match.Endpoint);
        Assert.Equal("b", match.Values["x"]);
    }

    [Fact]
    public void An_optional_last_parameter_matches_with_and_without_a_value()
    {
        var router = RouterWith(E("GET", "/reports/{year:int?}"));

        Assert.NotNull(router.Match("/reports", "GET").Endpoint);
        var withYear = router.Match("/reports/2026", "GET");
        Assert.Equal("2026", withYear.Values["year"]);
        Assert.False(router.Match("/reports", "GET").Values.ContainsKey("year"));
    }

    [Fact]
    public void A_catch_all_takes_the_rest_of_the_path()
    {
        var router = RouterWith(E("GET", "/files/{*path}"));

        Assert.Equal("docs/2026/report.pdf", router.Match("/files/docs/2026/report.pdf", "GET").Values["path"]);
        Assert.Equal("", router.Match("/files", "GET").Values["path"]);
    }

    [Fact]
    public void Wrong_method_on_an_existing_path_reports_the_allowed_methods()
    {
        var router = RouterWith(E("GET", "/orders/{id}"), E("DELETE", "/orders/{id}"));

        var match = router.Match("/orders/1", "PUT");

        Assert.Null(match.Endpoint);
        Assert.True(match.PathMatched);
        Assert.Equal(new[] { "DELETE", "GET", "HEAD" }, match.AllowedMethods);
    }

    [Fact]
    public void A_method_match_on_a_less_specific_route_beats_a_405_on_a_more_specific_one()
    {
        var getMe = E("GET", "/users/me");
        var deleteById = E("DELETE", "/users/{id}");
        var router = RouterWith(getMe, deleteById);

        Assert.Same(deleteById, router.Match("/users/me", "DELETE").Endpoint);
    }

    [Fact]
    public void HEAD_is_served_by_the_GET_endpoint()
    {
        var get = E("GET", "/health");

        Assert.Same(get, RouterWith(get).Match("/health", "HEAD").Endpoint);
    }

    [Fact]
    public void Routes_that_match_the_same_requests_are_rejected_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RouterWith(E("GET", "/users/{id}"), E("GET", "/users/{name}")));

        Assert.Contains("Ambiguous routes", error.Message);
    }

    [Fact]
    public void Same_template_with_different_methods_is_fine()
    {
        var router = RouterWith(E("GET", "/users/{id}"), E("PUT", "/users/{id}"));

        Assert.Equal("PUT", router.Match("/users/1", "PUT").Endpoint!.HttpMethod);
    }

    [Theory]
    [InlineData("users")]               // must start with '/'
    [InlineData("/a/{*rest}/b")]        // catch-all must be last
    [InlineData("/a/{x?}/b")]           // only the last parameter can be optional
    [InlineData("/a/{x}/{x}")]          // duplicate name
    [InlineData("/a/{x:nonsense}")]     // unknown constraint
    [InlineData("/a/file.{ext}")]       // parameter must be a whole segment
    [InlineData("/a/{}")]               // empty name
    public void Invalid_templates_are_rejected(string template)
    {
        Assert.Throws<FormatException>(() => RouteTemplate.Parse(template));
    }
}
