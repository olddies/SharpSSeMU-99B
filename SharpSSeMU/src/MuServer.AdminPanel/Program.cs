using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MudBlazor.Services;
using MuServer.AdminPanel;
using MuServer.AdminPanel.Auth;
using MuServer.AdminPanel.Components;
using MuServer.AdminPanel.Config;
using MuServer.AdminPanel.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// Paths to the GameServer's real data files -- the panel reads and writes those same files, not a copy. By
// default it assumes this project lives next to MuServer.GameServer (src/MuServer.AdminPanel and
// src/MuServer.GameServer), as in the repo; GameServer:DataPath in appsettings.json can override it for other
// deployments.
var dataPath = builder.Configuration["GameServer:DataPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MuServer.GameServer", "bin", "Debug", "net10.0", "Data");
builder.Services.AddSingleton(new GameDataPaths(Path.GetFullPath(dataPath)));
builder.Services.AddScoped<ServerStatusRepository>();

// The character editor talks directly to the DataServer's database (there is no file in between for a
// character) -- the same connection string the real DataServer already uses, read from its own DataServer.ini
// so as not to repeat it by hand in two places; Database:ConnectionString in appsettings.json can override it
// for another deployment.
var dataServerIniPath = builder.Configuration["DataServer:IniPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MuServer.DataServer", "bin", "Debug", "net10.0", "DataServer.ini");
var pgConnectionString = builder.Configuration["Database:ConnectionString"]
    ?? MuServer.DataServer.Config.DataServerConfig.Load(dataServerIniPath).PostgresConnectionString;
builder.Services.AddScoped(_ => new CharacterEditRepository(pgConnectionString));

// The panel writes the server configuration, so it sits behind a password -- see Auth/AdminPassword.cs for
// where it comes from.
builder.Services.AddSingleton<AdminPassword>();
builder.Services.AddScoped<Localizer>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/auth/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "muserver.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// It is instantiated at start-up (and not on the first request) so that the notice with the generated password
// appears on the console as soon as the panel is started, not only when someone logs in.
_ = app.Services.GetRequiredService<AdminPassword>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The login has to be a normal POST: the cookie is written on the HTTP response, and a Blazor circuit can no
// longer touch the headers while it is running. It lives under /auth/ and not under /login because that route
// is already taken by the Razor page of the form, and two endpoints on the same route clash.
app.MapPost("/auth/login", async (HttpContext http, AdminPassword adminPassword) =>
{
    var form = await http.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    if (!adminPassword.Matches(password))
    {
        var failedUrl = string.IsNullOrEmpty(returnUrl)
            ? "/login?error=true"
            : $"/login?error=true&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.Redirect(failedUrl);
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    // Only returning to a local route is accepted, so that a hand-crafted returnUrl cannot use the login as a
    // redirector to another site.
    var target = !string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        ? returnUrl
        : "/";

    return Results.Redirect(target);
});

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// Language switch: a normal POST (like login/logout) because the cookie is written on the HTTP response.
app.MapPost("/auth/lang", async (HttpContext http) =>
{
    var form = await http.Request.ReadFormAsync();
    var lang = MuServer.Shared.Localization.Loc.Normalize(form["lang"].ToString());
    var returnUrl = form["returnUrl"].ToString();

    http.Response.Cookies.Append(Localizer.CookieName, lang, new CookieOptions
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
    });

    var target = !string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/";

    return Results.Redirect(target);
});

app.Run();
