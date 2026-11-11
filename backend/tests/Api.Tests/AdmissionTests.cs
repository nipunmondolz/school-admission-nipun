using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SchoolAdmission.Api;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Hosts the real app against a real PostgreSQL database (not an in-memory fake), because
/// the thing under test is PostgreSQL row locking.
///   Normal / Second : two independent app instances on the SAME database (simulates two processes)
///   Short           : an instance whose holds last only 1 second (expiry tests)
/// </summary>
public sealed class Fixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Normal = null!;
    public WebApplicationFactory<Program> Second = null!;
    public WebApplicationFactory<Program> Short = null!;

    public Task InitializeAsync()
    {
        var conn = EnsureDatabase();
        // Start sequentially so the schema is created once, before the others start.
        Normal = Make(conn, 600); Normal.CreateClient();
        Second = Make(conn, 600); Second.CreateClient();
        Short = Make(conn, 1); Short.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Normal.DisposeAsync();
        await Second.DisposeAsync();
        await Short.DisposeAsync();
    }

    static string EnsureDatabase()
    {
        var target = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=admission_test;Username=admission;Password=admission");
        var dbName = target.Database!;
        var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };

        // The server may still be starting; retry for up to ~60s.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var c = new NpgsqlConnection(admin.ConnectionString);
                c.Open();
                using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", c);
                exists.Parameters.AddWithValue("n", dbName);
                if (exists.ExecuteScalar() is null)
                {
                    using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", c);
                    create.ExecuteNonQuery();
                }
                return target.ConnectionString;
            }
            catch (NpgsqlException) when (attempt < 60) { Thread.Sleep(1000); }
        }
    }

    static WebApplicationFactory<Program> Make(string conn, int holdSeconds) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<AppSettings>();
                s.AddSingleton(new AppSettings(conn, holdSeconds));
            }));
}

public class AdmissionTests : IClassFixture<Fixture>
{
    readonly Fixture fx;
    public AdmissionTests(Fixture fx) => this.fx = fx;

    // 17-digit ids, unique per test run so tests never collide with each other.
    static long counter = 10_000_000_000_000_000L + DateTime.UtcNow.Ticks % 1_000_000_000_000_000L;
    static string NewSid() => Interlocked.Increment(ref counter).ToString();

