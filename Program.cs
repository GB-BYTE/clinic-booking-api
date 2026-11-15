using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

if (File.Exists(".env")) foreach (var line in File.ReadAllLines(".env"))
    {
        var split = line.IndexOf('=');
        if (split > 0 && !line.StartsWith('#') && Environment.GetEnvironmentVariable(line[..split]) is null)
            Environment.SetEnvironmentVariable(line[..split], line[(split + 1)..]);
    }
var builder = WebApplication.CreateBuilder(args);
var uri = new Uri(builder.Configuration["DATABASE_URL"] ?? throw new Exception("DATABASE_URL is required"));
var apiKey = builder.Configuration["API_KEY"] ?? throw new Exception("API_KEY is required");
var credentials = uri.UserInfo.Split(':', 2);
var cs = new NpgsqlConnectionStringBuilder
{
    Host = uri.Host,
    Port = uri.IsDefaultPort ? 5432 : uri.Port,
    Database = uri.AbsolutePath.Trim('/'),
    Username = Uri.UnescapeDataString(credentials[0]),
    Password = Uri.UnescapeDataString(credentials[1]),
    SslMode = SslMode.VerifyFull,
    MaxPoolSize = 20,
    Timeout = 30
};
await using var db = NpgsqlDataSource.Create(cs.ConnectionString);
await using (var conn = await db.OpenConnectionAsync())
{
    await Db.Execute(conn, """
        CREATE TABLE IF NOT EXISTS slots(id text PRIMARY KEY, provider text NOT NULL, start_at timestamptz NOT NULL, end_at timestamptz NOT NULL);
        CREATE TABLE IF NOT EXISTS bookings(seq bigserial UNIQUE, id text PRIMARY KEY, slot_id text NOT NULL REFERENCES slots(id), name text NOT NULL, email text NOT NULL, status text NOT NULL CHECK(status IN ('confirmed','cancelled')), created_at timestamptz NOT NULL DEFAULT clock_timestamp());
        CREATE UNIQUE INDEX IF NOT EXISTS one_confirmed_booking_per_slot ON bookings(slot_id) WHERE status='confirmed';
        CREATE INDEX IF NOT EXISTS bookings_email_seq ON bookings(email,seq);
        CREATE TABLE IF NOT EXISTS idempotency(key text PRIMARY KEY, body text NOT NULL, code integer NOT NULL, response text NOT NULL);
        CREATE TABLE IF NOT EXISTS attempts(email text NOT NULL, attempted_at timestamptz NOT NULL DEFAULT clock_timestamp());
        CREATE INDEX IF NOT EXISTS attempts_email_time ON attempts(email,attempted_at);
        """);
}
var app = builder.Build();
if (Environment.GetEnvironmentVariable("PORT") is { } port) app.Urls.Add($"http://0.0.0.0:{port}");
app.Use(async (ctx, next) =>
{
    try
    {
        if (ctx.Request.Path.StartsWithSegments("/slots") || ctx.Request.Path.StartsWithSegments("/bookings") || ctx.Request.Path.StartsWithSegments("/reset"))
        {
            var given = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-API-Key"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(apiKey)))
            {
                await Db.Error(401, "invalid API key").ExecuteAsync(ctx); return;
            }
        }
        await next();
    }
    catch (JsonException) { await Db.Error(400, "invalid JSON body").ExecuteAsync(ctx); }
    catch (BadHttpRequestException) { await Db.Error(400, "invalid request").ExecuteAsync(ctx); }
    catch (Exception ex) { app.Logger.LogError("Request failed: {Type}", ex.GetType().Name); await Db.Error(500, "internal server error").ExecuteAsync(ctx); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/reset", async (HttpRequest request) =>
{
    var input = await request.ReadFromJsonAsync<ResetInput>();
    if (input?.slots is null || input.slots.Any(s => string.IsNullOrWhiteSpace(s.id) || string.IsNullOrWhiteSpace(s.provider) || s.start == default || s.end <= s.start) || input.slots.Select(s => s.id).Distinct().Count() != input.slots.Length)
        return Db.Error(400, "invalid slots");
    await using var conn = await db.OpenConnectionAsync(); await using var tx = await conn.BeginTransactionAsync(); await Db.Lock(conn);
    await Db.Execute(conn, "DELETE FROM idempotency; DELETE FROM attempts; DELETE FROM bookings; DELETE FROM slots;");
    foreach (var s in input.slots) await Db.Execute(conn, "INSERT INTO slots VALUES($1,$2,$3,$4)", s.id!, s.provider!, s.start.UtcDateTime, s.end.UtcDateTime);
    await tx.CommitAsync(); return Results.Ok(new { status = "ok" });
});
app.MapGet("/slots", async (HttpRequest request) =>
{
    await using var conn = await db.OpenConnectionAsync();
    var provider = request.Query["provider"].ToString();
    await using var cmd = Db.Command(conn, "SELECT s.id,s.provider,s.start_at,s.end_at,NOT EXISTS(SELECT 1 FROM bookings b WHERE b.slot_id=s.id AND b.status='confirmed') FROM slots s WHERE ($1='' OR provider=$1) ORDER BY start_at,id", provider);
    await using var reader = await cmd.ExecuteReaderAsync(); var slots = new List<object>();
    while (await reader.ReadAsync()) slots.Add(new { id = reader.GetString(0), provider = reader.GetString(1), start = reader.GetDateTime(2), end = reader.GetDateTime(3), available = reader.GetBoolean(4) });
    return Results.Ok(new { slots });
});
app.MapPost("/bookings", async (HttpContext context) =>
{
    var input = await context.Request.ReadFromJsonAsync<BookingInput>();
    var name = input?.name?.Trim(); var email = input?.email?.Trim().ToLowerInvariant();
    var key = context.Request.Headers["Idempotency-Key"].ToString(); var body = JsonSerializer.Serialize(input);
    await using var conn = await db.OpenConnectionAsync(); await using var tx = await conn.BeginTransactionAsync(); await Db.Lock(conn);
    // Database transaction lock makes retries and attempts consistent across instances.
    if (key.Length > 0)
    {
        await using var cmd = Db.Command(conn, "SELECT body,code,response FROM idempotency WHERE key=$1", key);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync()) return reader.GetString(0) == body ? Results.Content(reader.GetString(2), "application/json", statusCode: reader.GetInt32(1)) : Db.Error(422, "idempotency key reused with different body");
    }
    if (!string.IsNullOrEmpty(email))
    {
        await Db.Execute(conn, "DELETE FROM attempts WHERE attempted_at < clock_timestamp()-interval '1 minute'");
        await using var count = Db.Command(conn, "SELECT count(*),COALESCE(ceil(extract(epoch FROM min(attempted_at)+interval '1 minute'-clock_timestamp()))::int,1) FROM attempts WHERE email=$1", email);
        int retry; long attempts;
        await using (var reader = await count.ExecuteReaderAsync()) { await reader.ReadAsync(); attempts = reader.GetInt64(0); retry = Math.Max(1, reader.GetInt32(1)); }
        if (attempts >= 5) { await tx.CommitAsync(); context.Response.Headers.RetryAfter = retry.ToString(); return Db.Error(429, "too many booking attempts"); }
        await Db.Execute(conn, "INSERT INTO attempts(email) VALUES($1)", email);
    }
    int code; object response;
    if (string.IsNullOrWhiteSpace(input?.slot_id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('@'))
    {
        code = 400; response = new { error = "slot_id, name and valid email are required" };
    }
    else
    {
        var start = await Db.Scalar(conn, "SELECT start_at FROM slots WHERE id=$1", input.slot_id);
        if (start is null) { code = 404; response = new { error = "slot not found" }; }
        else if ((DateTime)start <= DateTime.UtcNow) { code = 400; response = new { error = "slot has already started" }; }
        else if ((bool)(await Db.Scalar(conn, "SELECT EXISTS(SELECT 1 FROM bookings WHERE slot_id=$1 AND status='confirmed')", input.slot_id))!) { code = 409; response = new { error = "slot already booked" }; }
        else
        {
            var id = Guid.NewGuid().ToString();
            await Db.Execute(conn, "INSERT INTO bookings(id,slot_id,name,email,status) VALUES($1,$2,$3,$4,'confirmed')", id, input.slot_id, name, email);
            response = (await Db.Booking(conn, id))!; code = 201;
        }
    }
    var json = JsonSerializer.Serialize(response);
    if (key.Length > 0) await Db.Execute(conn, "INSERT INTO idempotency VALUES($1,$2,$3,$4)", key, body, code, json);
    await tx.CommitAsync(); return Results.Content(json, "application/json", statusCode: code);
});
app.MapGet("/bookings/{id}", async (string id) =>
{
    await using var conn = await db.OpenConnectionAsync(); var booking = await Db.Booking(conn, id);
    return booking is null ? Db.Error(404, "booking not found") : Results.Ok(booking);
});
app.MapDelete("/bookings/{id}", async (string id) =>
{
    await using var conn = await db.OpenConnectionAsync(); await using var tx = await conn.BeginTransactionAsync(); await Db.Lock(conn);
    var booking = await Db.Booking(conn, id);
    if (booking is null) return Db.Error(404, "booking not found");
    if (booking.status == "cancelled") return Db.Error(409, "booking already cancelled");
    await Db.Execute(conn, "UPDATE bookings SET status='cancelled' WHERE id=$1", id);
    await tx.CommitAsync(); return Results.Ok(booking with { status = "cancelled" });
});
app.MapGet("/bookings", async (HttpRequest request) =>
{
    var email = request.Query["email"].ToString().Trim().ToLowerInvariant(); var limit = 10; long cursor = 0;
    if (string.IsNullOrWhiteSpace(email)) return Db.Error(400, "email is required");
    if (request.Query.ContainsKey("limit") && (!int.TryParse(request.Query["limit"], out limit) || limit < 1 || limit > 50)) return Db.Error(400, "limit must be between 1 and 50");
    if (request.Query.ContainsKey("cursor"))
    {
        try { var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Query["cursor"].ToString())); if (!long.TryParse(decoded, out cursor) || cursor < 1) return Db.Error(400, "invalid cursor"); }
        catch (FormatException) { return Db.Error(400, "invalid cursor"); }
    }
    await using var conn = await db.OpenConnectionAsync();
    await using var cmd = Db.Command(conn, "SELECT id,slot_id,name,email,status,created_at,seq FROM bookings WHERE email=$1 AND seq>$2 ORDER BY seq LIMIT $3", email, cursor, limit + 1);
    await using var reader = await cmd.ExecuteReaderAsync(); var bookings = new List<BookingRecord>(); long last = 0; bool more = false;
    while (await reader.ReadAsync()) { if (bookings.Count == limit) { more = true; break; } bookings.Add(Db.ReadBooking(reader)); last = reader.GetInt64(6); }
    return Results.Ok(new { bookings, next_cursor = more ? Convert.ToBase64String(Encoding.UTF8.GetBytes(last.ToString())) : null });
});
app.Run();

