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

var app = builder.Build();

// --- Auto-migrate on startup ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- Middleware pipeline ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "OfficeAschi API v1"));
}

// TOTP auth middleware (before controllers, after routing)
app.UseMiddleware<TotpAuthMiddleware>();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
