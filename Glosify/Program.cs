using Glosify.Data;
using Glosify.Data.Importing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Glosify.Models;
using Glosify.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Add Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
})
    .AddEntityFrameworkStores<GlosifyContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
});

builder.Services.AddAuthentication()
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
    });

builder.Services.AddRazorPages();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ILanguageContext, CookieLanguageContext>();

// Configure Azure SQL Database
builder.Services.AddDbContext<GlosifyContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.CommandTimeout(30);
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
        }
    )
    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
);

var app = builder.Build();

if (args.FirstOrDefault()?.Equals("import-kaikki-german", StringComparison.OrdinalIgnoreCase) == true)
{
    var importOptions = ReadKaikkiImportOptions(args.Skip(1).ToArray());
    using var scope = app.Services.CreateScope();
    var importer = new KaikkiGermanDictionaryImporter(scope.ServiceProvider.GetRequiredService<GlosifyContext>());
    var result = await importer.ImportAsync(importOptions);
    Console.WriteLine($"Done. Read {result.LinesRead:n0}, parsed {result.Parsed:n0}, inserted {result.Inserted:n0}, skipped {result.Skipped:n0}.");
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "login",
    pattern: "login",
    defaults: new { controller = "Account", action = "Login" });

app.MapControllerRoute(
    name: "quizzes",
    pattern: "Quizzes/{action=Index}/{id?}",
    defaults: new { controller = "Quiz" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages();


app.Run();

static KaikkiImportOptions ReadKaikkiImportOptions(string[] args)
{
    var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "kaikki.org-dictionary-German.jsonl";
    if (!File.Exists(path) && File.Exists(Path.Combine("Glosify", path)))
    {
        path = Path.Combine("Glosify", path);
    }

    var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    var migrate = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
    var resume = args.Contains("--resume", StringComparer.OrdinalIgnoreCase);
    var batchSize = ReadIntOption(args, "--batch-size") ?? 500;
    var limit = ReadIntOption(args, "--limit");
    var checkpointPath = ReadStringOption(args, "--checkpoint");

    return new KaikkiImportOptions(path, dryRun, migrate, batchSize, limit, checkpointPath, resume);
}

static int? ReadIntOption(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }

        var prefix = $"{name}=";
        if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i][prefix.Length..], out value))
        {
            return value;
        }
    }

    return null;
}

static string? ReadStringOption(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }

        var prefix = $"{name}=";
        if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return args[i][prefix.Length..];
        }
    }

    return null;
}

public partial class Program { }
