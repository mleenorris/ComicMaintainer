using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Data;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using ComicMaintainer.WebApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Configure settings from environment variables and appsettings
builder.Services.Configure<AppSettings>(options =>
{
    builder.Configuration.GetSection("AppSettings").Bind(options);
    
    // Override with environment variables if present
    var watchedDir = Environment.GetEnvironmentVariable("WATCHED_DIR");
    if (!string.IsNullOrEmpty(watchedDir))
        options.WatchedDirectory = watchedDir;
    
    var duplicateDir = Environment.GetEnvironmentVariable("DUPLICATE_DIR");
    if (!string.IsNullOrEmpty(duplicateDir))
        options.DuplicateDirectory = duplicateDir;
    
    var basePath = Environment.GetEnvironmentVariable("BASE_PATH");
    if (!string.IsNullOrEmpty(basePath))
        options.BasePath = basePath;
});

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? $"Data Source={Path.Combine(builder.Configuration["AppSettings:ConfigDirectory"] ?? "/Config", "comicmaintainer.db")}";
builder.Services.AddDbContext<ComicMaintainerDbContext>(options =>
    options.UseSqlite(connectionString));

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register application services
builder.Services.AddSingleton<IFileStoreService, FileStoreService>();
builder.Services.AddSingleton<IComicProcessorService, ComicProcessorService>();
builder.Services.AddSingleton<IFileWatcherService, FileWatcherService>();

// Add hosted service for file watcher
builder.Services.AddHostedService<FileWatcherHostedService>();

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ComicMaintainerDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Serve static files from wwwroot (we'll copy the Python templates/static there)
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

app.MapControllers();

// Map default route to serve index.html
app.MapFallbackToFile("index.html");

app.Run();
