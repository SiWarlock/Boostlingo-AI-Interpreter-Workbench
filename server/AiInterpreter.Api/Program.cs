// Minimal buildable host (A.1 scaffold). Real host wiring — DI, camelCase JSON, CORS,
// WebSocket support, listen port, and GET /api/health — lands in A.5 (ARCH-029).
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();
