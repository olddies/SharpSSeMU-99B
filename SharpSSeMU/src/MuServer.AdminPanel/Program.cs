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

// Rutas a los archivos de datos reales del GameServer -- el panel lee y escribe esos mismos
// archivos, no una copia. Por defecto asume que este proyecto vive al lado de MuServer.GameServer
// (src/MuServer.AdminPanel y src/MuServer.GameServer), como está en el repo; GameServer:DataPath en
// appsettings.json lo puede pisar para otros despliegues.
var dataPath = builder.Configuration["GameServer:DataPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MuServer.GameServer", "bin", "Debug", "net10.0", "Data");
builder.Services.AddSingleton(new GameDataPaths(Path.GetFullPath(dataPath)));
builder.Services.AddScoped<ServerStatusRepository>();

// El editor de personajes habla directo con la base de DataServer (no hay archivo de por medio para
// un personaje) -- misma cadena de conexión que ya usa el DataServer real, leída de su propio
// DataServer.ini para no repetirla a mano en dos lugares; Database:ConnectionString en
// appsettings.json la puede pisar para otro despliegue.
var dataServerIniPath = builder.Configuration["DataServer:IniPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "MuServer.DataServer", "bin", "Debug", "net10.0", "DataServer.ini");
var pgConnectionString = builder.Configuration["Database:ConnectionString"]
    ?? MuServer.DataServer.Config.DataServerConfig.Load(dataServerIniPath).PostgresConnectionString;
builder.Services.AddScoped(_ => new CharacterEditRepository(pgConnectionString));

// El panel escribe la configuración del servidor, así que va detrás de una contraseña -- ver
// Auth/AdminPassword.cs para de dónde sale.
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

// Se instancia al arrancar (y no en el primer request) para que el aviso con la contraseña generada
// salga en la consola apenas se levanta el panel, no recién cuando alguien entra.
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

// El login tiene que ser un POST normal: la cookie se escribe en la respuesta HTTP, y un circuito de
// Blazor ya no puede tocar los headers cuando está corriendo. Va bajo /auth/ y no bajo /login porque
// esa ruta ya la ocupa la página Razor del formulario, y dos endpoints en la misma ruta chocan.
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

    // Sólo se acepta volver a una ruta local, para que un returnUrl armado a mano no pueda usar el
    // login como redirector a otro sitio.
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
