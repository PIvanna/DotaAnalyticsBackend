using System.Text;
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Hubs;
using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<DotaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection") 
                      ?? "Host=localhost;Port=5432;Database=dota_analytics;Username=postgres;Password=1234"));

builder.Services.AddHttpClient(); 
// ------------------------------

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSignalR();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "super_secret_key_1234567890123456"; 
var key = Encoding.ASCII.GetBytes(jwtKey);

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,   
            ValidateAudience = false, 
        
            RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            NameClaimType = "http://schemas.microsoft.com/ws/2005/05/identity/claims/name",
        
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/syncHub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });


builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("PlayerOnly", policy => policy.RequireRole("Player"));
    options.AddPolicy("AnyUser", policy => policy.RequireRole("Admin", "Player", "Spectator"));
});

builder.Services.AddScoped<DataSeederService>(); 
builder.Services.AddScoped<TournamentService>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddScoped<MlPredictionService>();
builder.Services.AddScoped<HeroService>();
builder.Services.AddHostedService<SyncBackgroundWorker>();

builder.Services.AddSingleton<SystemStateService>();
builder.Services.AddScoped<PlayerDashboardService>();
    
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", builder => builder
        .WithOrigins("http://localhost:3000") 
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()); 
});


var app = builder.Build();

app.UseCors("CorsPolicy");


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication(); 
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Match}/{action=Index}/{id?}");

app.MapHub<SyncHub>("/syncHub");

app.Run();