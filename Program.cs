using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OfficeAschiApi.Data;
using OfficeAschiApi.Hubs;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Services;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "OfficeAschi API", Version = "v1" });
    var totpScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "TOTP auth. Format: TOTP manager:{teamId}:{code} or TOTP reportee:{reporteeId}:{code}"
    };
    c.AddSecurityDefinition("TOTP", totpScheme);
    c.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("TOTP"), new List<string>() }
    });
});

// EF Core + SQLite
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite("Data Source=officeaschi.db"));

// App services
builder.Services.AddSingleton<TotpService>();
builder.Services.AddScoped<WaitlistService>();
builder.Services.AddScoped<NotificationService>();

// SignalR
builder.Services.AddSignalR();

// VAPID settings
builder.Services.Configure<VapidSettings>(builder.Configuration.GetSection("VapidSettings"));

var app = builder.Build();

// --- Auto-migrate on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- Auto-generate VAPID keys if not configured ---
{
    var vapid = app.Configuration.GetSection("VapidSettings");
    if (string.IsNullOrEmpty(vapid["PublicKey"]) || string.IsNullOrEmpty(vapid["PrivateKey"]))
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(true);
        // Uncompressed public key: 0x04 || X || Y (65 bytes)
        var publicKeyBytes = new byte[65];
        publicKeyBytes[0] = 0x04;
        Array.Copy(parameters.Q.X!, 0, publicKeyBytes, 1, 32);
        Array.Copy(parameters.Q.Y!, 0, publicKeyBytes, 33, 32);
        var publicKey = Convert.ToBase64String(publicKeyBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var privateKey = Convert.ToBase64String(parameters.D!)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        app.Configuration["VapidSettings:PublicKey"] = publicKey;
        app.Configuration["VapidSettings:PrivateKey"] = privateKey;
        app.Logger.LogInformation("Generated VAPID keys. Public key: {PublicKey}", publicKey);
    }
}

// --- Middleware pipeline ---
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "OfficeAschi API v1"));

// Serve Angular static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// TOTP auth middleware (before controllers, after routing)
app.UseMiddleware<TotpAuthMiddleware>();

app.MapControllers();

app.MapHub<NotificationHub>("/hubs/notifications");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Fallback to index.html for Angular client-side routing
app.MapFallbackToFile("index.html");

app.Run();
