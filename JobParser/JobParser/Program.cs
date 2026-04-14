using JobParser.Data;
using JobParser.Helpers;
using JobParser.Jobs;
using JobParser.Repositories;
using JobParser.Services;
using JobParser.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Serilog;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/parser-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient<AmountWorkParser>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = true
    })
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
    });

builder.Services.AddHttpClient<LayboardParser>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept",
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8");
});

builder.Services.AddHttpClient<PhoneCheckerService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "JobParser/2.0");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "JobParser API",
        Version = "v2.0",
        Description = "Парсер вакансий с проверкой номеров через PhoneNumberAPI"
    });
});

builder.Services.AddScoped<ProgressRepository>();
builder.Services.AddScoped<ProcessedUrlsRepository>();

builder.Services.AddScoped<HtmlParserService>();
builder.Services.AddScoped<PhoneCheckerService>();
builder.Services.AddScoped<CsvExportService>();
builder.Services.AddScoped<ParserService>();

builder.Services.AddScoped<ISiteParser, AmountWorkParser>();
builder.Services.AddScoped<ISiteParser, LayboardParser>();

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<ExclusionFilter>>();
    var filePath = config["ParserSettings:ExclusionsFilePath"] ?? "exclusions.txt";
    return new ExclusionFilter(filePath, logger);
});

builder.Services.AddQuartz(q =>
{
    q.UseMicrosoftDependencyInjectionJobFactory();

    var jobKey = new JobKey("ParserJob");
    q.AddJob<ParserJob>(opts => opts.WithIdentity(jobKey));

    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("ParserJob-trigger")
        .WithCronSchedule(builder.Configuration["Quartz:CronSchedule"] ?? "0 0 */6 * * ?")
    );
});

builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    try
    {
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.MigrateAsync();

        var processedCount = await context.ProcessedLeads.CountAsync();

        Log.Information("Database ready. Processed URLs: {ProcessedCount}", processedCount);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Database connection error");
    }
}

var phoneApiUrl = builder.Configuration["ParserSettings:PhoneCheckApiUrl"];
try
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var baseUrl = new Uri(phoneApiUrl!).GetLeftPart(UriPartial.Authority);
    await httpClient.GetAsync(baseUrl);
    Log.Information("PhoneNumberAPI доступен: {Url}", baseUrl);
}
catch
{
    Log.Error("PhoneNumberAPI недоступен по адресу {Url}. Запустите PhoneNumberAPI первым!", phoneApiUrl);
    Log.Error("Завершение работы JobParser...");
    Environment.Exit(1);
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "JobParser API v2.0");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new
{
    project = "JobParser",
    version = "2.0.0",
    status = "Running",
    endpoints = new
    {
        info = "GET /",
        run = "POST /api/parser/run",
        stats = "GET /api/stats",
        swagger = "GET /swagger"
    }
}))
.WithName("Info")
.WithTags("Info");

app.MapPost("/api/parser/run", async (ParserService parserService) =>
{
    try
    {
        await parserService.RunAsync();
        return Results.Ok(new
        {
            success = true,
            message = "Парсинг завершён",
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Ошибка запуска парсера");
        return Results.Problem(
            detail: ex.Message,
            title: "Ошибка выполнения парсинга"
        );
    }
})
.WithName("RunParser")
.WithTags("Parser");

app.MapGet("/api/stats", async (AppDbContext context) =>
{
    var processedCount = await context.ProcessedLeads.CountAsync();

    var sourceStats = await context.ProcessedLeads
        .GroupBy(p => p.Source)
        .Select(g => new
        {
            Source = g.Key,
            Count = g.Count(),
            Latest = g.Max(p => p.ProcessedAt)
        })
        .ToListAsync();

    return Results.Ok(new
    {
        totalProcessedUrls = processedCount,
        bySource = sourceStats,
        database = "jobparser_db",
        timestamp = DateTime.UtcNow
    });
})
.WithName("GetStats")
.WithTags("Stats");

Log.Information("JobParser v2.0 started");
app.Run();