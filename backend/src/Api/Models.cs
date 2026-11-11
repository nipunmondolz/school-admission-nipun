using Microsoft.EntityFrameworkCore;

namespace SchoolAdmission.Api;

public static class HoldStatus
{
    public const string Held = "HELD";
    public const string Confirmed = "CONFIRMED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}

public class ClassEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int TotalSeats { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Hold
{
    public string Id { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Status { get; set; } = HoldStatus.Held;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdmissionDb(DbContextOptions<AdmissionDb> options) : DbContext(options)
{
    public DbSet<ClassEntity> Classes => Set<ClassEntity>();
    public DbSet<Hold> Holds => Set<Hold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ClassEntity>(e =>
        {
            e.ToTable("Classes", t => t.HasCheckConstraint("CK_Classes_TotalSeats_Positive", "\"TotalSeats\" > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<Hold>(e =>
        {
            e.ToTable("Holds");
            e.HasKey(x => x.Id);
            e.Property(x => x.StudentId).HasMaxLength(17).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasOne<ClassEntity>().WithMany().HasForeignKey(x => x.ClassId);

            // Second line of defence: even if application code is wrong, the database
            // refuses a second HELD/CONFIRMED hold for the same student in the same class.
            e.HasIndex(x => new { x.ClassId, x.StudentId })
                .IsUnique()
                .HasFilter("\"Status\" IN ('HELD','CONFIRMED')");

            e.HasIndex(x => new { x.ClassId, x.Status, x.ExpiresAt });
            e.HasIndex(x => new { x.StudentId, x.CreatedAt });
        });
    }
}

// ---- DTOs ----
public record CreateClassRequest(string? Name, int? TotalSeats);
public record ErrorBody(string Code, string Message);
public record ClassCreated(string Id, string Name, int TotalSeats);
public record ClassSummary(string Id, string Name, int TotalSeats, int HeldSeats, int ConfirmedSeats, int AvailableSeats);
public record HoldCreated(string HoldId, string ClassId, string StudentId, string Status, DateTime ExpiresAt);
public record HoldView(string HoldId, string ClassId, string Status, DateTime ExpiresAt);
public record HoldConfirmed(string HoldId, string Status);
public record MyHold(string HoldId, string ClassId, string ClassName, string Status, DateTime ExpiresAt);
