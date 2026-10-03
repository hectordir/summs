using Summs.Server;

var builder = WebApplication.CreateBuilder(args);

// Railway (and similar hosts) say which port to listen on through PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 64 * 1024);
builder.Services.AddSingleton<RoomRegistry>();
builder.Services.AddHostedService<RoomCleanup>();

var app = builder.Build();

app.MapGet("/", (RoomRegistry rooms) => $"Summs: {rooms.RoomCount} salas");
app.MapHub<RoomHub>("/sync");

app.Run();
