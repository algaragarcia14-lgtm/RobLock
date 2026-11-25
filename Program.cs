using System.IO.Ports;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting;
using QRCoder;
using Npgsql;
using NpgsqlTypes;

// ---------------------------------------------------------------
//  RobLock back end (C#)
//  - one admin account (password is set the first time you open /admin.html)
//  - admin generates a random 5-digit session id for a device, shown as a QR code
//  - whoever holds an active session id gets a Lock/Unlock page; unlocking after the grace period ends that session
//  - data lives in the private roblock PostgreSQL schema
//  The front end is plain HTML in wwwroot/ and Private/.
// ---------------------------------------------------------------

// Offline synthetic transition checks (no Supabase connection or serial access).
if (args.Contains("--check-security-events", StringComparer.Ordinal))
{
    Environment.ExitCode = Db.CheckSecurityEventLogging() ? 0 : 1;
    return;
}

// Optional database diagnostics, without starting the website or Arduino.
var checkWrite = args.Contains("--check-supabase-write", StringComparer.Ordinal);
var checkSupabase = checkWrite || args.Contains("--check-supabase", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(
    args.Where(arg => arg != "--check-supabase" && arg != "--check-supabase-write").ToArray());

if (checkSupabase)
{
    var connectionString = builder.Configuration.GetConnectionString("Supabase");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("FAIL: ConnectionStrings:Supabase is missing. Check User Secrets and the Development environment.");
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        var settings = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (settings.SslMode != Npgsql.SslMode.VerifyFull)
        {
            Console.Error.WriteLine("FAIL: Configure SSL Mode=VerifyFull before testing.");
            Environment.ExitCode = 1;
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.RootCertificate) || !File.Exists(settings.RootCertificate))
        {
            Console.Error.WriteLine("FAIL: Root Certificate file is missing or inaccessible. Check its exact path and extension.");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine("PASS: Root Certificate file exists (trust not yet verified).");
        settings.Timeout = 15;
        settings.CommandTimeout = 15;
        settings.Pooling = false;
        await using var connection = new Npgsql.NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using (var role = new Npgsql.NpgsqlCommand("SELECT current_user", connection))
        {
            var databaseRole = (string?)await role.ExecuteScalarAsync();
            if (databaseRole != "roblock_app")
            {
                Console.Error.WriteLine("FAIL: Connected, but not as the restricted roblock_app role.");
                Environment.ExitCode = 1;
                return;
            }
        }
        Console.WriteLine("PASS: Supabase login and verified TLS connection.");
        Console.WriteLine("PASS: Restricted roblock_app role.");
        await using var transaction = await connection.BeginTransactionAsync();
        if (!checkWrite)
        {
            await using var readOnly = new Npgsql.NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync();
        }
        var tables = new[] { "admin_accounts", "devices", "sessions", "user_information", "security_events", "used_session_ids" };
        foreach (var table in tables)
        {
            // Identifiers are from the fixed allowlist above, not user input.
            await using var command = new Npgsql.NpgsqlCommand($"SELECT COUNT(*) FROM roblock.{table}", connection, transaction);
            var count = (long)(await command.ExecuteScalarAsync())!;
            Console.WriteLine($"PASS: {table}: {count} row(s)");
        }
        if (checkWrite)
        {
            await using var update = new NpgsqlCommand(
                "UPDATE roblock.devices SET name = name WHERE id = 'rack-a1'", connection, transaction);
            if (await update.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("Expected one visible rack-a1 record.");
            Console.WriteLine("PASS: Device UPDATE executed under roblock_app and RLS.");
        }
        await transaction.RollbackAsync();
        Console.WriteLine(checkWrite ? "SUCCESS: Write test rolled back. No records were changed."
            : "SUCCESS: All six tables are readable. No records were changed.");
    }
    catch (Npgsql.PostgresException ex)
    {
        // Do not print connection strings, passwords or database record contents.
        Console.Error.WriteLine($"FAIL: PostgreSQL SQLSTATE {ex.SqlState}.");
        Environment.ExitCode = 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAIL: Connection check failed. Diagnostic exception types:");
        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
        {
            Console.Error.WriteLine($"  {cause.GetType().FullName}");
            if (cause is System.Net.Sockets.SocketException socket)
                Console.Error.WriteLine($"  Socket error: {socket.SocketErrorCode}");
            if (cause is System.Security.Authentication.AuthenticationException ||
                cause is System.Security.Cryptography.CryptographicException)
                Console.Error.WriteLine("  TLS/certificate error. Verify the Supabase CA file and Windows date/time.");
            if (cause is TimeoutException)
                Console.Error.WriteLine("  Connection or query timed out.");
            if (cause is Npgsql.PostgresException pg)
                Console.Error.WriteLine($"  PostgreSQL SQLSTATE: {pg.SqlState}");
        }
        Environment.ExitCode = 1;
    }
    return; // Never start the website or Arduino serial service in check mode.
}

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "roblock_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        // API calls should get a 401, not a redirect to a login page
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<ArduinoSerial>();
builder.Services.AddSingleton<Limiter>();
builder.Services.AddHostedService<SecurityEventMonitor>();

var app = builder.Build();
// Validate credentials and schema before serving requests or opening a serial port.
try
{
    app.Services.GetRequiredService<Db>().ValidateStartup();
}
catch (PostgresException ex)
{
    Console.Error.WriteLine("SUPABASE DATABASE ERROR");
    Console.Error.WriteLine($"SQLSTATE: {ex.SqlState}");
    Console.Error.WriteLine($"MESSAGE: {ex.MessageText}");
    Console.Error.WriteLine($"TABLE: {ex.TableName}");
    Console.Error.WriteLine($"COLUMN: {ex.ColumnName}");
    Console.Error.WriteLine($"CONSTRAINT: {ex.ConstraintName}");
 
    Environment.ExitCode = 1;
    return;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SUPABASE ERROR: {ex.GetType().Name}");
    Console.Error.WriteLine(ex.Message);
 
    Environment.ExitCode = 1;
    return;
}
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DatabaseUnavailableException)
    {
        if (ctx.Response.HasStarted) throw;
        ctx.Response.StatusCode = 503;
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsJsonAsync(new { error = "Database unavailable or save not confirmed. Stop device operations and contact the administrator." });
    }
});

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/dashboard"))
        ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseDefaultFiles();   // "/" serves wwwroot/index.html
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ---- admin dashboard page (only served to a logged-in admin) ----

app.MapGet("/dashboard", (HttpContext ctx, IWebHostEnvironment env) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Redirect("/admin.html");
    return Results.File(Path.Combine(env.ContentRootPath, "Private", "dashboard.html"), "text/html; charset=utf-8");
});

app.MapGet("/admin", () => Results.Redirect("/admin.html"));

var api = app.MapGroup("/api");

// ---- admin account: setup, login, logout ----

api.MapGet("/admin/status", (Db db) => Results.Ok(new { configured = db.AdminConfigured() }));

