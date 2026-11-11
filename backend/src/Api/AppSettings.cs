namespace SchoolAdmission.Api;

/// <summary>
/// Runtime settings. Read once from environment variables:
///   DB_CONNECTION          Npgsql connection string
///   HOLD_DURATION_SECONDS  hold lifetime in seconds (default 600)
/// </summary>
public sealed record AppSettings(string DbConnection, int HoldDurationSeconds)
{
    public const string DefaultConnection =
        "Host=localhost;Port=5432;Database=admission;Username=admission;Password=admission";

    public static AppSettings FromConfiguration(IConfiguration c)
    {
        var conn = c["DB_CONNECTION"];
        if (string.IsNullOrWhiteSpace(conn)) conn = c.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(conn)) conn = DefaultConnection;

        var seconds = int.TryParse(c["HOLD_DURATION_SECONDS"], out var s) && s > 0 ? s : 600;
        return new AppSettings(conn, seconds);
    }
}
