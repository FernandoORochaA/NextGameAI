using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.VisualDevelopment;

namespace NextGameAI.Web.Tests;

// A static-assets-only TestServer: never executes Program, registers a DbContext or opens SQLite.
[Trait("Host", "Mvc")]
public sealed class VisualStaticAssetsTests
{
    [Theory]
    [InlineData("/lib/bootstrap/dist/css/bootstrap.min.css", ".navbar")]
    [InlineData("/css/site.css", ".library-form")]
    [InlineData("/NextGameAI.Web.styles.css", ".navbar-brand")]
    public async Task VisualManifestServesOriginalAndCompressedCss(string path, string expectedCss)
    {
        await using var app = Build(loadManifest: true);
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var gzip in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.AcceptEncoding.ParseAdd(gzip ? "gzip" : "identity");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (gzip)
            {
                Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
                using var compressed = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var text = new StreamReader(compressed);
                Assert.Contains(expectedCss, await text.ReadToEndAsync());
            }
            else Assert.Contains(expectedCss, System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public async Task WithoutRuntimeManifestGeneratedCssIsUnavailable()
    {
        await using var app = Build(loadManifest: false);
        await app.StartAsync();
        using var client = app.GetTestClient();
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => client.GetAsync("/NextGameAI.Web.styles.css"));
        Assert.EndsWith("NextGameAI.Web.styles.css", error.FileName);
    }

    private static WebApplication Build(bool loadManifest)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/NextGameAI.Web"));
        // Empty builder does not discover appsettings, environment configuration or User Secrets.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = VisualEnvironment.Name,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = sourceRoot,
            WebRootPath = Path.Combine(sourceRoot, "wwwroot")
        });
        builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = Path.Combine(AppContext.BaseDirectory,
            "NextGameAI.Web.staticwebassets.runtime.json");
        builder.Services.AddRouting();
        builder.WebHost.UseTestServer();
        if (loadManifest) builder.WebHost.UseStaticWebAssets();
        var app = builder.Build();
        app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, "NextGameAI.Web.staticwebassets.endpoints.json"));
        return app;
    }
}
