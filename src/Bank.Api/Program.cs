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
using Bank.Api.Chatbot;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// =====================
// 1) Controllers + JSON
// =====================
builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// =====================
// 2) CORS
// =====================
static string[] NormalizeOrigins(IEnumerable<string?> origins)
{
    return origins
        .Where(o => !string.IsNullOrWhiteSpace(o))
        .Select(o => o!.Trim().TrimEnd('/'))
        .Where(o =>
        {
            if (o.Contains("*"))
                return o.StartsWith("http://*.") || o.StartsWith("https://*."); // e.g. https://*.neobankers.org

            return Uri.TryCreate(o, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

var knownFrontendOrigins = new[]
{
    builder.Configuration["App:FrontendBaseUrl"],
    builder.Configuration["Frontend:BaseUrl"],
    "http://localhost:5173",
    "http://127.0.0.1:5173",
    "http://localhost:4173",
    "http://127.0.0.1:4173",
    "https://neobankers.org",
    "https://www.neobankers.org",
    "https://app.neobankers.org",
    "https://*.neobankers.org"
};

var allowedOrigins = NormalizeOrigins(configuredOrigins.Concat(knownFrontendOrigins));
Console.WriteLine($"CORS Allowed Origins: {string.Join(", ", allowedOrigins)}");

builder.Services.AddCors(o =>
{
    o.AddPolicy("frontend", p => p
        .WithOrigins(allowedOrigins)
        .SetIsOriginAllowedToAllowWildcardSubdomains()
        .AllowAnyHeader()
        .AllowAnyMethod()
        // CHATBOT: SignalR uses negotiate + websockets; some setups need credentials.
        .AllowCredentials()
    );
});

// ======
// Middleware - check if user is Active
// ======
builder.Services.AddScoped<ActiveUserMiddleware>();

// ======
// DB
// ======
builder.Services.AddDbContext<BankDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// =========================
// CHATBOT: SignalR + Router
// =========================
builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
builder.Services.Configure<ChatbotLlmOptions>(builder.Configuration.GetSection(ChatbotLlmOptions.SectionName));
builder.Services.AddSingleton<NoopChatIntentResolver>();
builder.Services.AddHttpClient<OpenAiChatIntentResolver>();
builder.Services.AddScoped<IChatIntentResolver>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ChatbotLlmOptions>>().Value;
    if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
        return sp.GetRequiredService<NoopChatIntentResolver>();

    return sp.GetRequiredService<OpenAiChatIntentResolver>();
});
builder.Services.AddScoped<IChatbotRouter, ChatbotRouter>();

builder.Logging.AddConsole();

// =========================
// Identity (Client = AppUser)
// =========================
builder.Services.AddIdentityCore<AppUser>(opt =>
{
    opt.User.RequireUniqueEmail = true;
    opt.SignIn.RequireConfirmedEmail = true;
})
.AddEntityFrameworkStores<BankDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

// =========
// JWT Auth
// =========
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

        // =========================
        // CHATBOT: JWT for SignalR
        // =========================
        // Browsers cannot set Authorization header for WebSocket upgrade easily,
        // so SignalR commonly sends JWT via query string: ?access_token=...
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                    context.Token = accessToken;

                return Task.CompletedTask;
            }
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

// Auto-migrate on startup
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

// Use in Production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// CORS must run before auth
app.UseCors("frontend");

app.UseAuthentication();
app.UseMiddleware<ActiveUserMiddleware>();
app.UseAuthorization();

// =========================
// CHATBOT: Hub endpoint
// =========================
app.MapHub<Bank.Api.Chatbot.ChatHub>("/hubs/chat");

app.MapControllers();

app.Run();

// Needed for integration testing with WebApplicationFactory<Program>
public partial class Program { }
