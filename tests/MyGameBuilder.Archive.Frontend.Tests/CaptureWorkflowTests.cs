using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MyGameBuilder.Archive.Frontend.Tests;

public sealed class CaptureWorkflowTests
{
    [Fact]
    public async Task CaptureWorkflowStoresDedupeNon200HeadersDiscoveredUrlsAndExcludes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-frontend-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var seeds = Path.Combine(directory, "seeds.txt");
        var output = Path.Combine(directory, "frontend.sqlite");
        await File.WriteAllTextAsync(
            seeds,
            """
            url http://example.com/index.html
            url http://example.com/missing.png
            exclude https://example.com/forum/
            url http://example.com/forum/page.html
            """,
            TestContext.Current.CancellationToken);

        using var loggerFactory = LoggerFactory.Create(static _ => { });
        var handler = new FakeWaybackHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WaybackCdxClient(
            httpClient,
            new Uri("https://web.test/cdx/search/cdx"),
            new Uri("https://web.test/web"),
            loggerFactory.CreateLogger<WaybackCdxClient>());
        var workflow = new CaptureWorkflow(
            new FrontendArchiveOptions(
                seeds,
                output,
                Path.Combine(directory, "work"),
                Concurrency: 2,
                Resume: false,
                Replace: false,
                new Uri("https://web.test/cdx/search/cdx"),
                new Uri("https://web.test/web")),
            client,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));
        Assert.Equal(0, handler.ForumReplayCount);

        using var connection = OpenReadOnly(output);
        Assert.Equal(3, Count(connection, "SELECT COUNT(*) FROM frontend_capture;"));
        Assert.Equal(2, Count(connection, "SELECT COUNT(*) FROM frontend_content;"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replay_status_code = 404;"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_exclude;"));
        Assert.Equal(3, Count(connection, "SELECT COUNT(*) FROM frontend_response_header WHERE name = 'x-original-status';"));
        Assert.True(Count(connection, "SELECT COUNT(*) FROM frontend_discovered_url WHERE resolved_canonical_url = 'http://cdn.example.com/app.js';") >= 1);
        Assert.True(Count(connection, "SELECT COUNT(*) FROM frontend_discovered_url WHERE resolved_canonical_url = 'http://example.com/style/site.css';") >= 1);

        using var lookup = connection.CreateCommand();
        lookup.CommandText =
            """
            SELECT capture_timestamp
            FROM v_frontend_capture_lookup
            WHERE canonical_url = 'http://example.com/index.html'
              AND capture_timestamp <= $timestamp
            ORDER BY capture_timestamp DESC
            LIMIT 1;
            """;
        lookup.Parameters.AddWithValue("$timestamp", "20000115000000");
        Assert.Equal("20000101000000", lookup.ExecuteScalar());

        lookup.Parameters["$timestamp"].Value = "20000301000000";
        Assert.Equal("20000201000000", lookup.ExecuteScalar());
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }

    private sealed class FakeWaybackHandler : HttpMessageHandler
    {
        public int ForumReplayCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.RequestUri?.OriginalString ?? string.Empty;
            if (text.Contains("/cdx/search/cdx", StringComparison.Ordinal))
            {
                return Task.FromResult(CdxResponse(text));
            }

            if (text.Contains("forum/page.html", StringComparison.Ordinal))
            {
                ForumReplayCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (text.Contains("20000101000000id_/http://example.com/index.html", StringComparison.Ordinal)
                || text.Contains("20000201000000id_/http://example.com/index.html", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("hello http://cdn.example.com/app.js url(/style/site.css)", Encoding.UTF8, "text/html")
                };
                response.Headers.TryAddWithoutValidation("X-Original-Status", "200");
                return Task.FromResult(response);
            }

            if (text.Contains("missing.png", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("missing http://example.com/lost.png", Encoding.UTF8, "text/plain")
                };
                response.Headers.TryAddWithoutValidation("X-Original-Status", "404");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected " + text)
            });
        }

        private static HttpResponseMessage CdxResponse(string requestText)
        {
            var decoded = Uri.UnescapeDataString(requestText);
            var body = decoded.Contains("index.html", StringComparison.Ordinal)
                ? """
                  [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                  ["20000101000000","http://example.com/index.html","text/html","200","DIGEST1","55",null],
                  ["20000201000000","http://example.com/index.html","text/html","200","DIGEST1","55",null]]
                  """
                : decoded.Contains("missing.png", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000102000000","http://example.com/missing.png","image/png","404","DIGEST2","35",null]]
                      """
                    : """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000103000000","http://example.com/forum/page.html","text/html","200","DIGEST3","35",null]]
                      """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
