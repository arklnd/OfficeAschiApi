using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OfficeAschiApi.Data;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Services;

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
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
}
else
{
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlite("Data Source=officeaschi.db"));
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
    c.InjectJavascript("/swagger-totp.js");
});

// Serve Angular static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// CORS
app.UseCors();

// TOTP auth middleware (before controllers, after routing)
app.UseMiddleware<TotpAuthMiddleware>();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Fallback to index.html for Angular client-side routing
app.MapFallbackToFile("index.html");

app.Run();