    static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, string? sid = null, string? json = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (sid is not null) req.Headers.Add("X-Student-Id", sid);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return c.SendAsync(req);
    }

    static async Task<string> NewClass(HttpClient c, string name, int seats)
    {
        var r = await Send(c, HttpMethod.Post, "/classes", json: JsonSerializer.Serialize(new { name, totalSeats = seats }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    static Task<HttpResponseMessage> Apply(HttpClient c, string classId, string sid) =>
        Send(c, HttpMethod.Post, $"/classes/{classId}/holds", sid);

    static async Task<string> ApplyOk(HttpClient c, string classId, string sid)
    {
        var r = await Apply(c, classId, sid);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetString()!;
    }

    static async Task<(int total, int held, int confirmed, int available)> Seats(HttpClient c, string classId)
    {
        var r = await c.GetAsync($"/classes/{classId}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (j.GetProperty("totalSeats").GetInt32(), j.GetProperty("heldSeats").GetInt32(),
                j.GetProperty("confirmedSeats").GetInt32(), j.GetProperty("availableSeats").GetInt32());
    }

    static async Task<string> GetStatus(HttpClient c, string holdId)
    {
        var r = await c.GetAsync($"/holds/{holdId}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }

    // ---- Rule 1: never more than totalSeats, however many requests arrive together ----

    [Fact]
    public async Task Concurrent_applications_never_exceed_total_seats()
    {
        var c = fx.Normal.CreateClient();
        var classId = await NewClass(c, "Concurrency 10 seats", 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Apply(c, classId, NewSid())));

        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(190, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);

        var s = await Seats(c, classId);
        Assert.Equal((10, 10, 0, 0), s);
    }

    // ---- Bonus: two separate app instances sharing one database ----

    [Fact]
    public async Task Two_app_instances_on_one_database_never_exceed_total_seats()
    {
        var a = fx.Normal.CreateClient();
        var b = fx.Second.CreateClient();
        var classId = await NewClass(a, "Two instances 5 seats", 5);

        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(i => Apply(i % 2 == 0 ? a : b, classId, NewSid())));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(95, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal((5, 5, 0, 0), await Seats(b, classId));
    }

    // ---- Rule 3: one active application per student per class ----

    [Fact]
    public async Task Same_student_cannot_hold_twice_in_one_class_but_can_in_another()
    {
        var c = fx.Normal.CreateClient();
        var c1 = await NewClass(c, "Dup A", 5);
        var c2 = await NewClass(c, "Dup B", 5);
        var sid = NewSid();

        await ApplyOk(c, c1, sid);
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(c, c1, sid)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Apply(c, c2, sid)).StatusCode);
        Assert.Equal(1, (await Seats(c, c1)).held);

        // Parallel duplicate attempts: exactly one wins.
        var other = NewSid();
        var race = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Apply(c, c1, other)));
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(2, (await Seats(c, c1)).held);
    }

    // ---- Rules 4 & 5: owner-only confirm; repeated confirm counts once ----

    [Fact]
    public async Task Confirm_is_idempotent_and_owner_only()
    {
        var c = fx.Normal.CreateClient();
        var classId = await NewClass(c, "Confirm", 3);
        var sid = NewSid();
        var holdId = await ApplyOk(c, classId, sid);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", NewSid())).StatusCode);

        var repeats = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", sid)));
        Assert.All(repeats, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        Assert.Equal((3, 0, 1, 2), await Seats(c, classId));
        Assert.Equal("CONFIRMED", await GetStatus(c, holdId));

        // A confirmed student cannot apply again to the same class.
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(c, classId, sid)).StatusCode);
    }

    // ---- Rule 2: expiry releases the seat with no API call ----

    [Fact]
    public async Task Expired_hold_releases_seat_and_cannot_be_confirmed()
    {
        var c = fx.Short.CreateClient(); // holds last 1 second
        var classId = await NewClass(c, "Expiry", 1);
        var sid = NewSid();
        var holdId = await ApplyOk(c, classId, sid);

        Assert.Equal("HELD", await GetStatus(c, holdId));
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(c, classId, NewSid())).StatusCode); // seat is taken

        await Task.Delay(1600);

        Assert.Equal("EXPIRED", await GetStatus(c, holdId));
        Assert.Equal((1, 0, 0, 1), await Seats(c, classId));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", sid)).StatusCode);

        // The seat is free again, and the same student may re-apply.
        Assert.Equal(HttpStatusCode.Created, (await Apply(c, classId, sid)).StatusCode);
    }

    // ---- Cancel ----

    [Fact]
    public async Task Cancel_frees_the_seat_and_blocks_confirm()
    {
        var c = fx.Normal.CreateClient();
        var classId = await NewClass(c, "Cancel", 1);
        var sid = NewSid();
        var holdId = await ApplyOk(c, classId, sid);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", sid)).StatusCode);
        Assert.Equal("CANCELLED", await GetStatus(c, holdId));
        Assert.Equal((1, 0, 0, 1), await Seats(c, classId));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", sid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", NewSid())).StatusCode);

        var mine = await (await Send(c, HttpMethod.Get, "/me/holds", sid)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal("Cancel", mine[0].GetProperty("className").GetString());
        Assert.Equal("CANCELLED", mine[0].GetProperty("status").GetString());
    }

    // ---- Rule 7: bad input is a 4xx, never a 500 ----

    [Theory]
    [InlineData("{\"name\":\"\",\"totalSeats\":5}")]
    [InlineData("{\"name\":\"X\",\"totalSeats\":0}")]
    [InlineData("{\"name\":\"X\",\"totalSeats\":-3}")]
    [InlineData("{\"name\":\"X\",\"totalSeats\":\"many\"}")]
    [InlineData("{\"name\":\"X\",\"totalSeats\":99999999999999}")]
    [InlineData("{\"name\":\"X\"}")]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    public async Task Invalid_class_bodies_return_400(string json)
    {
        var c = fx.Normal.CreateClient();
        var r = await Send(c, HttpMethod.Post, "/classes", json: json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Invalid_ids_and_unknown_resources_return_4xx()
    {
        var c = fx.Normal.CreateClient();
        var classId = await NewClass(c, "Validation", 2);

        foreach (var bad in new[] { null, "", "123", "1234567890123456a", "123456789012345678" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Apply(c, classId, bad ?? "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, HttpMethod.Get, "/me/holds")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Apply(c, "c_missing", NewSid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/classes/c_missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/holds/h_missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Post, "/holds/h_missing/confirm", NewSid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Delete, "/holds/h_missing", NewSid())).StatusCode);
    }
}
