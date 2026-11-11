using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(AppSettings.FromConfiguration(builder.Configuration));
builder.Services.AddDbContext<AdmissionDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<AppSettings>().DbConnection));

builder.Services.AddHostedService<DbInitializer>();   // must be registered before the sweeper
builder.Services.AddHostedService<ExpirySweeper>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Date")));   // lets the UI correct for browser/server clock differences

var app = builder.Build();

// Anything unexpected becomes a JSON error, never a blank page. Bad requests stay 4xx.
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (ex is BadHttpRequestException bad)
    {
        ctx.Response.StatusCode = bad.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new ErrorBody("BAD_REQUEST", "অনুরোধের তথ্য ঠিক নেই।"));
        return;
    }
    app.Logger.LogError(ex, "Unhandled error");
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new ErrorBody("SERVER_ERROR", "সার্ভারে একটি সমস্যা হয়েছে। একটু পরে আবার চেষ্টা করুন।"));
}));

app.UseCors();
Endpoints.Map(app);

app.Run();

// Needed so the test project can host the app with WebApplicationFactory<Program>.
public partial class Program { }
