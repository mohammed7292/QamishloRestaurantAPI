
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
app.UseStaticFiles();

app.MapControllers();

app.Run();