using AppointmentsApi;

var app = await AppointmentsApplication.CreateAsync(args);
await app.RunAsync();
