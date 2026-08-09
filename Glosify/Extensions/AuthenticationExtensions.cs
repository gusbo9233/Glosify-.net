using Glosify.Data;
using Glosify.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Glosify.Extensions;

public static class AuthenticationExtensions
{
    /// <summary>
    /// Cookie sign-in for the web app, bearer tokens for the mobile client, and the two
    /// external providers, each registered only when its credentials are configured.
    /// </summary>
    public static IServiceCollection AddGlosifyAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Add Identity
        services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            // Off by default because registration has no email-confirmation flow yet;
            // a deployment that adds an IEmailSender can flip this without a code change.
            options.SignIn.RequireConfirmedAccount =
                configuration.GetValue("Identity:RequireConfirmedAccount", false);
        })
            .AddEntityFrameworkStores<GlosifyContext>()
            .AddDefaultTokenProviders()
            // Enables MapIdentityApi (token-based register/login/refresh) for the mobile app,
            // backed by the same user store as the cookie-based web sign-in.
            .AddApiEndpoints();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/Home/Error";
            // Stated rather than inherited. The defaults happen to be safe in production because
            // UseHttpsRedirection plus the forwarded X-Forwarded-Proto make every request HTTPS,
            // but that leaves the cookie's security depending on middleware ordering.
            // Development keeps SameAsRequest so the http:// launch profile can still sign in.
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
        });

        var authenticationBuilder = services.AddAuthentication();
        authenticationBuilder.AddBearerToken(IdentityConstants.BearerScheme);
        var googleClientId = configuration["Authentication:Google:ClientId"];
        var googleClientSecret = configuration["Authentication:Google:ClientSecret"];
        var microsoftClientId = configuration["Authentication:Microsoft:ClientId"];
        var microsoftClientSecret = configuration["Authentication:Microsoft:ClientSecret"];
        if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
        {
            authenticationBuilder.AddGoogle(options =>
            {
                options.ClientId = googleClientId;
                options.ClientSecret = googleClientSecret;
                options.Events.OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GoogleAuthentication");
                    logger.LogWarning(context.Failure, "Google external login failed.");

                    context.HandleResponse();
                    context.Response.Redirect("/login?externalLoginError=Google");
                    return Task.CompletedTask;
                };
            });
        }

        if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
        {
            authenticationBuilder.AddMicrosoftAccount(options =>
            {
                options.ClientId = microsoftClientId;
                options.ClientSecret = microsoftClientSecret;
                options.Events.OnRemoteFailure = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("MicrosoftAuthentication");
                    logger.LogWarning(context.Failure, "Microsoft external login failed.");

                    context.HandleResponse();
                    context.Response.Redirect("/login?externalLoginError=Microsoft");
                    return Task.CompletedTask;
                };
            });
        }

        return services;
    }
}