record SlotInput(string? id, string? provider, DateTimeOffset start, DateTimeOffset end);
record ResetInput(SlotInput[]? slots);
record BookingInput(string? slot_id, string? name, string? email);
record BookingRecord(string id, string slot_id, string name, string email, string status, DateTime created_at);
static class Db
{
    public static IResult Error(int code, string message) => Results.Json(new { error = message }, statusCode: code);
    public static NpgsqlCommand Command(NpgsqlConnection conn, string sql, params object[] values) { var cmd = new NpgsqlCommand(sql, conn); foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value }); return cmd; }
    public static async Task Execute(NpgsqlConnection conn, string sql, params object[] values) { await using var cmd = Command(conn, sql, values); await cmd.ExecuteNonQueryAsync(); }
    public static async Task<object?> Scalar(NpgsqlConnection conn, string sql, params object[] values) { await using var cmd = Command(conn, sql, values); return await cmd.ExecuteScalarAsync(); }
    public static Task Lock(NpgsqlConnection conn) => Execute(conn, "SELECT pg_advisory_xact_lock(824603117)");
    public static BookingRecord ReadBooking(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDateTime(5));
    public static async Task<BookingRecord?> Booking(NpgsqlConnection conn, string id) { await using var cmd = Command(conn, "SELECT id,slot_id,name,email,status,created_at FROM bookings WHERE id=$1", id); await using var r = await cmd.ExecuteReaderAsync(); return await r.ReadAsync() ? ReadBooking(r) : null; }
}
