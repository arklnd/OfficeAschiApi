using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using ModelContextProtocol.AspNetCore;
using OfficeAschiApi.Caching;
using OfficeAschiApi.Data;
using OfficeAschiApi.McpBridge;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Output Caching — server-side response cache with tag-based eviction
builder.Services.AddOutputCache(options =>
{
    // Team search list (no team-scoped tag — global list)
    options.AddPolicy("TeamsList", b => b
        .Expire(TimeSpan.FromMinutes(2))
        .SetVaryByQuery("q")
        .Tag("teams-list"));

    // Per-team static data: team detail, seats, reportees (scoped by team-{id})
    options.AddPolicy("TeamScoped", b => b
        .Expire(TimeSpan.FromMinutes(2))
        .AddPolicy<TeamScopedTagPolicy>());

    // Per-team availability data (short TTL, scoped by team-{id})
    options.AddPolicy("Availability", b => b
        .Expire(TimeSpan.FromSeconds(30))
        .SetVaryByQuery("date", "from", "to")
        .AddPolicy<TeamScopedTagPolicy>());

    // Seat overview: all seats across teams for a date (global)
    options.AddPolicy("SeatOverview", b => b
        .Expire(TimeSpan.FromSeconds(30))
        .SetVaryByQuery("date")
        .Tag("seats-overview"));
});

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "OfficeAschi API", Version = "v1" });
    var xmlFile = Path.Combine(AppContext.BaseDirectory, "OfficeAschiApi.xml");
    if (File.Exists(xmlFile))
        c.IncludeXmlComments(xmlFile);
    var totpScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Swagger helper: Enter SECRET manager:{teamId}:{base32Secret} or SECRET reportee:{reporteeId}:{base32Secret}\n\n" +
                      "The SECRET prefix is a Swagger UI convenience — it auto-generates the TOTP code from your secret on each request. " +
                      "The actual API expects: TOTP manager:{teamId}:{6-digit-code} or TOTP reportee:{reporteeId}:{6-digit-code} (use this format for curl / network calls)."
    };
    c.AddSecurityDefinition("TOTP", totpScheme);
    c.DocumentFilter<TotpSecurityDocumentFilter>();
});

// EF Core — controlled by DB_TYPE env variable (AZURE_SQL or SQLITE)
var dbType = builder.Configuration["DB_TYPE"] ?? "SQLITE";
if (dbType.Equals("AZURE_SQL", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
            sqlOptions => sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 6,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null))
        .AddInterceptors(sp.GetRequiredService<WriteThroughInterceptor>()));
    builder.Services.AddHostedService<DbKeepAliveService>();
}
else
{
    builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
        opt.UseSqlite("Data Source=officeaschi.db")
        .AddInterceptors(sp.GetRequiredService<WriteThroughInterceptor>()));
}

// CORS — allow any origin
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// App services
builder.Services.AddSingleton<TotpService>();
builder.Services.AddScoped<WaitlistService>();

// Write-through in-memory cache — mirrors DB, serves all reads from RAM
builder.Services.AddSingleton<WriteThroughCache>();
builder.Services.AddSingleton<WriteThroughInterceptor>();
builder.Services.AddHostedService<CacheWarmupService>();

// MCP server — auto-discover tools from API controllers
builder.Services.AddToolsFromControllers();
builder.Services
    .AddMcpServer(options =>
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        options.ServerInfo = new() { Name = "OfficeAschi", Version = version };
    })
    .WithHttpTransport(options => options.Stateless = true);

var app = builder.Build();

// --- Auto-migrate on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- Middleware pipeline ---
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "OfficeAschi API v1");
    c.InjectStylesheet("/swagger-custom.css");
    c.InjectJavascript("/swagger-totp.js");
});

// Serve Angular static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// CORS
app.UseCors();

// Output caching (after CORS, before controllers)
app.UseOutputCache();

// TOTP auth middleware (before controllers, after routing)
app.UseMiddleware<TotpAuthMiddleware>();

app.MapControllers();

app.MapMcp("/mcp");

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Fallback to index.html for Angular client-side routing
app.MapFallbackToFile("index.html");

app.Run();
