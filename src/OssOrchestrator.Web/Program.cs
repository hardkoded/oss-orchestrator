using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using OssOrchestrator.Web;
using OssOrchestrator.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Sign-in with Microsoft Entra ID. Everything requires an authenticated user,
// and only the accounts listed in Auth:AllowedUsers get through.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OwnerPolicy.Name, p => p.AddRequirements(new AllowedUserRequirement()))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new AllowedUserRequirement())
        .Build());

builder.Services.AddSingleton<IAuthorizationHandler, AllowedUserHandler>();
builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
builder.Services.AddMemoryCache();

// The page refreshes itself with fetch, which cannot follow the sign-in redirect. It gets 401 instead and reloads.
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    var redirect = options.Events.OnRedirectToIdentityProvider;
    options.Events.OnRedirectToIdentityProvider = context =>
    {
        if (context.Request.Headers.XRequestedWith != "fetch") return redirect(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.HandleResponse();
        return Task.CompletedTask;
    };
});

// The page is read every 15 seconds, and the free hosting tier limits the data sent per day.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

// The database is the source of truth. Access uses this app's identity (no keys stored in the app).
builder.Services.AddSingleton(_ => OrcDb.Create(
    builder.Configuration["Cosmos:Endpoint"] ?? throw new InvalidOperationException("Set Cosmos:Endpoint.")));

var app = builder.Build();

app.UseHttpsRedirection();
app.UseResponseCompression();
// Static files (the stylesheet) come before authentication, so the login page can be styled too.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGitHubWebhook();
app.MapRazorPages();
// The settings moved into the dashboard.
app.MapGet("/Settings", () => Results.Redirect("/#repos"));

app.Run();

public partial class Program { }
