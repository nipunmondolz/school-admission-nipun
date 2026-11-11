using Microsoft.EntityFrameworkCore;

namespace SchoolAdmission.Api;

/// <summary>Creates the schema on startup. Retries until PostgreSQL accepts connections.</summary>
public sealed class DbInitializer(IServiceScopeFactory scopes, ILogger<DbInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        const int maxAttempts = 60;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AdmissionDb>();
                await db.Database.EnsureCreatedAsync(ct);
                log.LogInformation("Database ready");
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                log.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Housekeeping only. Correctness never depends on this: every query treats a HELD hold
/// with ExpiresAt &lt;= now as expired. The sweeper just keeps stored statuses tidy.
/// </summary>
public sealed class ExpirySweeper(IServiceScopeFactory scopes, ILogger<ExpirySweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AdmissionDb>();
                var now = DateTime.UtcNow;
                await db.Holds
                    .Where(h => h.Status == HoldStatus.Held && h.ExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatus.Expired), stop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogWarning("Expiry sweep failed: {Message}", ex.Message);
            }
        }
    }
}
