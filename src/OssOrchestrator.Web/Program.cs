using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using OssOrchestrator.Web;

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

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapRazorPages();

app.MapGet("/", () => Results.Text("OSS Orchestrator dashboard (scaffold)", "text/plain"));

app.Run();

public partial class Program { }
