using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    var messages = options.ModelBindingMessageProvider;
    messages.SetAttemptedValueIsInvalidAccessor((value, field) => $"O valor informado para {field} é inválido.");
    messages.SetValueMustNotBeNullAccessor(_ => "Informe um valor válido.");
    messages.SetValueMustBeANumberAccessor(field => $"O campo {field} deve ser numérico.");
    messages.SetUnknownValueIsInvalidAccessor(field => $"O valor informado para {field} é inválido.");
});
builder.Services.AddDbContext<NextGameAiDbContext>(options => options.UseSqlite(
    builder.Configuration.GetConnectionString("Library") ??
    $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "nextgameai.db")};Foreign Keys=True"));
builder.Services.AddScoped<CurrentUserProfile>();
builder.Services.AddScoped<LibraryService>();

var app = builder.Build();

// Local MVP: apply versioned migrations on F5. Production deployment will need a separate strategy.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().Database.Migrate();
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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

public partial class Program { }
