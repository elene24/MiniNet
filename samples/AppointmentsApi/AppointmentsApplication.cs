using AppointmentsApi.Data;
using AppointmentsApi.Domain;
using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Hosting;
using MiniNet.Logging;
using MiniNet.Middleware;
using MiniNet.Routing;

namespace AppointmentsApi;

/// <summary>
/// A small clinic-booking API built only on MiniNet. Separate from Program.cs so tests can create the
/// same app with test settings (port 0, a fake clock) and call it in-process or over TCP.
/// </summary>
public static class AppointmentsApplication
{
    public const string ConnectionStringVariable = "APPOINTMENTS_CONNECTION_STRING";

    public static async Task<MiniApp> CreateAsync(string[] args, Action<MiniAppBuilder>? configure = null)
    {
        var builder = MiniApp.CreateBuilder(args);
        builder.Services.AddSingleton(new ClinicSchedule());
        builder.Services.AddScoped<AppointmentService>();

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddSingleton<IAppointmentRepository, InMemoryAppointmentRepository>();
        }
        else
        {
            var sql = new SqlAppointmentRepository(SqlAppointmentRepository.CreateConnectionFactory(connectionString));
            await sql.EnsureSchemaAsync(CancellationToken.None);
            builder.Services.AddSingleton<IAppointmentRepository>(sql);
        }

        configure?.Invoke(builder); // tests override services here; the last registration wins
        var app = builder.Build();

        app.Logger.Info(string.IsNullOrWhiteSpace(connectionString)
            ? "Storage: in-memory (set APPOINTMENTS_CONNECTION_STRING to use Azure SQL)"
            : "Storage: Azure SQL");

        // ---- Pipeline. Order matters: see README "Middleware order". ----
        app.UseExceptionHandling(o => o.IncludeDetails = app.IsDevelopment);
        app.UseRequestLogging();
        app.UseRouting();
        app.UseRateLimiting(o =>
        {
            o.Capacity = 60;
            o.TokensPerSecond = 5;
            o.TimeProvider = app.Services.GetRequiredService<TimeProvider>();
        });

        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(MiniApp app)
    {
        app.MapGet("/health", () => new { status = "ok" }).DisableRateLimiting();

        app.MapGet("/doctors", (string? specialty, IAppointmentRepository repository, CancellationToken ct) =>
            repository.GetDoctorsAsync(specialty, ct));

        app.MapGet("/doctors/{id:int}", async (int id, IAppointmentRepository repository, CancellationToken ct) =>
            await repository.GetDoctorAsync(id, ct) is { } doctor
                ? Results.Ok(doctor)
                : Results.NotFound(new { message = $"Doctor {id} not found." }));

        app.MapGet("/doctors/{doctorId:int}/slots", async (int doctorId, DateOnly date, AppointmentService service, CancellationToken ct) =>
            await service.GetFreeSlotsAsync(doctorId, date, ct) is { } slots
                ? Results.Ok(new { doctorId, date, slots })
                : Results.NotFound(new { message = $"Doctor {doctorId} not found." }));

        app.MapGet("/doctors/{doctorId:int}/appointments",
            async (int doctorId, DateOnly date, IAppointmentRepository repository, ClinicSchedule schedule, CancellationToken ct) =>
            {
                var (from, to) = schedule.DayRange(date);
                return await repository.GetAppointmentsAsync(doctorId, from, to, ct);
            });

        app.MapPost("/doctors/{doctorId:int}/appointments",
            async (int doctorId, BookAppointmentRequest request, AppointmentService service, CancellationToken ct) =>
            {
                var result = await service.BookAsync(doctorId, request, ct);
                return result.Status switch
                {
                    BookingStatus.Booked => Results.Created($"/appointments/{result.Appointment!.Id}", result.Appointment),
                    BookingStatus.DoctorNotFound => Results.NotFound(new { message = $"Doctor {doctorId} not found." }),
                    BookingStatus.SlotTaken => Results.Problem(409, "Slot already booked.", result.Reason),
                    _ => Results.Problem(422, "Slot not available.", result.Reason),
                };
            });

        app.MapGet("/appointments/{id:guid}", async (Guid id, IAppointmentRepository repository, CancellationToken ct) =>
            await repository.GetAppointmentAsync(id, ct) is { } appointment
                ? Results.Ok(appointment)
                : Results.NotFound());

        app.MapDelete("/appointments/{id:guid}", async (Guid id, IAppointmentRepository repository, CancellationToken ct) =>
            await repository.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
