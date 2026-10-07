using DocuMind.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.AddOpenApi();

var connectionString =
    builder.Configuration.GetConnectionString("DocuMind")
    ?? throw new InvalidOperationException(
        "Connection string 'DocuMind' is missing.");

builder.Services.AddDbContext<DocuMindDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocuMindDbContext>("postgresql");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/api/v1/health/ready");

app.Run();