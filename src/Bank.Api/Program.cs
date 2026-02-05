using System.Text;
using System.Text.Json.Serialization;
using Bank.Application.Abstractions;
using Bank.Infrastructure.Data;
using Bank.Infrastructure.Identity;
using Bank.Infrastructure.Persistence;
using Bank.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using Bank.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// =====================
// 1) Controllers + JSON
// =====================
// Addition: JsonStringEnumConverter so LedgerEntryType shows as "TransferOut" instead of 2.
builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// =====================
// 2) CORS (frontend dev)
// =====================
// Addition: allow frontend dev server to call the API from a browser.
// IMPORTANT: keep origins tight (do NOT AllowAnyOrigin with credentials).
var allowedOrigins =
    builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(o =>
{
    o.AddPolicy("frontend", p => p
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});


// ======
// Middleware - check if user is Active, if not - use email confirmation (smtp + token)
// ======
builder.Services.AddScoped<ActiveUserMiddleware>();

// ======
// DB
// ======
builder.Services.AddDbContext<BankDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// =========================
// Identity (Client = AppUser)
// =========================
builder.Services.AddIdentityCore<AppUser>(opt =>
{
    opt.User.RequireUniqueEmail = true;
    // Deploy grade: requires unique email on registration
    opt.SignIn.RequireConfirmedEmail = true;
})
.AddEntityFrameworkStores<BankDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

// =========
// JWT Auth
// =========
// NOTE: In production, Jwt:Key must be a strong secret from env/user-secrets/vault, NOT hardcoded.
var jwtKey = builder.Configuration["Jwt:Key"] ?? "DEV_ONLY_CHANGE_ME_CHANGE_ME_CHANGE_ME_32CHARS";
var issuer = builder.Configuration["Jwt:Issuer"] ?? "Bank.Api";
var audience = builder.Configuration["Jwt:Audience"] ?? "Bank.Client";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddScoped<IEmailSender, MailKitEmailSender>();

var smtp = builder.Configuration.GetSection("Smtp");
Console.WriteLine($"SMTP Host: {smtp["Host"]}, Port: {smtp["Port"]}, From: {smtp["FromEmail"]}");

// ==============
// Repos + UoW
// ==============
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<ILedgerRepository, LedgerRepository>();
builder.Services.AddScoped<IBankUnitOfWork, EfUnitOfWork>();

builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

// ===================
// Swagger + JWT button
// ===================
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Bank.Api", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost
};

fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Auto-migrate on startup (dev-friendly; for prod usually run migrations separately)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
    db.Database.Migrate();
}

// Swagger in Development only
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection(); (use in Production)

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}


// CORS must run before auth/authorization for browser calls
app.UseCors("frontend");

app.UseAuthentication();
app.UseMiddleware<ActiveUserMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Needed for integration testing with WebApplicationFactory<Program>
public partial class Program { }