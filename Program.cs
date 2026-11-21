using QamishloRestaurantAPI.Database;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Browser clients (customer app, delivery portal and POS) use the same API host.
builder.Services.AddCors(options =>
{
    options.AddPolicy("QamishloClients", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// FCM is optional: missing credentials disable push only, never order creation.
builder.Services.AddSingleton<QamishloRestaurantAPI.Services.FirebaseNotificationService>();

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();


var app = builder.Build();

// Automatically ensure database schema updates (DailyOrderNumber, Rejected status, photo storage, archiving)
await DatabaseInitializer.EnsureSchemaUpdatedAsync(app.Logger);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled API exception. TraceId: {TraceId}", context.TraceIdentifier);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "حدث خطأ غير متوقع في الخادم. يرجى المحاولة لاحقاً.",
                traceId = context.TraceIdentifier
            });
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("QamishloClients");
app.UseDefaultFiles();
app.UseStaticFiles();

// Explicit fallback routes for KDS and Web POS so trailing slashes or proxies never return 404
static IResult ServeWwwRootPage(IWebHostEnvironment env, string subFolder)
{
    var root = string.IsNullOrWhiteSpace(env.WebRootPath)
        ? Path.Combine(env.ContentRootPath, "wwwroot")
        : env.WebRootPath;
    var filePath = Path.Combine(root, subFolder, "index.html");
    if (!File.Exists(filePath))
        return Results.NotFound(new { success = false, message = $"Page /{subFolder}/index.html not found." });
    return Results.File(filePath, "text/html; charset=utf-8");
}
app.MapGet("/kds", (IWebHostEnvironment env) => ServeWwwRootPage(env, "kds"));
app.MapGet("/kds/", (IWebHostEnvironment env) => ServeWwwRootPage(env, "kds"));
app.MapGet("/pos", (IWebHostEnvironment env) => ServeWwwRootPage(env, "pos"));
app.MapGet("/pos/", (IWebHostEnvironment env) => ServeWwwRootPage(env, "pos"));

app.MapControllers();

app.Run();