using System.Text.Json;
using AppointmentsApi;
using AppointmentsApi.Domain;
using MiniNet.DependencyInjection;
using MiniNet.Hosting;
using MiniNet.Http;
using MiniNet.Logging;

namespace AppointmentsApi.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Runs the real AppointmentsApi pipeline in-process, with the in-memory repository and a fixed clock.</summary>
public class AppointmentsApiTests : IAsyncLifetime
{
    // Monday 30 November 2026, 08:00 in Tbilisi. Tuesday 1 December is bookable.
    private static readonly DateTimeOffset Now = new(2026, 11, 30, 8, 0, 0, TimeSpan.FromHours(4));
    private const string Tuesday10 = "2026-12-01T10:00:00+04:00";

    private MiniApp _app = null!;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AppointmentsApplication.ConnectionStringVariable, null);
        _app = await AppointmentsApplication.CreateAsync([], builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(new ManualTimeProvider(Now));
            builder.Services.AddSingleton<ILogger>(new InMemoryLogger());
        });
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<HttpResponse> Book(int doctorId, string startsAt, string patient = "Elene Margalitadze") =>
        _app.SendAsync(HttpRequest.Create("POST", $"/doctors/{doctorId}/appointments",
            JsonSerializer.Serialize(new { patientName = patient, startsAt })));

    private static T Read<T>(HttpResponse response) =>
        JsonSerializer.Deserialize<T>(response.Body.ToArray(), MiniJson.Options)!;

    [Fact]
    public async Task Lists_and_filters_doctors()
    {
        var all = Read<List<Doctor>>(await _app.SendAsync(HttpRequest.Create("GET", "/doctors")));
        var cardiology = Read<List<Doctor>>(await _app.SendAsync(HttpRequest.Create("GET", "/doctors?specialty=Cardiology")));

        Assert.Equal(4, all.Count);
        Assert.All(cardiology, d => Assert.Equal("cardiology", d.Specialty));
        Assert.Equal(2, cardiology.Count);
    }

    [Fact]
    public async Task Booking_a_free_slot_returns_201_and_removes_it_from_free_slots()
    {
        var response = await Book(1, Tuesday10);

        Assert.Equal(201, response.StatusCode);
        var appointment = Read<Appointment>(response);
        Assert.Equal($"/appointments/{appointment.Id}", response.Headers["Location"]);

        var slots = await _app.SendAsync(HttpRequest.Create("GET", "/doctors/1/slots?date=2026-12-01"));
        Assert.DoesNotContain("10:00:00+04:00", System.Text.Encoding.UTF8.GetString(slots.Body.ToArray()));
        Assert.Contains("10:30:00+04:00", System.Text.Encoding.UTF8.GetString(slots.Body.ToArray()));
    }

    [Fact]
    public async Task The_same_slot_cannot_be_booked_twice_even_when_written_in_another_time_zone()
    {
        await Book(1, Tuesday10);

        var sameInstantInUtc = await Book(1, "2026-12-01T06:00:00Z", "Someone Else");

        Assert.Equal(409, sameInstantInUtc.StatusCode);
    }

    [Fact]
    public async Task The_same_slot_with_a_different_doctor_is_fine()
    {
        await Book(1, Tuesday10);

        Assert.Equal(201, (await Book(2, Tuesday10)).StatusCode);
    }

    [Fact]
    public async Task Fifty_simultaneous_bookings_for_one_slot_produce_exactly_one_appointment()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => Book(3, Tuesday10, $"Patient {i}"))));

        Assert.Single(responses, r => r.StatusCode == 201);
        Assert.Equal(49, responses.Count(r => r.StatusCode == 409));
    }

    [Theory]
    [InlineData("2026-11-30T07:00:00+04:00", "in the future")]           // already past
    [InlineData("2026-12-01T10:15:00+04:00", "every 30 minutes")]        // not on the grid
    [InlineData("2026-12-05T10:00:00+04:00", "closed on that day")]      // Saturday
    [InlineData("2026-12-01T18:00:00+04:00", "between 09:00 and 18:00")] // closing time
    [InlineData("2026-12-01T08:30:00+04:00", "between 09:00 and 18:00")] // before opening
    public async Task Invalid_slots_are_rejected_with_a_reason(string startsAt, string reason)
    {
        var response = await Book(1, startsAt);

        Assert.Equal(422, response.StatusCode);
        Assert.Contains(reason, System.Text.Encoding.UTF8.GetString(response.Body.ToArray()));
    }

    [Fact]
    public async Task Missing_fields_are_a_validation_error()
    {
        var response = await _app.SendAsync(HttpRequest.Create("POST", "/doctors/1/appointments", """{"patientName":"E"}"""));

        Assert.Equal(400, response.StatusCode);
        var problem = Read<MiniNet.Endpoints.ProblemDetails>(response);
        Assert.True(problem.Errors!.ContainsKey("patientName"));
        Assert.True(problem.Errors.ContainsKey("startsAt"));
    }

    [Fact]
    public async Task Unknown_doctors_are_404()
    {
        Assert.Equal(404, (await Book(99, Tuesday10)).StatusCode);
        Assert.Equal(404, (await _app.SendAsync(HttpRequest.Create("GET", "/doctors/99"))).StatusCode);
        Assert.Equal(404, (await _app.SendAsync(HttpRequest.Create("GET", "/doctors/99/slots?date=2026-12-01"))).StatusCode);
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_again()
    {
        var appointment = Read<Appointment>(await Book(1, Tuesday10));

        var cancelled = await _app.SendAsync(HttpRequest.Create("DELETE", $"/appointments/{appointment.Id}"));
        var cancelledAgain = await _app.SendAsync(HttpRequest.Create("DELETE", $"/appointments/{appointment.Id}"));

        Assert.Equal(204, cancelled.StatusCode);
        Assert.Equal(404, cancelledAgain.StatusCode);
        Assert.Equal(201, (await Book(1, Tuesday10, "Next Patient")).StatusCode);
    }

    [Fact]
    public async Task Clients_are_rate_limited_but_the_health_check_is_not()
    {
        // The test clock never moves, so the 60-token bucket does not refill.
        var statuses = new List<int>();
        for (var i = 0; i < 61; i++)
            statuses.Add((await _app.SendAsync(HttpRequest.Create("GET", "/doctors"))).StatusCode);

        Assert.Equal(60, statuses.Count(s => s == 200));
        Assert.Equal(429, statuses[^1]);
        Assert.Equal(200, (await _app.SendAsync(HttpRequest.Create("GET", "/health"))).StatusCode);
    }
}

public class ClinicScheduleTests
{
    private readonly ClinicSchedule _schedule = new();

    [Fact]
    public void A_working_day_has_eighteen_half_hour_slots()
    {
        var slots = _schedule.SlotsOn(new DateOnly(2026, 12, 1)).ToList();

        Assert.Equal(18, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.FromHours(4)), slots[0]);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 17, 30, 0, TimeSpan.FromHours(4)), slots[^1]);
    }

    [Fact]
    public void Weekends_have_no_slots()
    {
        Assert.Empty(_schedule.SlotsOn(new DateOnly(2026, 12, 6)));
    }
}
