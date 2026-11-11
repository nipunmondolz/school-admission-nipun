using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SchoolAdmission.Api;

public static class Endpoints
{
    const string StudentHeader = "X-Student-Id";

    public static void Map(WebApplication app)
    {
        app.MapPost("/classes", CreateClass);
        app.MapGet("/classes", ListClasses);
        app.MapGet("/classes/{classId}", GetClass);
        app.MapPost("/classes/{classId}/holds", CreateHold);
        app.MapGet("/holds/{holdId}", GetHold);
        app.MapPost("/holds/{holdId}/confirm", ConfirmHold);
        app.MapDelete("/holds/{holdId}", CancelHold);
        app.MapGet("/me/holds", MyHolds);
    }

    // ---------- helpers ----------

    static IResult Err(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), statusCode: status);

    static IResult BadStudent() =>
        Err(400, "INVALID_STUDENT_ID", "শিক্ষার্থীর জন্মনিবন্ধন নম্বর ঠিক নেই। ঠিক ১৭টি অঙ্ক (০-৯) লিখুন।");

    /// <summary>X-Student-Id must be exactly 17 ASCII digits.</summary>
    static bool TryStudent(HttpRequest req, out string studentId)
    {
        studentId = req.Headers[StudentHeader].ToString().Trim();
        if (studentId.Length != 17) return false;
        foreach (var ch in studentId)
            if (ch < '0' || ch > '9') return false;
        return true;
    }

    static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..16];

    static DateTime TruncateToMs(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    /// <summary>A HELD hold whose expiry has passed is EXPIRED, whether or not the sweeper has run.</summary>
    static string EffectiveStatus(Hold h, DateTime now) =>
        h.Status == HoldStatus.Held && h.ExpiresAt <= now ? HoldStatus.Expired : h.Status;

    static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };

    /// <summary>
    /// Takes a row lock on the class. Everything that changes seat usage for a class runs
    /// inside a transaction that holds this lock, so decisions are serialized per class, in the
    /// database, across all backend processes.
    /// </summary>
    static Task LockClass(AdmissionDb db, string classId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Classes\" WHERE \"Id\" = {classId} FOR UPDATE", ct);

    static async Task<(int held, int confirmed)> CountSeats(AdmissionDb db, string classId, DateTime now, CancellationToken ct)
    {
        var rows = await db.Holds.AsNoTracking()
            .Where(h => h.ClassId == classId &&
                        (h.Status == HoldStatus.Confirmed || (h.Status == HoldStatus.Held && h.ExpiresAt > now)))
            .GroupBy(h => h.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync(ct);
        var held = rows.FirstOrDefault(r => r.Status == HoldStatus.Held)?.N ?? 0;
        var confirmed = rows.FirstOrDefault(r => r.Status == HoldStatus.Confirmed)?.N ?? 0;
        return (held, confirmed);
    }

    static ClassSummary Summary(ClassEntity c, int held, int confirmed) =>
        new(c.Id, c.Name, c.TotalSeats, held, confirmed, Math.Max(0, c.TotalSeats - held - confirmed));

    // ---------- classes ----------

    static async Task<IResult> CreateClass(HttpContext http, AdmissionDb db)
    {
        CreateClassRequest? body;
        try { body = await http.Request.ReadFromJsonAsync<CreateClassRequest>(http.RequestAborted); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return Err(400, "INVALID_BODY", "পাঠানো তথ্য ঠিক নেই। নাম (লেখা) ও আসন-সংখ্যা (পূর্ণসংখ্যা) দিন।");
        }

        var name = body?.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            return Err(400, "INVALID_NAME", "ক্লাসের নাম দিন (সর্বোচ্চ ২০০ অক্ষর)।");
        if (body?.TotalSeats is not int seats || seats < 1 || seats > 1_000_000)
            return Err(400, "INVALID_SEATS", "আসন-সংখ্যা ১ থেকে ১০,০০,০০০ এর মধ্যে একটি পূর্ণসংখ্যা হতে হবে।");

        var entity = new ClassEntity { Id = NewId("c_"), Name = name, TotalSeats = seats, CreatedAt = DateTime.UtcNow };
        db.Classes.Add(entity);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Json(new ClassCreated(entity.Id, entity.Name, entity.TotalSeats), statusCode: 201);
    }

    static async Task<IResult> ListClasses(HttpContext http, AdmissionDb db)
    {
        var ct = http.RequestAborted;
        var now = DateTime.UtcNow;
        var classes = await db.Classes.AsNoTracking().OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var counts = await db.Holds.AsNoTracking()
            .Where(h => h.Status == HoldStatus.Confirmed || (h.Status == HoldStatus.Held && h.ExpiresAt > now))
            .GroupBy(h => new { h.ClassId, h.Status })
            .Select(g => new { g.Key.ClassId, g.Key.Status, N = g.Count() })
            .ToListAsync(ct);

        var result = classes.Select(c =>
        {
            var held = counts.FirstOrDefault(x => x.ClassId == c.Id && x.Status == HoldStatus.Held)?.N ?? 0;
            var confirmed = counts.FirstOrDefault(x => x.ClassId == c.Id && x.Status == HoldStatus.Confirmed)?.N ?? 0;
            return Summary(c, held, confirmed);
        });
        return Results.Ok(result);
    }

    static async Task<IResult> GetClass(string classId, HttpContext http, AdmissionDb db)
    {
        var ct = http.RequestAborted;
        var c = await db.Classes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == classId, ct);
        if (c is null) return Err(404, "CLASS_NOT_FOUND", "এই ক্লাসটি পাওয়া যায়নি।");
        var (held, confirmed) = await CountSeats(db, classId, DateTime.UtcNow, ct);
        return Results.Ok(Summary(c, held, confirmed));
    }

    // ---------- holds ----------

    static async Task<IResult> CreateHold(string classId, HttpContext http, AdmissionDb db, AppSettings cfg)
    {
        if (!TryStudent(http.Request, out var studentId)) return BadStudent();
        var ct = http.RequestAborted;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockClass(db, classId, ct);

            var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId, ct);
            if (cls is null) return Err(404, "CLASS_NOT_FOUND", "এই ক্লাসটি পাওয়া যায়নি।");

            var now = DateTime.UtcNow;

            // An old, lapsed hold of this student must not block a fresh application.
            await db.Holds
                .Where(h => h.ClassId == classId && h.StudentId == studentId &&
                            h.Status == HoldStatus.Held && h.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.Expired), ct);

            var alreadyApplied = await db.Holds.AnyAsync(h =>
                h.ClassId == classId && h.StudentId == studentId &&
                (h.Status == HoldStatus.Held || h.Status == HoldStatus.Confirmed), ct);
            if (alreadyApplied)
                return Err(409, "ALREADY_APPLIED", "এই শিক্ষার্থীর এই ক্লাসে ইতিমধ্যে একটি চলমান আবেদন বা নিশ্চিত ভর্তি আছে।");

            var (held, confirmed) = await CountSeats(db, classId, now, ct);
            if (held + confirmed >= cls.TotalSeats)
                return Err(409, "SEAT_FULL", "দুঃখিত, এই ক্লাসে এখন কোনো আসন খালি নেই।");

            var hold = new Hold
            {
                Id = NewId("h_"),
                ClassId = classId,
                StudentId = studentId,
                Status = HoldStatus.Held,
                CreatedAt = TruncateToMs(now),
                ExpiresAt = TruncateToMs(now.AddSeconds(cfg.HoldDurationSeconds)),
            };
            db.Holds.Add(hold);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Json(new HoldCreated(hold.Id, hold.ClassId, hold.StudentId, hold.Status, hold.ExpiresAt), statusCode: 201);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return Err(409, "ALREADY_APPLIED", "এই শিক্ষার্থীর এই ক্লাসে ইতিমধ্যে একটি চলমান আবেদন বা নিশ্চিত ভর্তি আছে।");
        }
    }

    static async Task<IResult> GetHold(string holdId, HttpContext http, AdmissionDb db)
    {
        var h = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId, http.RequestAborted);
        if (h is null) return Err(404, "HOLD_NOT_FOUND", "এই আবেদনটি পাওয়া যায়নি।");
        return Results.Ok(new HoldView(h.Id, h.ClassId, EffectiveStatus(h, DateTime.UtcNow), h.ExpiresAt));
    }

    static async Task<IResult> ConfirmHold(string holdId, HttpContext http, AdmissionDb db)
    {
        if (!TryStudent(http.Request, out var studentId)) return BadStudent();
        var ct = http.RequestAborted;

        var probe = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId, ct);
        if (probe is null) return Err(404, "HOLD_NOT_FOUND", "এই আবেদনটি পাওয়া যায়নি।");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockClass(db, probe.ClassId, ct);

        // Re-read under the lock: state may have changed while we waited.
        var hold = await db.Holds.FirstOrDefaultAsync(x => x.Id == holdId, ct);
        if (hold is null) return Err(404, "HOLD_NOT_FOUND", "এই আবেদনটি পাওয়া যায়নি।");
        if (hold.StudentId != studentId)
            return Err(403, "NOT_OWNER", "এই আবেদনটি আপনার নয়।");

        var now = DateTime.UtcNow;

        if (hold.Status == HoldStatus.Confirmed)
            return Results.Ok(new HoldConfirmed(hold.Id, HoldStatus.Confirmed)); // idempotent repeat

        if (hold.Status == HoldStatus.Cancelled)
            return Err(409, "HOLD_CANCELLED", "এই আবেদনটি বাতিল করা হয়েছে, তাই ভর্তি নিশ্চিত করা যাবে না।");

        if (hold.Status == HoldStatus.Expired || hold.ExpiresAt <= now)
        {
            if (hold.Status == HoldStatus.Held)
            {
                hold.Status = HoldStatus.Expired;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            return Err(409, "HOLD_EXPIRED", "আবেদনের মেয়াদ শেষ হয়ে গেছে। আবার আবেদন করুন।");
        }

        hold.Status = HoldStatus.Confirmed;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new HoldConfirmed(hold.Id, HoldStatus.Confirmed));
    }

    static async Task<IResult> CancelHold(string holdId, HttpContext http, AdmissionDb db)
    {
        if (!TryStudent(http.Request, out var studentId)) return BadStudent();
        var ct = http.RequestAborted;

        var probe = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId, ct);
        if (probe is null) return Err(404, "HOLD_NOT_FOUND", "এই আবেদনটি পাওয়া যায়নি।");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockClass(db, probe.ClassId, ct);

        var hold = await db.Holds.FirstOrDefaultAsync(x => x.Id == holdId, ct);
        if (hold is null) return Err(404, "HOLD_NOT_FOUND", "এই আবেদনটি পাওয়া যায়নি।");
        if (hold.StudentId != studentId)
            return Err(403, "NOT_OWNER", "এই আবেদনটি আপনার নয়।");

        var now = DateTime.UtcNow;

        if (hold.Status == HoldStatus.Cancelled) return Results.NoContent(); // idempotent repeat
        if (hold.Status == HoldStatus.Confirmed)
            return Err(409, "CANNOT_CANCEL_CONFIRMED", "নিশ্চিত হয়ে যাওয়া ভর্তি এখান থেকে বাতিল করা যায় না।");
        if (hold.Status == HoldStatus.Expired || hold.ExpiresAt <= now)
            return Err(409, "HOLD_EXPIRED", "আবেদনের মেয়াদ আগেই শেষ হয়ে গেছে।");

        hold.Status = HoldStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> MyHolds(HttpContext http, AdmissionDb db)
    {
        if (!TryStudent(http.Request, out var studentId)) return BadStudent();
        var now = DateTime.UtcNow;

        var rows = await (from h in db.Holds.AsNoTracking()
                          join c in db.Classes.AsNoTracking() on h.ClassId equals c.Id
                          where h.StudentId == studentId
                          orderby h.CreatedAt descending, h.Id
                          select new { Hold = h, ClassName = c.Name })
            .ToListAsync(http.RequestAborted);

        return Results.Ok(rows.Select(r =>
            new MyHold(r.Hold.Id, r.Hold.ClassId, r.ClassName, EffectiveStatus(r.Hold, now), r.Hold.ExpiresAt)));
    }
}
