using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OpenGameBuilder.Mgb.Archive.ClientArchiver.Tests;

public sealed class CaptureWorkflowTests
{
    [Fact]
    public async Task CaptureWorkflowStoresMixedStatusCapturesPrunesAllErrorUrlsAndAppliesExcludes()
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
            url http://example.com/mixed.html
            url https://www.example.com/secure.html
            url http://example.com/secure.html
            exclude https://v2.example.com/
            url http://v2.example.com/client.html
            exclude-contains forum
            url http://example.com/community/forum/page.html
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
                RetryReplayErrors: false,
                new Uri("https://web.test/cdx/search/cdx"),
                new Uri("https://web.test/web")),
            client,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(output));
        Assert.Equal(0, handler.ForumReplayCount);
        Assert.Equal(0, handler.V2ReplayCount);
        Assert.Equal(1, handler.V2RedirectReplayCount);

        using var connection = OpenReadOnly(output);
        Assert.Equal(11, Count(connection, "SELECT COUNT(*) FROM frontend_capture;"));
        Assert.Equal(7, Count(connection, "SELECT COUNT(*) FROM frontend_content;"));
        Assert.Equal(5, Count(connection, "SELECT COUNT(*) FROM frontend_resource;"));
        Assert.Equal(4, Count(connection, "SELECT COUNT(*) FROM v_frontend_capture_lookup WHERE canonical_url = 'http://example.com/secure.html';"));
        Assert.Equal(3, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/index.html';"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://cdn.example.com/app.js';"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/style/site.css';"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replay_status_code = 404;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/missing.png';"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/redirect-to-v2.html';"));
        Assert.Equal(2, Count(connection, "SELECT COUNT(*) FROM frontend_exclude;"));
        Assert.Equal(8, Count(connection, "SELECT COUNT(*) FROM frontend_response_header WHERE name = 'x-original-status';"));

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

    [Fact]
    public async Task CaptureWorkflowFollowsAllowedLiveRedirectsAndSkipsExcludedLiveRedirects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-frontend-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var seeds = Path.Combine(directory, "seeds.txt");
        var output = Path.Combine(directory, "frontend.sqlite");
        await File.WriteAllTextAsync(
            seeds,
            """
            url http://example.com/live.js
            url http://example.com/to-v2.js
            exclude https://v2.example.com/
            """,
            TestContext.Current.CancellationToken);

        using var loggerFactory = LoggerFactory.Create(static _ => { });
        var handler = new DirectRedirectHandler();
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
                RetryReplayErrors: false,
                new Uri("https://web.test/cdx/search/cdx"),
                new Uri("https://web.test/web")),
            client,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.LiveTargetCount);
        Assert.Equal(0, handler.V2TargetCount);
        using var connection = OpenReadOnly(output);
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/live.js' AND replay_status_code = 200 AND replay_url = 'https://cdn.example.com/live.js';"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE original_url = 'http://example.com/to-v2.js';"));

        using var lookup = connection.CreateCommand();
        lookup.CommandText =
            """
            SELECT fc.body
            FROM frontend_capture c
            JOIN frontend_content fc ON fc.content_id = c.content_id
            WHERE c.original_url = 'http://example.com/live.js';
            """;
        Assert.Equal("final live", Encoding.UTF8.GetString((byte[])lookup.ExecuteScalar()!));
    }

    [Fact]
    public async Task CaptureWorkflowRetriesTransientCdxFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-frontend-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var seeds = Path.Combine(directory, "seeds.txt");
        var output = Path.Combine(directory, "frontend.sqlite");
        await File.WriteAllTextAsync(
            seeds,
            "url http://example.com/index.html",
            TestContext.Current.CancellationToken);

        using var loggerFactory = LoggerFactory.Create(static _ => { });
        var handler = new FakeWaybackHandler { FailNextCdxRequest = true };
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
                RetryReplayErrors: false,
                new Uri("https://web.test/cdx/search/cdx"),
                new Uri("https://web.test/web")),
            client,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        await workflow.RunAsync(TestContext.Current.CancellationToken);

        using var connection = OpenReadOnly(output);
        Assert.Equal(6, Count(connection, "SELECT COUNT(*) FROM frontend_capture;"));
        Assert.True(handler.CdxRequestCount >= 2);
    }

    [Fact]
    public async Task CaptureWorkflowDefersExhaustedReplayFailuresAndContinuesOtherCaptures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mgb-frontend-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var seeds = Path.Combine(directory, "seeds.txt");
        var output = Path.Combine(directory, "frontend.sqlite");
        await File.WriteAllTextAsync(
            seeds,
            """
            url http://example.com/bad.txt
            url http://example.com/good.txt
            """,
            TestContext.Current.CancellationToken);

        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var httpClient = new HttpClient(new ReplayFailureWaybackHandler());
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
                RetryReplayErrors: false,
                new Uri("https://web.test/cdx/search/cdx"),
                new Uri("https://web.test/web")),
            client,
            loggerFactory.CreateLogger<CaptureWorkflow>());

        var ex = await Assert.ThrowsAsync<ArchiveFatalException>(() => workflow.RunAsync(TestContext.Current.CancellationToken));

        Assert.Contains("replay downloads remain pending", ex.Message, StringComparison.Ordinal);
        using var connection = OpenReadOnly(output + ".inprogress.sqlite");
        Assert.Equal(2, Count(connection, "SELECT COUNT(*) FROM frontend_capture;"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_content;"));
        Assert.Equal(1, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replayed_utc IS NULL AND replay_error IS NULL;"));
        Assert.Equal(0, Count(connection, "SELECT COUNT(*) FROM frontend_capture WHERE replay_error IS NOT NULL;"));
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

    private sealed class DirectRedirectHandler : HttpMessageHandler
    {
        public int LiveTargetCount { get; private set; }
        public int V2TargetCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.RequestUri?.OriginalString ?? string.Empty;
            if (text.Contains("/cdx/search/cdx", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        [["timestamp","original","mimetype","statuscode","digest","length","redirect"]]
                        """,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            if (text.Equals("http://example.com/live.js", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                response.Headers.Location = new Uri("https://cdn.example.com/live.js");
                return Task.FromResult(response);
            }

            if (text.Equals("https://cdn.example.com/live.js", StringComparison.Ordinal))
            {
                LiveTargetCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("final live", Encoding.UTF8, "application/javascript")
                });
            }

            if (text.Equals("http://example.com/to-v2.js", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://v2.example.com/client.js");
                return Task.FromResult(response);
            }

            if (text.Equals("https://v2.example.com/client.js", StringComparison.Ordinal))
            {
                V2TargetCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("excluded", Encoding.UTF8, "application/javascript")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unexpected " + text)
            });
        }
    }

    private sealed class FakeWaybackHandler : HttpMessageHandler
    {
        public int ForumReplayCount { get; private set; }
        public int V2ReplayCount { get; private set; }
        public int V2RedirectReplayCount { get; private set; }
        public int CdxRequestCount { get; private set; }
        public bool FailNextCdxRequest { get; init; }
        private bool _failedCdxRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.RequestUri?.OriginalString ?? string.Empty;
            if (text.Contains("/cdx/search/cdx", StringComparison.Ordinal))
            {
                CdxRequestCount++;
                if (FailNextCdxRequest && !_failedCdxRequest)
                {
                    _failedCdxRequest = true;
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("temporary CDX connection failure"));
                }

                return Task.FromResult(CdxResponse(text));
            }

            if (text.Contains("forum/page.html", StringComparison.Ordinal))
            {
                ForumReplayCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (text.Contains("v2.example.com/client.html", StringComparison.Ordinal))
            {
                V2ReplayCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (text.Contains("redirect-to-v2.html", StringComparison.Ordinal))
            {
                V2RedirectReplayCount++;
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://web.test/web/20000115000000id_/https://v2.example.com/client.html");
                return Task.FromResult(response);
            }

            if (text.Equals("http://example.com/index.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("live index", Encoding.UTF8, "text/html")
                });
            }

            if (text.Equals("http://cdn.example.com/app.js", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("console.log('cdn');", Encoding.UTF8, "application/javascript")
                });
            }

            if (text.Equals("http://example.com/style/site.css", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("body{}", Encoding.UTF8, "text/css")
                });
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

            if (text.Contains("secure.html", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("secure", Encoding.UTF8, "text/html")
                };
                response.Headers.TryAddWithoutValidation("X-Original-Status", "200");
                return Task.FromResult(response);
            }

            if (text.Contains("20000102000000id_/http://example.com/mixed.html", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("mixed missing", Encoding.UTF8, "text/plain")
                };
                response.Headers.TryAddWithoutValidation("X-Original-Status", "404");
                return Task.FromResult(response);
            }

            if (text.Contains("20000202000000id_/http://example.com/mixed.html", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("mixed ok", Encoding.UTF8, "text/plain")
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
                  ["20000115000000","http://example.com/redirect-to-v2.html","text/html","302",null,null,null],
                  ["20000201000000","http://example.com/index.html","text/html","200","DIGEST1","55",null]]
                  """
                : decoded.Contains("https://www.example.com/secure.html", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000104000000","https://www.example.com/secure.html","text/html","200","DIGEST6","6",null]]
                      """
                : decoded.Contains("http://example.com/secure.html", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000105000000","http://example.com/secure.html","text/html","200","DIGEST6","6",null]]
                      """
                : decoded.Contains("mixed.html", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000102000000","http://example.com/mixed.html","text/html","404","DIGEST4","13",null],
                      ["20000202000000","http://example.com/mixed.html","text/html","200","DIGEST5","8",null]]
                      """
                : decoded.Contains("missing.png", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000102000000","http://example.com/missing.png","image/png","404","DIGEST2","-7113922270",null]]
                      """
                    : decoded.Contains("v2.example.com", StringComparison.Ordinal)
                        ? """
                          [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                          ["20000103000000","http://v2.example.com/client.html","text/html","200","DIGESTV2","35",null]]
                          """
                    : """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000103000000","http://example.com/community/forum/page.html","text/html","200","DIGEST3","35",null]]
                      """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ReplayFailureWaybackHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = request.RequestUri?.OriginalString ?? string.Empty;
            if (text.Contains("/cdx/search/cdx", StringComparison.Ordinal))
            {
                var decoded = Uri.UnescapeDataString(text);
                var body = decoded.Contains("bad.txt", StringComparison.Ordinal)
                    ? """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000101000000","http://example.com/bad.txt","text/plain","200","BAD","3",null]]
                      """
                    : """
                      [["timestamp","original","mimetype","statuscode","digest","length","redirect"],
                      ["20000101000001","http://example.com/good.txt","text/plain","200","GOOD","4",null]]
                      """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            if (text.Contains("bad.txt", StringComparison.Ordinal))
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("temporary replay failure"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("good", Encoding.UTF8, "text/plain")
            });
        }
    }
}
