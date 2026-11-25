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
        Console.WriteLine("PASS: Supabase login and verified TLS connection.");
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
            Console.WriteLine("PASS: Device UPDATE executed under configured database account.");
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