api.MapPost("/admin/setup", async (AdminAuthRequest req, HttpContext ctx, Db db) =>
{
    var username = (req.Username ?? "").Trim();
    var password = req.Password ?? "";
    if (username.Length < 3 || username.Length > 32)
        return Results.Json(new { error = "Username needs to be 3 to 32 characters." }, statusCode: 400);
    if (!username.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
        return Results.Json(new { error = "Username can only use letters, numbers, dots, dashes and underscores." }, statusCode: 400);
    if (password.Length < 8)
        return Results.Json(new { error = "Password needs to be at least 8 characters." }, statusCode: 400);
    if (!db.TrySetupAdmin(username, password))
        return Results.Json(new { error = "The admin account is already set up." }, statusCode: 409);
    await Auth.SignInAdmin(ctx, username);
    return Results.Ok(new { ok = true });
});

api.MapPost("/admin/login", async (AdminAuthRequest req, HttpContext ctx, Db db, Limiter limiter) =>
{
    var key = "login:" + Auth.Ip(ctx);
    if (limiter.Blocked(key)) return Auth.TooMany();
    if (!db.AdminConfigured())
        return Results.Json(new { error = "The admin account isn't set up yet." }, statusCode: 409);
    var username = (req.Username ?? "").Trim();
    if (!db.VerifyAdmin(username, req.Password ?? ""))
    {
        limiter.Fail(key);
        return Results.Json(new { error = "Incorrect username or password." }, statusCode: 401);
    }
    limiter.Reset(key);
    await Auth.SignInAdmin(ctx, db.AdminUsername());
    return Results.Ok(new { ok = true });
});

api.MapPost("/admin/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

api.MapGet("/admin/me", (HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
        ? Results.Ok(new { admin = true, username = ctx.User.Identity?.Name ?? "admin" })
        : Results.Json(new { admin = false }, statusCode: 401));

// ---- admin-only API ----

var admin = api.MapGroup("/admin").RequireAuthorization();

admin.MapGet("/dashboard", (Db db, ArduinoSerial serial) =>
{
    var physicalId = Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID");
    if (!string.IsNullOrWhiteSpace(physicalId)) db.DeviceStatus(physicalId, serial);
    return Results.Ok(db.GetDashboard());
});

admin.MapGet("/history", (Db db) => Results.Ok(db.GetSessionHistory()));

admin.MapGet("/devices/{id}/status", (string id, Db db, ArduinoSerial serial) =>
    db.DeviceStatus(id, serial));

admin.MapPost("/sessions", (CreateSessionRequest req, Db db, ArduinoSerial serial) =>
{
    var (view, error, status) = db.CreateSession(req.DeviceId ?? "", serial);
    return view is null
        ? Results.Json(new { error }, statusCode: status)
        : Results.Ok(view);
});

admin.MapPost("/sessions/{id}/end", (string id, HttpContext ctx, Db db, ArduinoSerial serial) =>
    db.EndSession(id, serial, ctx.User.Identity?.Name ?? "admin"));

admin.MapGet("/sessions/{id}/qr.svg", (string id, HttpContext ctx, Db db) =>
{
    if (db.GetSessionView(id) is null) return Results.NotFound();
    var url = $"{ctx.Request.Scheme}://{ctx.Request.Host}/control.html?s={id}";
    return Results.Content(Qr.Svg(url), "image/svg+xml");
});

admin.MapPost("/devices/{id}/{action}", (string id, string action, HttpContext ctx, Db db, ArduinoSerial serial) =>
    db.DeviceAction(id, action, serial, ctx.User.Identity?.Name ?? "admin"));

// ---- user API (anyone holding a valid 5-digit session id) ----

api.MapGet("/session/{id}", (string id, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial) =>
    Handlers.UserLookup(id, ctx, db, limiter, serial));

api.MapGet("/session/{id}/status", (string id, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial) =>
    Handlers.UserLookup(id, ctx, db, limiter, serial));

api.MapPost("/session/{id}/join", (string id, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.UserJoin(id, ctx, db, limiter));

api.MapPost("/session/{id}/info", (string id, UserInfoRequest req, HttpContext ctx, Db db, Limiter limiter) =>
    Handlers.SaveInfo(id, req, ctx, db, limiter));

api.MapPost("/session/{id}/lock",(string id, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial) =>
    Handlers.UserCommand(id, "lock", ctx, db, limiter, serial));

api.MapPost("/session/{id}/unlock", (string id, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial) =>
    Handlers.UserCommand(id, "unlock", ctx, db, limiter, serial));

app.Run();

// ===============================================================
//  Request and response shapes
// ===============================================================

record AdminAuthRequest(string? Username, string? Password);
record CreateSessionRequest(string? DeviceId);
record UserInfoRequest(string? Name, int? Age, string? Address, string? Phone);

record SessionView(
    string Id, string DeviceId, string DeviceName, bool Locked, bool Buzzer,
    string Status, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? TimeIn, DateTime? TimeOut, string? EndReason, UserInfo? User)
{
    public bool StateKnown { get; init; } = true;
    public bool Physical { get; init; }
    public DateTime? GraceUntil { get; init; }
    public long GraceRemainingMs { get; init; }
}

record DeviceView(string Id, string Name, bool Locked, bool Buzzer, SessionView? ActiveSession)
{
    public bool StateKnown { get; init; } = true;
    public bool Physical { get; init; }
    public bool BuzzerKnown { get; init; }
    public string? BuzzerMode { get; init; }
    public string? ArmState { get; init; }
    public string StateMeaning => Physical ? "Arduino commanded state (no position feedback)" : "Simulated state";
}

record EventView(DateTime Time, string Kind, string Title, string Detail, string DeviceName, string? SessionId);

record SessionHistoryView(SessionView Session, List<EventView> Events);

record DashboardData(List<DeviceView> Devices, List<SessionView> Sessions, List<EventView> Events, int SessionMinutes);

// ===============================================================
//  Stored data
// ===============================================================

class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Locked { get; set; } = true;
    public bool Buzzer { get; set; }
    public bool BuzzerKnown { get; set; }
    public string? BuzzerMode { get; set; }
    public string? ArmState { get; set; }
    public int? ReportedGraceMs { get; set; }
    public bool StateKnown { get; set; } = true; // for physical device: only a reported commanded state
}

class LockSession
{
    public string Id { get; set; } = "";            // the 5-digit session id
    public string DeviceId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ClaimedAt { get; set; }        // "time in": first time the user opened it
    public DateTime? EndedAt { get; set; }          // "time out"
    public DateTime? GraceUntil { get; set; }       // server-authoritative, persisted across reloads
    public string? EndReason { get; set; }
    public UserInfo? User { get; set; }             // details the user typed on the control page
}

class UserInfo
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
}

class LogEvent
{
    public DateTime Time { get; set; }
    public string Kind { get; set; } = "info";      // ok | warn | info
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? SessionId { get; set; }
}

class AdminRecord
{
    public string Username { get; set; } = "";      // empty on old password-only accounts, which forces a new setup
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
}

class Store
{
    public List<Device> Devices { get; set; } = new();
    public List<LockSession> Sessions { get; set; } = new();
    // Retain issued QR IDs separately so new sessions never revive old links.
    public HashSet<string> UsedSessionIds { get; set; } = new();
    public List<LogEvent> Events { get; set; } = new();
    public AdminRecord? Admin { get; set; }

    [JsonIgnore]
    public bool Dirty { get; set; }
}

// ===============================================================
//  Supabase PostgreSQL database
// ===============================================================

class DatabaseUnavailableException : Exception { }

class Db
{
    public const int GraceSeconds = 15;
    public const int SessionMinutes = 120;   // a session stops working after this long

    private readonly string _connectionString;
    private readonly string? _physicalId = Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID");
    private readonly object _gate = new();
    private bool _writeFault;

    public Db(IConfiguration config)
    {
        var raw = config.GetConnectionString("Supabase");
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Supabase connection string is required.");
        var settings = new NpgsqlConnectionStringBuilder(raw);
        if (settings.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(settings.RootCertificate) || !File.Exists(settings.RootCertificate))
            throw new InvalidOperationException("A verified TLS connection and existing root certificate are required.");
        settings.Timeout = 15;
        settings.CommandTimeout = 15;
        settings.IncludeErrorDetail = false;
        settings.PersistSecurityInfo = false;
        _connectionString = settings.ConnectionString;
    }

    public void ValidateStartup()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using (var role = new NpgsqlCommand("SELECT current_user", connection))
            if (!Equals(role.ExecuteScalar(), "roblock_app"))
                throw new InvalidOperationException("Use the restricted RobLock database account.");
        using var tx = connection.BeginTransaction();
        var st = Load(connection, tx);
        if (st.Devices.Count == 0) throw new InvalidOperationException("Import the registered devices first.");
        tx.Rollback();
        Console.WriteLine("RobLock storage: Supabase PostgreSQL (no JSON fallback).");
    }

    private static string Text(NpgsqlDataReader r, int i, string fallback = "") => r.IsDBNull(i) ? fallback : r.GetString(i);
    private static string? NullableText(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime? NullableDate(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
    private static bool Bool(NpgsqlDataReader r, int i, bool fallback = false) => r.IsDBNull(i) ? fallback : r.GetBoolean(i);

    private static Store Load(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        var st = new Store();
        // All six tables are read in one round trip under the operation transaction.
        using var cmd = new NpgsqlCommand("""
            SELECT username,salt,password_hash FROM roblock.admin_accounts WHERE id=1;
            SELECT id,name,locked,buzzer,buzzer_known,buzzer_mode,arm_state,reported_grace_ms,state_known FROM roblock.devices ORDER BY id;
            SELECT s.id,s.device_id,s.created_at,s.expires_at,s.claimed_at,s.ended_at,s.grace_until,s.end_reason,
                   u.session_id,u.name,u.age,u.phone,u.address
            FROM roblock.sessions s LEFT JOIN roblock.user_information u ON u.session_id=s.id ORDER BY s.created_at,s.id;
            SELECT event_time,kind,title,detail,device_id,device_name,session_id FROM roblock.security_events ORDER BY id;
            SELECT session_id FROM roblock.used_session_ids;
            """, c, tx);
        using var r = cmd.ExecuteReader();
        if (r.Read()) st.Admin = new AdminRecord { Username=Text(r,0), Salt=Text(r,1), Hash=Text(r,2) };
        r.NextResult();
        while (r.Read()) st.Devices.Add(new Device {
            Id=Text(r,0), Name=Text(r,1), Locked=Bool(r,2,true), Buzzer=Bool(r,3), BuzzerKnown=Bool(r,4),
            BuzzerMode=NullableText(r,5), ArmState=NullableText(r,6), ReportedGraceMs=r.IsDBNull(7)?null:r.GetInt32(7), StateKnown=Bool(r,8)
        });
        r.NextResult();
        while (r.Read()) st.Sessions.Add(new LockSession {
            Id=Text(r,0), DeviceId=Text(r,1), CreatedAt=r.GetDateTime(2), ExpiresAt=r.GetDateTime(3),
            ClaimedAt=NullableDate(r,4), EndedAt=NullableDate(r,5), GraceUntil=NullableDate(r,6), EndReason=NullableText(r,7),
            User=r.IsDBNull(8)?null:new UserInfo { Name=Text(r,9), Age=r.IsDBNull(10)?0:r.GetInt32(10), Phone=Text(r,11), Address=Text(r,12) }
        });
        r.NextResult();
        while (r.Read()) st.Events.Add(new LogEvent {
            Time=r.GetDateTime(0), Kind=Text(r,1,"info"), Title=Text(r,2), Detail=Text(r,3),
            DeviceId=Text(r,4), DeviceName=Text(r,5), SessionId=NullableText(r,6)
        });
        r.NextResult();
        while (r.Read()) st.UsedSessionIds.Add(Text(r,0));
        return st;
    }

    // Snapshots are only in memory for change detection; no JSON files are read or written.
    private static string Snapshot<T>(T value) => JsonSerializer.Serialize(value);
    private static void Exec(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params (string Name, NpgsqlDbType Type, object? Value)[] args)
    {
        using var cmd = new NpgsqlCommand(sql,c,tx);
        foreach (var a in args) cmd.Parameters.AddWithValue(a.Name,a.Type,a.Value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void SaveChanges(NpgsqlConnection c, NpgsqlTransaction tx, Store st, string oldAdmin,
        Dictionary<string,string> oldDevices, Dictionary<string,string> oldSessions, HashSet<string> oldCodes, int oldEvents)
    {
        if (st.Admin is {} admin && Snapshot(admin) != oldAdmin)
            Exec(c,tx,"""
                INSERT INTO roblock.admin_accounts(id,username,salt,password_hash) VALUES(1,@username,@salt,@hash)
                ON CONFLICT(id) DO UPDATE SET username=EXCLUDED.username,salt=EXCLUDED.salt,password_hash=EXCLUDED.password_hash
                """, ("username",NpgsqlDbType.Text,admin.Username),("salt",NpgsqlDbType.Text,admin.Salt),("hash",NpgsqlDbType.Text,admin.Hash));
        foreach (var d in st.Devices)
        {
            if (oldDevices.TryGetValue(d.Id,out var previous) && previous == Snapshot(d)) continue;
            Exec(c,tx,"""
                INSERT INTO roblock.devices(id,name,locked,buzzer,buzzer_known,buzzer_mode,arm_state,reported_grace_ms,state_known)
                VALUES(@id,@name,@locked,@buzzer,@known,@mode,@arm,@grace,@state)
                ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name,locked=EXCLUDED.locked,buzzer=EXCLUDED.buzzer,
                buzzer_known=EXCLUDED.buzzer_known,buzzer_mode=EXCLUDED.buzzer_mode,arm_state=EXCLUDED.arm_state,
                reported_grace_ms=EXCLUDED.reported_grace_ms,state_known=EXCLUDED.state_known
                """, ("id",NpgsqlDbType.Text,d.Id),("name",NpgsqlDbType.Text,d.Name),("locked",NpgsqlDbType.Boolean,d.Locked),
                ("buzzer",NpgsqlDbType.Boolean,d.Buzzer),("known",NpgsqlDbType.Boolean,d.BuzzerKnown),("mode",NpgsqlDbType.Text,d.BuzzerMode),
                ("arm",NpgsqlDbType.Text,d.ArmState),("grace",NpgsqlDbType.Integer,d.ReportedGraceMs),("state",NpgsqlDbType.Boolean,d.StateKnown));
        }
        foreach (var s in st.Sessions)
        {
            if (oldSessions.TryGetValue(s.Id,out var previous) && previous == Snapshot(s)) continue;
            Exec(c,tx,"""
                INSERT INTO roblock.sessions(id,device_id,created_at,expires_at,claimed_at,ended_at,grace_until,end_reason)
                VALUES(@id,@device,@created,@expires,@claimed,@ended,@grace,@reason)
                ON CONFLICT(id) DO UPDATE SET claimed_at=EXCLUDED.claimed_at,ended_at=EXCLUDED.ended_at,
                grace_until=EXCLUDED.grace_until,end_reason=EXCLUDED.end_reason
                """, ("id",NpgsqlDbType.Text,s.Id),("device",NpgsqlDbType.Text,s.DeviceId),("created",NpgsqlDbType.TimestampTz,s.CreatedAt),
                ("expires",NpgsqlDbType.TimestampTz,s.ExpiresAt),("claimed",NpgsqlDbType.TimestampTz,s.ClaimedAt),
                ("ended",NpgsqlDbType.TimestampTz,s.EndedAt),("grace",NpgsqlDbType.TimestampTz,s.GraceUntil),("reason",NpgsqlDbType.Text,s.EndReason));
            if (s.User is {} u)
                Exec(c,tx,"""
                    INSERT INTO roblock.user_information(session_id,name,age,phone,address) VALUES(@id,@name,@age,@phone,@address)
                    ON CONFLICT(session_id) DO UPDATE SET name=EXCLUDED.name,age=EXCLUDED.age,phone=EXCLUDED.phone,address=EXCLUDED.address
                    """, ("id",NpgsqlDbType.Text,s.Id),("name",NpgsqlDbType.Text,u.Name),("age",NpgsqlDbType.Integer,u.Age),
                    ("phone",NpgsqlDbType.Text,u.Phone),("address",NpgsqlDbType.Text,u.Address));
        }
        foreach (var code in st.UsedSessionIds.Where(x=>!oldCodes.Contains(x)))
            Exec(c,tx,"INSERT INTO roblock.used_session_ids(session_id,issued_at) VALUES(@id,@at) ON CONFLICT(session_id) DO NOTHING",
                ("id",NpgsqlDbType.Text,code),("at",NpgsqlDbType.TimestampTz,st.Sessions.FirstOrDefault(s=>s.Id==code)?.CreatedAt));
        // The cloud history is append-only; previously imported rows are never rewritten.
        foreach (var e in st.Events.Skip(oldEvents))
            Exec(c,tx,"""
                INSERT INTO roblock.security_events(session_id,device_id,device_name,event_time,kind,title,detail)
                VALUES(@session,@device,@name,@time,@kind,@title,@detail)
                """, ("session",NpgsqlDbType.Text,e.SessionId),("device",NpgsqlDbType.Text,e.DeviceId),("name",NpgsqlDbType.Text,e.DeviceName),
                ("time",NpgsqlDbType.TimestampTz,e.Time),("kind",NpgsqlDbType.Text,e.Kind),("title",NpgsqlDbType.Text,e.Title),("detail",NpgsqlDbType.Text,e.Detail));
    }

    // The original server-side authorization/session logic runs unchanged inside a SQL transaction.
    private T Run<T>(Func<Store,T> work, bool mutates = false)
    {
        lock (_gate)
        {
            if (_writeFault) throw new DatabaseUnavailableException();
            bool workStarted = false;
            try
            {
                using var c = new NpgsqlConnection(_connectionString);
                c.Open();
                using var tx = c.BeginTransaction();
                // Serialize operations between cooperating RobLock database clients.
                using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(1867634021)",c,tx)) gate.ExecuteNonQuery();
                var st = Load(c,tx);
                var oldAdmin=Snapshot(st.Admin);
                var oldDevices=st.Devices.ToDictionary(d=>d.Id,d=>Snapshot(d));
                var oldSessions=st.Sessions.ToDictionary(s=>s.Id,s=>Snapshot(s));
                var oldCodes=new HashSet<string>(st.UsedSessionIds);
                var oldEvents=st.Events.Count;
                workStarted=true;
                Sweep(st);
                var result=work(st);
                if (mutates || st.Dirty) SaveChanges(c,tx,st,oldAdmin,oldDevices,oldSessions,oldCodes,oldEvents);
                tx.Commit();
                return result;
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.IO.IOException)
            {
                // A hardware command may already have completed. Do not silently retry or fall back to old JSON.
                if (workStarted) _writeFault=true;
                Console.Error.WriteLine("RobLock database operation failed: " + ex.GetType().Name + (ex is PostgresException pg ? " SQLSTATE " + pg.SqlState : ""));
                if (_writeFault) Console.Error.WriteLine("Device operations blocked until operator reconciliation and server restart.");
                throw new DatabaseUnavailableException();
            }
        }
    }

    private static void Sweep(Store st)
    {
        var now = DateTime.UtcNow;
        foreach (var s in st.Sessions.Where(x => x.EndedAt is null && now >= x.ExpiresAt).ToList())
        {
            s.GraceUntil = null;
            s.EndedAt = s.ExpiresAt;
            s.EndReason = "expired";
            Log(st, "info", "Session expired", "Time ran out", DeviceOf(st, s), s.Id, s.ExpiresAt);
        }
    }

    private static Device? DeviceOf(Store st, LockSession s) =>
        st.Devices.FirstOrDefault(d => d.Id == s.DeviceId);

    private static void Log(Store st, string kind, string title, string detail, Device? dev, string? sessionId, DateTime? at = null)
    {
        st.Events.Add(new LogEvent
        {
            Time = at ?? DateTime.UtcNow,
            Kind = kind,
            Title = title,
            Detail = detail,
            DeviceId = dev?.Id ?? "",
            DeviceName = dev?.Name ?? "",
            SessionId = sessionId,
        });
        st.Dirty = true;
    }

    private static LockSession? Active(Store st, string id)
    {
        if (id.Length != 5 || !id.All(c => c >= '0' && c <= '9')) return null;
        return st.Sessions.FirstOrDefault(s => s.Id == id && s.EndedAt is null);
    }

    private static SessionView View(Store st, LockSession s)
    {
        var dev = DeviceOf(st, s);
        var status = s.EndedAt is not null ? "ended" : s.ClaimedAt is null ? "waiting" : "active";
        var grace = s.EndedAt is null && dev is { Locked: true, StateKnown: true } ? s.GraceUntil : null;
        return new SessionView(
            s.Id, s.DeviceId, dev?.Name ?? "Unknown device", dev?.Locked ?? true, dev?.Buzzer ?? false,
            status, s.CreatedAt, s.ExpiresAt, s.ClaimedAt, s.EndedAt, s.EndReason, s.User)
        { StateKnown = dev?.StateKnown ?? false,
          Physical = dev is not null && dev.Id == Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID"),
          GraceUntil = grace,
          GraceRemainingMs = grace is DateTime until ? Math.Max(0L, (long)Math.Ceiling((until - DateTime.UtcNow).TotalMilliseconds)) : 0L };
    }

    private static DeviceView DeviceViewOf(Store st, Device d)
    {
        var active = st.Sessions.FirstOrDefault(s => s.DeviceId == d.Id && s.EndedAt is null);
        return new DeviceView(d.Id, d.Name, d.Locked, d.Buzzer, active is null ? null : View(st, active))
        { StateKnown = d.StateKnown, Physical = d.Id == Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID"),
          BuzzerKnown = d.BuzzerKnown, BuzzerMode = d.BuzzerMode, ArmState = d.ArmState };
    }

    // ---- admin account ----

    // An old account with no username counts as "not set up", so the setup screen shows again.
    private static bool HasAdmin(Store st) =>
        st.Admin is not null && !string.IsNullOrWhiteSpace(st.Admin.Username);

    public bool AdminConfigured() => Run(st => HasAdmin(st));

    public string AdminUsername() => Run(st => st.Admin?.Username ?? "admin");

    public bool TrySetupAdmin(string username, string password) => Run(st =>
    {
        if (HasAdmin(st)) return false;
        var (salt, hash) = Auth.HashPassword(password);
        st.Admin = new AdminRecord { Username = username, Salt = salt, Hash = hash };
        st.Dirty = true;
        return true;
    });

    public bool VerifyAdmin(string username, string password) => Run(st =>
    {
        if (!HasAdmin(st)) return false;
        // always check the password too, so a wrong username takes the same time as a wrong password
        var userOk = string.Equals(st.Admin!.Username, username, StringComparison.OrdinalIgnoreCase);
        var passOk = Auth.Verify(st.Admin, password);
        return userOk && passOk;
    });

    // ---- dashboard ----

    public DashboardData GetDashboard() => Run(st =>
    {
        var devices = st.Devices.Select(d => DeviceViewOf(st, d)).ToList();
        var sessions = st.Sessions
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .Select(s => View(st, s))
            .ToList();
        var events = st.Events
            .OrderByDescending(e => e.Time)
            .Take(60)
            .Select(e => new EventView(e.Time, e.Kind, e.Title, e.Detail, e.DeviceName, e.SessionId))
            .ToList();
        return new DashboardData(devices, sessions, events, SessionMinutes);
    });

    // All retained sessions, with only the events associated with each session.
    // Match device and creation time too, because a trimmed session ID can be reused.
    public List<SessionHistoryView> GetSessionHistory() => Run(st =>
    {
        var eventsBySession = st.Events.ToLookup(e => e.SessionId);
        return st.Sessions.OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionHistoryView(View(st, s), eventsBySession[s.Id]
                .Where(e => e.DeviceId == s.DeviceId && e.Time >= s.CreatedAt)
                .OrderByDescending(e => e.Time)
                .Select(e => new EventView(e.Time, e.Kind, e.Title, e.Detail, e.DeviceName, e.SessionId))
                .ToList()))
            .ToList();
    });

    // ---- sessions (admin side) ----

    public (SessionView? View, string? Error, int Status) CreateSession(string deviceId, ArduinoSerial serial) =>
        Run<(SessionView?, string?, int)>(st =>
        {
            var dev = st.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (dev is null) return (null, "Pick a device first.", 404);
            if (st.Sessions.Any(s => s.DeviceId == dev.Id && s.EndedAt is null))
                return (null, dev.Name + " already has an active session. End it first.", 409);

            // Migrate IDs still available in older databases, including retained event IDs.
            st.UsedSessionIds.UnionWith(st.Sessions.Select(s => s.Id));
            st.UsedSessionIds.UnionWith(st.Events.Where(e => e.SessionId is not null).Select(e => e.SessionId!));
            // Select from the remaining five-digit IDs without an unbounded retry loop.
            var available = Enumerable.Range(10000, 90000).Select(n => n.ToString())
                .Where(code => !st.UsedSessionIds.Contains(code)).ToArray();
            if (available.Length == 0)
                return (null, "All one-time session codes have been used. A new code format is required.", 409);
            var id = available[RandomNumberGenerator.GetInt32(available.Length)];
            st.UsedSessionIds.Add(id);

            var now = DateTime.UtcNow;
            var session = new LockSession
            {
                Id = id,
                DeviceId = dev.Id,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(SessionMinutes),
            };
            if (dev.Id == _physicalId)
            {
                var pin = SyncSessionPin(serial, session);
                if (!pin.Ok)
                {
                    Log(st, "warn", "Keypad PIN not confirmed", "Arduino could not confirm session provisioning", dev, null);
                    return (null, pin.Error, pin.Status);
                }
                ApplyReportedState(st, dev, pin);
            }
            st.Sessions.Add(session);
            Log(st, "info", "Session created", "QR code generated", dev, id);
            return (View(st, session), null, 200);
        }, true);

    // Keep the unlock and session closure under the same database gate.
    public IResult EndSession(string id, ArduinoSerial serial, string adminName) => Run<IResult>(st =>
    {
        var s = Active(st, id);
        var dev = s is null ? null : DeviceOf(st, s);
        if (s is null || dev is null) return Error("Session not found or already ended.", 404);

        // Verify the current commanded state rather than relying on the cached dashboard.
        // Do not move an already-unlocked mechanism unnecessarily.
        if (dev.Id == _physicalId)
        {
            var status = serial.Send("STATUS");
            if (!status.Ok)
            {
                MarkStateUnknown(dev);
                Log(st, "warn", "Session end not confirmed", $"By {adminName}; {status.Error}", dev, s.Id);
                return Error(status.Error, status.Status);
            }
            ApplyReportedState(st, dev, status);
        }
        if (!dev.StateKnown || dev.Locked || (dev.Id == _physicalId && dev.ArmState != "DISARMED"))
        {
            var error = SessionLockCommand(st, s, dev, "unlock", serial, $"By {adminName} (admin)");
            if (error is not null) return error;
            LogSessionCommand(st, s, dev, "unlock", $"By {adminName} (admin)");
        }
        else
        {
            Log(st, "info", "Already unlocked", dev.Id == _physicalId
                ? $"By {adminName}; Arduino commanded state confirmed (position not verified)"
                : $"By {adminName}; simulated state", dev, s.Id);
        }
        var pinError = RevokeSessionPin(st, s, dev, serial);
        if (pinError is not null) return pinError;
        CompleteSession(st, s, dev, "ended by admin", $"Ended by {adminName} (admin)");
        return Results.Ok(new { ok = true });
    }, true);

    private static void CompleteSession(Store st, LockSession s, Device dev, string reason, string detail)
    {
        s.GraceUntil = null;
        s.EndedAt = DateTime.UtcNow;
        s.EndReason = reason;
        Log(st, "info", "Session ended", detail, dev, s.Id);
    }

    private static void MarkStateUnknown(Device dev)
    {
        dev.StateKnown = false;
        dev.BuzzerKnown = false;
        dev.BuzzerMode = null;
        dev.ArmState = null;
    }

    public SessionView? GetSessionView(string id) => Run<SessionView?>(st =>
    {
        var s = Active(st, id);
        return s is null ? null : View(st, s);
    });

    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

    // Read the Arduino's last *commanded* state; never claim a physical position measurement.
    // History records logical alarm/test transitions, not the buzzer's individual pulses.
    // Timestamps are server observation times, not a hardware real-time clock.
    private static void RecordObservedSecurityEvents(Store st, Device dev, ArduinoResult response, string? command)
    {
        if (!response.Ok || response.ArmState is not ("DISARMED" or "GRACE" or "ARMED" or "ALARM" or "SILENCED") ||
            !response.BuzzerOn.HasValue || response.BuzzerMode is not ("IDLE" or "TEST" or "ALARM")) return;
        var arm = response.ArmState;
        var mode = response.BuzzerMode;
        if ((arm == "ALARM") != (mode == "ALARM")) return; // inconsistent snapshot
        var previousArm = dev.ArmState;
        var previousMode = dev.BuzzerKnown ? dev.BuzzerMode : null;
        var now = DateTime.UtcNow;
        var sessionId = st.Sessions.FirstOrDefault(s => s.DeviceId == dev.Id && s.EndedAt is null && s.ExpiresAt > now)?.Id;
        void Event(string kind, string title, string detail) => Log(st, kind, title, detail, dev, sessionId, now);

        if (previousMode == "TEST" && mode != "TEST" && command != "BUZZER-OFF")
            Event("info", "Buzzer test ended automatically", "Arduino no longer reports TEST mode; exact stop cause not reported");
        if (previousArm == "SILENCED" && arm == "ARMED")
            Event("ok", "Alarm rearm completed", "Arduino reports ARMED after alarm recovery");
        else if (previousArm == "SILENCED" && arm == "ALARM")
            Event("warn", "Alarm rearm ended with renewed alarm", "Arduino reports a new alarm after recovery");

        if (arm == "ALARM" && previousArm != "ALARM")
        {
            // An already-active alarm after a monitoring gap is not proof of a new incident.
            if (previousArm is null || previousMode is null)
                Event("warn", "Tamper alarm observed", "Arduino reports an active tamper alarm; start time unknown");
            else
            {
                Event("warn", "Tampering detected", "Arduino entered ALARM state after its local tamper detection");
                Event("warn", "Tamper alarm buzzer on", "Arduino reports alarm mode; buzzer may pulse on and off");
            }
        }
        else if (previousArm == "ALARM" && arm != "ALARM" && command != "ACK-ALARM")
        {
            Event("ok", "Tamper alarm buzzer off", "Arduino no longer reports ALARM mode; exact stop time unknown");
            if (arm == "SILENCED")
                Event("info", "Tamper alarm acknowledged locally", "Arduino reports recovery; responsible person not verified");
            else
                Event("info", "Alarm state changed", "Arduino reports " + arm + "; acknowledgment and recovery may have occurred between observations");
        }
        else if (arm == "SILENCED" && previousArm != "SILENCED" && command != "ACK-ALARM")
            Event("info", "Alarm recovery observed", "Arduino reports SILENCED; preceding alarm or acknowledgment was not observed");

        if (mode == "TEST" && previousMode != "TEST" && command != "BUZZER-ON")
            Event("info", "Buzzer test observed", "Arduino reports TEST mode; start time and actor not verified");
    }

    private static void ApplyReportedState(Store st, Device dev, ArduinoResult response, string? command = null)
    {
        RecordObservedSecurityEvents(st, dev, response, command);
        dev.StateKnown = response.LockState is "LOCKED" or "UNLOCKED";
        if (dev.StateKnown) dev.Locked = response.LockState == "LOCKED";
        dev.BuzzerKnown = response.BuzzerOn.HasValue && response.BuzzerMode is ("IDLE" or "TEST" or "ALARM");
        if (dev.BuzzerKnown) dev.Buzzer = response.BuzzerOn.GetValueOrDefault();
        dev.BuzzerMode = dev.BuzzerKnown ? response.BuzzerMode : null;
        dev.ArmState = response.ArmState;
        dev.ReportedGraceMs = response.GraceMs;
    }

    // Runs the real transition recorder against synthetic reports only.
    public static bool CheckSecurityEventLogging()
    {
        var st = new Store();
        var dev = new Device { Id = "test-rack", Name = "Test rack" };
        st.Devices.Add(dev);
        var now = DateTime.UtcNow;
        st.Sessions.Add(new LockSession { Id = "test-session", DeviceId = dev.Id,
            CreatedAt = now.AddMinutes(-1), ExpiresAt = now.AddMinutes(1) });
        bool passed = true;
        void Check(string name, bool ok) { passed &= ok; Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name); }
        void Report(string arm, string mode, bool buzzer, string? command = null) =>
            ApplyReportedState(st, dev, new ArduinoResult(true, "", 200, "LOCKED", arm, buzzer, mode), command);
        int Count(string title) => st.Events.Count(e => e.Title == title);

        Report("GRACE", "IDLE", false);
        Report("GRACE", "IDLE", false);
        Report("ARMED", "IDLE", false);
        Check("Grace and normal arming do not generate false tamper events", st.Events.Count == 0);
        Report("ALARM", "ALARM", true);
        Report("ALARM", "ALARM", false);
        Report("ALARM", "ALARM", true);
        Check("Tamper detection and alarm start recorded once despite pulsing", Count("Tampering detected") == 1 && Count("Tamper alarm buzzer on") == 1);
        Check("Device and active session association", st.Events.All(e => e.DeviceId == dev.Id && e.SessionId == "test-session"));
        Report("SILENCED", "IDLE", false);
        Check("Observed local acknowledgment and alarm off", Count("Tamper alarm buzzer off") == 1 && Count("Tamper alarm acknowledged locally") == 1);
        Report("ARMED", "IDLE", false);
        Check("Rearm completion recorded", Count("Alarm rearm completed") == 1);
        Report("ARMED", "TEST", true, "BUZZER-ON");
        Check("Explicit buzzer start is not duplicated", Count("Buzzer test observed") == 0);
        Report("ARMED", "IDLE", false);
        Check("Automatic test stop recorded", Count("Buzzer test ended automatically") == 1);
        Report("ARMED", "TEST", true, "BUZZER-ON");
        Report("ARMED", "IDLE", false, "BUZZER-OFF");
        Check("Explicit buzzer stop is not duplicated", Count("Buzzer test ended automatically") == 1);
        Report("ALARM", "ALARM", false);
        Report("SILENCED", "IDLE", false, "ACK-ALARM");
        Check("Explicit admin acknowledgment is not duplicated", Count("Tamper alarm buzzer off") == 1);
        Report("ALARM", "ALARM", true);
        Check("Renewed alarm recorded", Count("Alarm rearm ended with renewed alarm") == 1 && Count("Tampering detected") == 3);
        var countBeforeDisconnect = st.Events.Count;
        MarkStateUnknown(dev);
        Check("Disconnect is not reported as alarm off", st.Events.Count == countBeforeDisconnect);
        Report("ALARM", "ALARM", true);
        Check("Alarm after monitoring gap is labeled observed", Count("Tamper alarm observed") == 1);
        st.Sessions[0].EndedAt = DateTime.UtcNow;
        Report("ARMED", "IDLE", false);
        Report("ALARM", "ALARM", true);
        Check("No false session association without active session", st.Events.Last().SessionId is null);
        return passed;
    }

    // All PIN provisioning is derived from the server-owned active rack session.
    // The PIN itself is not included in status responses or security log details.
    private static ArduinoResult SyncSessionPin(ArduinoSerial serial, LockSession? session)
    {
        var seconds = session is null ? 0 : (int)Math.Clamp(
            Math.Floor((session.ExpiresAt - DateTime.UtcNow).TotalSeconds), 0, SessionMinutes * 60);
        var set = session is not null && session.EndedAt is null && seconds > 0;
        var result = set ? serial.Send("PIN-SET", $"{session!.Id} {seconds}") : serial.Send("PIN-CLEAR");
        if (result.Ok && result.GraceMs is null)
            return new(false, "Arduino does not report grace status. Upload the matching sketch.", 502);
        if (result.Ok && result.PinState != (set ? "ACTIVE" : "NONE"))
            return new(false, "Arduino did not confirm keypad PIN synchronization. Upload the matching sketch.", 502);
        return result;
    }

    private IResult? RevokeSessionPin(Store st, LockSession session, Device dev, ArduinoSerial serial)
    {
        if (dev.Id != _physicalId) return null;
        var pin = SyncSessionPin(serial, null);
        if (!pin.Ok)
        {
            Log(st, "warn", "Keypad PIN removal not confirmed", "Session remains open; contact admin to retry ending it", dev, session.Id);
            return Error("Unlock completed, but keypad PIN removal was not confirmed. Ask the admin to retry End session.", pin.Status);
        }
        ApplyReportedState(st, dev, pin);
        return null;
    }

    // Internal read-only hardware poll: no PIN, lock, buzzer or acknowledgment commands.
    // The normal database gate serializes this with dashboard/user requests.
    public bool ObserveSecurityStatus(ArduinoSerial serial) => Run(st =>
    {
        var dev = st.Devices.FirstOrDefault(d => d.Id == _physicalId);
        if (dev is null) return false;
        var response = serial.Send("STATUS");
        if (!response.Ok)
        {
            // Unknown is not OFF. Do not invent alarm-stop events on disconnect.
            if (dev.StateKnown || dev.BuzzerKnown || dev.ArmState is not null)
            {
                MarkStateUnknown(dev);
                st.Dirty = true;
            }
            return false;
        }
        ApplyReportedState(st, dev, response);
        st.Dirty = true;
        return true;
    });

    public IResult DeviceStatus(string id, ArduinoSerial serial) => Run<IResult>(st =>
    {
        var dev = st.Devices.FirstOrDefault(d => d.Id == id);
        if (dev is null) return Error("Device not found.", 404);
        if (id != _physicalId) return Results.Ok(DeviceViewOf(st, dev));
        var session = st.Sessions.FirstOrDefault(s => s.DeviceId == dev.Id && s.EndedAt is null);
        var result = SyncSessionPin(serial, session);
        if (!result.Ok)
        {
            dev.StateKnown = false;
            dev.BuzzerKnown = false;
            dev.BuzzerMode = null;
            dev.ArmState = null;
            st.Dirty = true;
            return Error(result.Error, result.Status);
        }
        ApplyReportedState(st, dev, result);
        if (session is not null && session.GraceUntil is DateTime deadline)
        {
            if (!dev.StateKnown || !dev.Locked || result.ArmState != "GRACE" || result.GraceMs is not int remaining || remaining <= 0)
                session.GraceUntil = null;
            else
            {
                var observed = DateTime.UtcNow.AddMilliseconds(remaining);
                if (observed < deadline) session.GraceUntil = observed; // status never extends grace
            }
        }
        st.Dirty = true;
        return Results.Ok(new { device = DeviceViewOf(st, dev), arm = result.ArmState, feedback = "NONE" });
    });

    public IResult DeviceAction(string id, string action, ArduinoSerial serial, string adminName) => Run<IResult>(st =>
    {
        var dev = st.Devices.FirstOrDefault(d => d.Id == id);
        if (dev is null || action is not ("lock" or "unlock" or "buzzer-on" or "buzzer-off" or "ack-alarm"))
            return Error("Unknown device or action.", 404);
        var active = st.Sessions.FirstOrDefault(s => s.DeviceId == dev.Id && s.EndedAt is null);
        if (action == "ack-alarm")
        {
            if (id != _physicalId) return Error("Alarm acknowledgment is only available for the physical Arduino.", 409);
            var observed = serial.Send("STATUS");
            if (observed.Ok) ApplyReportedState(st, dev, observed);
            var result = serial.Send("ACK-ALARM");
            if (!result.Ok)
            {
                dev.StateKnown = false;
                dev.BuzzerKnown = false;
                dev.BuzzerMode = null;
                dev.ArmState = null;
                Log(st, "warn", "Alarm acknowledgment not confirmed", $"By {adminName}; {result.Error}", dev, active?.Id);
                return Error(result.Error, result.Status);
            }
            ApplyReportedState(st, dev, result, "ACK-ALARM");
            if (!dev.StateKnown || !dev.Locked || !dev.BuzzerKnown || dev.Buzzer ||
                dev.BuzzerMode != "IDLE" || dev.ArmState != "SILENCED")
            {
                Log(st, "warn", "Alarm acknowledgment not confirmed", $"By {adminName}; unexpected Arduino state", dev, active?.Id);
                return Error("Arduino did not confirm a silenced alarm and unchanged locked command state.", 502);
            }
            Log(st, "ok", "Tamper alarm buzzer off", $"By {adminName}; Arduino confirmed alarm silenced", dev, active?.Id);
            Log(st, "ok", "Tamper alarm acknowledged", $"By {adminName}; Arduino confirmed; automatic 2-second rearm", dev, active?.Id);
            return Results.Ok(DeviceViewOf(st, dev));
        }
        if (id == _physicalId && action is ("buzzer-on" or "buzzer-off"))
        {
            var result = serial.Send(action.ToUpperInvariant());
            if (!result.Ok)
            {
                dev.BuzzerKnown = false;
                dev.BuzzerMode = null;
                dev.ArmState = null;
                st.Dirty = true;
                Log(st, "warn", "Arduino buzzer command not confirmed", result.Error, dev, active?.Id);
                return Error(result.Error, result.Status);
            }
            ApplyReportedState(st, dev, result, action.ToUpperInvariant());
            if (!dev.BuzzerKnown || dev.BuzzerMode != (action == "buzzer-on" ? "TEST" : "IDLE"))
            {
                st.Dirty = true;
                return Error("Arduino did not confirm the requested buzzer test state.", 502);
            }
            Log(st, "ok", action == "buzzer-on" ? "Buzzer test started" : "Buzzer test stopped",
                $"By {adminName}; Arduino confirmed", dev, active?.Id);
            return Results.Ok(DeviceViewOf(st, dev));
        }
        var wasLocked = dev.Locked;
        if (id == _physicalId && action is ("lock" or "unlock"))
        {
            var pin = SyncSessionPin(serial, active);
            if (!pin.Ok)
            {
                MarkStateUnknown(dev);
                Log(st, "warn", "Keypad PIN not confirmed", "Arduino synchronization failed", dev, active?.Id);
                return Error(pin.Error, pin.Status);
            }
            var result = serial.Send(action.ToUpperInvariant());
            if (!result.Ok)
            {
                dev.StateKnown = false;
                dev.BuzzerKnown = false;
                dev.BuzzerMode = null;
                dev.ArmState = null;
                st.Dirty = true;
                Log(st, "warn", "Arduino command not confirmed", result.Error, dev, active?.Id);
                return Error(result.Error, result.Status);
            }
            ApplyReportedState(st, dev, result);
            if (!dev.StateKnown || dev.Locked != (action == "lock"))
            {
                dev.StateKnown = false;
                st.Dirty = true;
                return Error("Arduino did not report the requested commanded lock state.", 502);
            }
        }
        else switch (action)
        {
            case "lock": dev.Locked = true; break;
            case "unlock": dev.Locked = false; break;
            case "buzzer-on": dev.Buzzer = true; break;
            case "buzzer-off": dev.Buzzer = false; break;
        }
        Log(st, action == "buzzer-on" ? "warn" : "ok",
            action switch { "lock" => "Locked", "unlock" => "Unlocked", "buzzer-on" => "Buzzer on", _ => "Buzzer off" },
            id == _physicalId ? "Arduino command completed (position not verified)" : "By admin (simulated)", dev, active?.Id);
        if (active is not null && action == "lock" && (id == _physicalId || !wasLocked))
            StartGrace(st, active, dev);
        else if (active is not null && action == "unlock") active.GraceUntil = null;
        return Results.Ok(DeviceViewOf(st, dev));
    }, true);

    // ---- sessions (user side) ----

    public SessionView? JoinSession(string id) => Run<SessionView?>(st =>
    {
        var s = Active(st, id);
        if (s is null) return null;
        if (s.ClaimedAt is null)
        {
            s.ClaimedAt = DateTime.UtcNow;
            Log(st, "ok", "Session started", "User opened the QR code", DeviceOf(st, s), s.Id);
        }
        return View(st, s);
    });

    public SessionView? SaveUserInfo(string id, UserInfo info) => Run<SessionView?>(st =>
    {
        var s = Active(st, id);
        var dev = s is null ? null : DeviceOf(st, s);
        if (s is null || dev is null) return null;

        if (s.ClaimedAt is null)
        {
            s.ClaimedAt = DateTime.UtcNow;
            Log(st, "ok", "Session started", "User opened the QR code", dev, s.Id);
        }

        var first = s.User is null;
        s.User = info;
        // the event text never contains the personal details; the admin sees them on the session
        Log(st, "info", first ? "User details saved" : "User details updated", "Typed on the control page", dev, s.Id);
        return View(st, s);
    }, true);

    public IResult UserCommand(string id, string action, ArduinoSerial serial) => Run<IResult>(st =>
    {
        if (action != "lock" && action != "unlock") return Error("Unknown action.", 404);
        var s = Active(st, id);
        var dev = s is null ? null : DeviceOf(st, s);
        if (s is null || dev is null) return Error("Session not found or ended.", 404);

        // Decide at the server before waiting for Arduino confirmation.
        // A failed command never consumes a session or extends its grace deadline.
        var requestedAt = DateTime.UtcNow;
        var wasLocked = dev.Locked;
        var withinGrace = action == "unlock" && s.GraceUntil is DateTime until && requestedAt < until;
        var error = SessionLockCommand(st, s, dev, action, serial, "By user");
        if (error is not null) return error;

        if (s.ClaimedAt is null)
        {
            s.ClaimedAt = DateTime.UtcNow;
            Log(st, "ok", "Session started", "User opened the QR code", dev, s.Id);
        }
        LogSessionCommand(st, s, dev, action, "By user");
        if (action == "lock" && (dev.Id == _physicalId || !wasLocked))
            StartGrace(st, s, dev);
        else if (action == "unlock")
        {
            s.GraceUntil = null;
            if (withinGrace)
                Log(st, "info", "Grace period unlock", "By user; session remains active", dev, s.Id);
            else
            {
                var pinError = RevokeSessionPin(st, s, dev, serial);
                if (pinError is not null) return pinError;
                CompleteSession(st, s, dev, "ended by user", "Ended by user after successful unlock");
            }
        }
        return Results.Ok(View(st, s));
    }, true);

    private void StartGrace(Store st, LockSession s, Device dev)
    {
        var remaining = dev.Id == _physicalId
            ? (dev.ArmState == "GRACE" ? dev.ReportedGraceMs.GetValueOrDefault() : 0)
            : GraceSeconds * 1000;
        if (remaining <= 0) { s.GraceUntil = null; return; }
        s.GraceUntil = DateTime.UtcNow.AddMilliseconds(Math.Min(remaining, GraceSeconds * 1000));
        Log(st, "info", "Grace period started", "15-second session-retention window after confirmed lock", dev, s.Id);
    }

    private IResult? SessionLockCommand(Store st, LockSession s, Device dev, string action,
        ArduinoSerial serial, string actor)
    {
        if (dev.Id == _physicalId)
        {
            var pin = SyncSessionPin(serial, s);
            if (!pin.Ok)
            {
                MarkStateUnknown(dev);
                Log(st, "warn", "Keypad PIN not confirmed", "Arduino synchronization failed", dev, s.Id);
                return Error(pin.Error, pin.Status);
            }
            var result = serial.Send(action.ToUpperInvariant());
            if (!result.Ok)
            {
                MarkStateUnknown(dev);
                Log(st, "warn", "Arduino command not confirmed", $"{actor}; {result.Error}", dev, s.Id);
                return Error(result.Error, result.Status);
            }
            ApplyReportedState(st, dev, result);
            if (!dev.StateKnown || dev.Locked != (action == "lock") ||
                (action == "unlock" && dev.ArmState != "DISARMED"))
            {
                dev.StateKnown = false;
                Log(st, "warn", "Arduino command not confirmed", $"{actor}; unexpected Arduino state", dev, s.Id);
                return Error("Arduino did not report the requested commanded lock state.", 502);
            }
        }
        else
        {
            dev.Locked = action == "lock";
            dev.StateKnown = true;
        }
        return null;
    }

    private void LogSessionCommand(Store st, LockSession s, Device dev, string action, string actor) =>
        Log(st, "ok", action == "lock" ? "Locked" : "Unlocked",
            actor + (dev.Id == _physicalId ? "; Arduino command completed (position not verified)" : " (simulated)"), dev, s.Id);

}

// ===============================================================
//  Helpers
// ===============================================================

// Runs even with all browser tabs closed. Firmware remains responsible for detection.
sealed class SecurityEventMonitor : BackgroundService
{
    private readonly Db _db;
    private readonly ArduinoSerial _serial;
    public SecurityEventMonitor(Db db, ArduinoSerial serial) { _db = db; _serial = serial; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ROBLOCK_SERIAL_PORT"))) return;
        // Yield before doing synchronous DB/serial work so application startup is not blocked.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            bool received = false;
            try { received = _db.ObserveSecurityStatus(_serial); }
            catch (DatabaseUnavailableException) { /* Existing DB fail-closed policy remains in force. */ }
            catch (Exception ex) { Console.Error.WriteLine("RobLock security monitor: " + ex.GetType().Name); }
            try { await Task.Delay(received ? 1000 : 5000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

static class Handlers
{
    private static IResult NotFoundOrEnded() =>
        Results.Json(new { error = "Session not found or ended." }, statusCode: 404);

    public static IResult UserLookup(string id, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var view = db.GetSessionView(id);
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        if (serial.IsPhysical(view.DeviceId))
        {
            var status = db.DeviceStatus(view.DeviceId, serial);
            if (status is IStatusCodeHttpResult code && code.StatusCode != 200)
                return status;
            view = db.GetSessionView(id);
            if (view is null) return NotFoundOrEnded();
        }
        return Results.Ok(view);
    }

    public static IResult UserJoin(string id, HttpContext ctx, Db db, Limiter limiter)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var view = db.JoinSession(id);
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        return Results.Ok(view);
    }

    public static IResult SaveInfo(string id, UserInfoRequest req, HttpContext ctx, Db db, Limiter limiter)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();

        var name = (req.Name ?? "").Trim();
        var address = (req.Address ?? "").Trim();
        var phone = (req.Phone ?? "").Trim();
        string? error = null;

        if (name.Length < 2 || name.Length > 80) error = "Name needs to be 2 to 80 characters.";
        else if (req.Age is null || req.Age < 1 || req.Age > 120) error = "Enter an age between 1 and 120.";
        else if (address.Length < 5 || address.Length > 200) error = "Address needs to be 5 to 200 characters.";
        else if (phone.Any(c => !(char.IsAsciiDigit(c) || c == ' ' || c == '+' || c == '-' || c == '(' || c == ')'))
                 || phone.Count(char.IsAsciiDigit) < 7 || phone.Count(char.IsAsciiDigit) > 15)
            error = "Phone number needs 7 to 15 digits.";
        else if ((name + address).Any(char.IsControl)) error = "Remove the special characters and try again.";

        if (error is not null) return Results.Json(new { error }, statusCode: 400);

        var view = db.SaveUserInfo(id, new UserInfo { Name = name, Age = req.Age!.Value, Address = address, Phone = phone });
        if (view is null) { limiter.Fail(key); return NotFoundOrEnded(); }
        return Results.Ok(view);
    }

    public static IResult UserCommand(string id, string action, HttpContext ctx, Db db, Limiter limiter, ArduinoSerial serial)
    {
        var key = "session:" + Auth.Ip(ctx);
        if (limiter.Blocked(key)) return Auth.TooMany();
        var result = db.UserCommand(id, action, serial);
        if (result is IStatusCodeHttpResult code && code.StatusCode == 404) limiter.Fail(key);
        return result;
    }
}

static class Auth
{
    private const int Iterations = 100_000;

    public static (string Salt, string Hash) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(AdminRecord admin, string password)
    {
        var salt = Convert.FromBase64String(admin.Salt);
        var expected = Convert.FromBase64String(admin.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static Task SignInAdmin(HttpContext ctx, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "admin"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    public static string Ip(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IResult TooMany() =>
        Results.Json(new { error = "Too many tries. Wait a minute and try again." }, statusCode: 429);
}

// Slows down guessing: 12 failed tries per minute per address.
class Limiter
{
    private const int MaxFails = 12;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, List<DateTime>> _fails = new();
    private readonly object _gate = new();

    public bool Blocked(string key)
    {
        lock (_gate) return Prune(key).Count >= MaxFails;
    }

    public void Fail(string key)
    {
        lock (_gate) Prune(key).Add(DateTime.UtcNow);
    }

    public void Reset(string key)
    {
        lock (_gate) _fails.Remove(key);
    }

    private List<DateTime> Prune(string key)
    {
        if (!_fails.TryGetValue(key, out var list))
        {
            list = new List<DateTime>();
            _fails[key] = list;
        }
        var now = DateTime.UtcNow;
        list.RemoveAll(t => now - t > Window);
        return list;
    }
}

static class Qr
{
    public static string Svg(string text)
    {
        var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(8);
    }
}

// One persistent port, one in-flight request at a time; debug and unsolicited lines are ignored.
record ArduinoResult(bool Ok, string Error, int Status, string? LockState = null, string? ArmState = null,
    bool? BuzzerOn = null, string? BuzzerMode = null, string? PinState = null, int? GraceMs = null);

sealed class ArduinoSerial : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _deviceId = Environment.GetEnvironmentVariable("ROBLOCK_ARDUINO_DEVICE_ID");
    private readonly string? _portName = Environment.GetEnvironmentVariable("ROBLOCK_SERIAL_PORT");
    private SerialPort? _port;
    private long _nextId;

    public bool IsPhysical(string id) => !string.IsNullOrWhiteSpace(_deviceId) && id == _deviceId;

    public ArduinoResult Send(string command, string? arguments = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_portName))
                return new(false, "ROBLOCK_SERIAL_PORT is not configured.", 503);
            try
            {
                if (_port is null || !_port.IsOpen)
                {
                    _port?.Dispose();
                    _port = new SerialPort(_portName, 115200, Parity.None, 8, StopBits.One)
                    {
                        NewLine = "\n", ReadTimeout = 500, WriteTimeout = 2000,
                        DtrEnable = false, RtsEnable = false
                    };
                    _port.Open();
                    // Opening Uno USB serial may reset the board; let it boot before sending.
                    Thread.Sleep(2200);
                    _port.DiscardInBuffer();
                }
                var id = Interlocked.Increment(ref _nextId).ToString("X");
                if (id.Length > 12) { Interlocked.Exchange(ref _nextId, 1); id = "1"; }
                _port.Write(id + " " + command + (arguments is null ? "" : " " + arguments) + "\n");
                var until = DateTime.UtcNow.AddMilliseconds(command == "LOCK" ? 25000 : 8000);
                var accepted = false;
                while (DateTime.UtcNow < until)
                {
                    string line;
                    try { line = _port.ReadLine().Trim(); }
                    catch (TimeoutException) { continue; }
                    if (line.StartsWith('#') || line.StartsWith("- ") || line.Length > 256) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || parts[0] != id) continue;
                    if (parts[2] != command && !(parts[2] == "-" && parts[1] == "REJECTED")) continue;
                    if (parts[1] == "ACCEPTED" && command != "STATUS") { accepted = true; continue; }
                    if (parts[1] == "REJECTED" || parts[1] == "ABORTED")
                    {
                        var reason = parts.FirstOrDefault(x => x.StartsWith("reason=")) ?? parts[1];
                        return new(false, "Arduino " + parts[1] + ": " + reason, 409);
                    }
                    if (parts[1] != "COMPLETED" || (command is ("LOCK" or "UNLOCK") && !accepted)) continue;
                    var state = parts.FirstOrDefault(x => x.StartsWith("lock="))?.Substring(5);
                    var arm = parts.FirstOrDefault(x => x.StartsWith("arm="))?.Substring(4);
                    if (state is not ("LOCKED" or "UNLOCKED" or "UNKNOWN") ||
                        !parts.Contains("feedback=NONE"))
                        return new(false, "Incomplete Arduino completion response.", 502);
                    var buzzer = parts.FirstOrDefault(x => x.StartsWith("buzzer="))?.Substring(7);
                    var mode = parts.FirstOrDefault(x => x.StartsWith("buzzerMode="))?.Substring(11);
                    bool? buzzerOn = buzzer == "ON" ? true : buzzer == "OFF" ? false : null;
                    if (command is ("BUZZER-ON" or "BUZZER-OFF" or "ACK-ALARM") &&
                        (!buzzerOn.HasValue || mode is not ("IDLE" or "TEST" or "ALARM")))
                        return new(false, "Arduino did not report buzzer state. Upload the updated sketch.", 502);
                    var pin = parts.FirstOrDefault(x => x.StartsWith("pin="))?.Substring(4);
                    var grace = parts.FirstOrDefault(x => x.StartsWith("graceMs="))?.Substring(8);
                    int? graceMs = int.TryParse(grace, out var ms) && ms >= 0 && ms <= 15000 ? ms : null;
                    return new(true, "", 200, state, arm, buzzerOn, mode, pin, graceMs);
                }
                // A timed-out operation may have executed. Close port and do not retry automatically.
                Disconnect();
                return new(false, "Arduino response timeout; commanded state is unconfirmed.", 504);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
            {
                Disconnect();
                return new(false, "Arduino serial unavailable: " + ex.Message, 503);
            }
        }
    }

    private void Disconnect() { try { _port?.Dispose(); } catch { } _port = null; }
    public void Dispose() { lock (_gate) Disconnect(); }
}
