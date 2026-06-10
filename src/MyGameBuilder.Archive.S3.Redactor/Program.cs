using MyGameBuilder.Archive.S3.Redactor;

var options = RedactorOptions.Parse(args);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(options);

RedactorRuntime runtime;
if (string.IsNullOrWhiteSpace(options.ArchivePath))
{
    runtime = RedactorRuntime.NotReady(options, "Start with --archive <path-to-archive.sqlite>.");
}
else if (!File.Exists(options.ArchivePath))
{
    runtime = RedactorRuntime.NotReady(options, $"Archive database was not found: {Path.GetFullPath(options.ArchivePath)}");
}
else
{
    var archive = new ArchiveDb(options.ArchivePath);
    var reviewStore = new ReviewStore(options.EffectiveReviewPath);
    reviewStore.Initialize(options.ArchivePath, options.UniqueColorThreshold);
    reviewStore.SetScanTotal(archive.CountManualReviewPngEntries());
    var propagation = new ScreenshotPropagation(archive);
    runtime = RedactorRuntime.Ready(options, archive, reviewStore, new RedactionSubmitter(archive, reviewStore, propagation));
}

builder.Services.AddSingleton(runtime);
builder.Services.AddHostedService<CandidateDiscoveryWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Content(RedactorPage.Html, "text/html"));

app.MapGet("/api/state", (RedactorRuntime runtime) =>
{
    if (!runtime.IsReady)
    {
        return Results.Ok(new ReviewStateDto(
            false,
            runtime.Message,
            runtime.Options.ArchivePath ?? string.Empty,
            runtime.Options.EffectiveReviewPath,
            runtime.Options.EffectiveOutputPath,
            runtime.Options.UniqueColorThreshold,
            new ScanProgress(0, 0, Complete: false),
            0,
            new ReviewCounts(0, 0, 0, 0, 0),
            null));
    }

    return Results.Ok(runtime.ReviewStore.GetStateDto(runtime.Options));
});

app.MapPost("/api/decision", (DecisionRequest request, RedactorRuntime runtime) =>
{
    if (!runtime.IsReady)
    {
        return Results.BadRequest(runtime.Message);
    }

    runtime.ReviewStore.SetDecision(request.EntryId, request.Status);
    return Results.Ok(runtime.ReviewStore.GetStateDto(runtime.Options));
});

app.MapPost("/api/move", (MoveRequest request, RedactorRuntime runtime) =>
{
    if (!runtime.IsReady)
    {
        return Results.BadRequest(runtime.Message);
    }

    runtime.ReviewStore.Move(request.Delta);
    return Results.Ok(runtime.ReviewStore.GetStateDto(runtime.Options));
});

app.MapGet("/image/{entryId:long}", (long entryId, RedactorRuntime runtime) =>
{
    if (!runtime.IsReady || runtime.ReviewStore.GetCandidate(entryId) is null)
    {
        return Results.NotFound();
    }

    return Results.File(runtime.Archive.GetBody(entryId), "image/png");
});

app.MapPost("/api/submit", (SubmitRequest request, RedactorRuntime runtime) =>
{
    if (!runtime.IsReady)
    {
        return Results.BadRequest(runtime.Message);
    }

    var outputPath = string.IsNullOrWhiteSpace(request.OutputPath)
        ? runtime.Options.EffectiveOutputPath
        : request.OutputPath;
    return Results.Ok(runtime.Submitter.Submit(outputPath, runtime.Options.UniqueColorThreshold));
});

app.Run();

public sealed record RedactorRuntime(
    bool IsReady,
    string? Message,
    RedactorOptions Options,
    ArchiveDb Archive,
    ReviewStore ReviewStore,
    RedactionSubmitter Submitter)
{
    public static RedactorRuntime NotReady(RedactorOptions options, string message) =>
        new(false, message, options, null!, null!, null!);

    public static RedactorRuntime Ready(RedactorOptions options, ArchiveDb archive, ReviewStore reviewStore, RedactionSubmitter submitter) =>
        new(true, null, options, archive, reviewStore, submitter);
}

public static class RedactorPage
{
    public const string Html =
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>MyGameBuilder S3 Redactor</title>
          <style>
            :root {
              color-scheme: light;
              --ink: #111827;
              --muted: #5b6472;
              --line: #d5dbe3;
              --surface: #f7f8fa;
              --approved: #0f766e;
              --redacted: #b42318;
              --pending: #7a4f01;
              --action: #2457a6;
            }
            * { box-sizing: border-box; }
            body {
              margin: 0;
              font-family: Segoe UI, system-ui, -apple-system, sans-serif;
              color: var(--ink);
              background: #ffffff;
            }
            header {
              display: grid;
              grid-template-columns: 1fr auto;
              gap: 16px;
              align-items: center;
              padding: 14px 18px;
              border-bottom: 1px solid var(--line);
              background: var(--surface);
            }
            h1 {
              margin: 0;
              font-size: 18px;
              font-weight: 650;
            }
            main {
              display: grid;
              grid-template-columns: minmax(260px, 1fr) 340px;
              min-height: calc(100vh - 65px);
            }
            .viewer {
              padding: 18px;
              overflow: auto;
            }
            .images {
              display: grid;
              grid-template-columns: repeat(3, minmax(180px, max-content));
              gap: 22px;
              align-items: start;
            }
            figure {
              margin: 0;
              display: grid;
              gap: 8px;
              justify-items: start;
            }
            figcaption {
              color: var(--muted);
              font-size: 13px;
            }
            .preview {
              border: 1px solid var(--line);
              background-color: #e9edf2;
              background-image:
                linear-gradient(45deg, #cbd3dd 25%, transparent 25%),
                linear-gradient(-45deg, #cbd3dd 25%, transparent 25%),
                linear-gradient(45deg, transparent 75%, #cbd3dd 75%),
                linear-gradient(-45deg, transparent 75%, #cbd3dd 75%);
              background-size: 20px 20px;
              background-position: 0 0, 0 10px, 10px -10px, -10px 0;
              image-rendering: pixelated;
            }
            .side {
              border-left: 1px solid var(--line);
              padding: 18px;
              display: grid;
              align-content: start;
              gap: 16px;
              background: #fbfcfd;
            }
            .status {
              font-size: 13px;
              color: var(--muted);
              word-break: break-word;
            }
            .pill {
              display: inline-flex;
              align-items: center;
              min-height: 28px;
              padding: 4px 9px;
              border: 1px solid var(--line);
              border-radius: 6px;
              font-size: 13px;
              font-weight: 650;
              background: #fff;
            }
            .pill.approved { color: var(--approved); border-color: #7bc4b9; }
            .pill.redacted { color: var(--redacted); border-color: #f0a39c; }
            .pill.unreviewed { color: var(--pending); border-color: #ddb768; }
            .counts {
              display: grid;
              grid-template-columns: 1fr 1fr;
              gap: 8px;
            }
            .metric {
              border: 1px solid var(--line);
              border-radius: 8px;
              padding: 10px;
              background: #fff;
            }
            .metric strong {
              display: block;
              font-size: 22px;
              line-height: 1.1;
            }
            .metric span {
              color: var(--muted);
              font-size: 12px;
            }
            .actions {
              display: grid;
              grid-template-columns: 1fr 1fr;
              gap: 8px;
            }
            button {
              min-height: 42px;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: #fff;
              color: var(--ink);
              font: inherit;
              font-weight: 650;
              cursor: pointer;
            }
            button.primary { background: var(--action); color: #fff; border-color: var(--action); }
            button.danger { background: var(--redacted); color: #fff; border-color: var(--redacted); }
            button:disabled { opacity: .48; cursor: not-allowed; }
            .wide { grid-column: 1 / -1; }
            dl { display: grid; grid-template-columns: auto 1fr; gap: 8px 12px; margin: 0; font-size: 13px; }
            dt { color: var(--muted); }
            dd { margin: 0; word-break: break-word; }
            input {
              width: 100%;
              min-height: 36px;
              border: 1px solid var(--line);
              border-radius: 6px;
              padding: 6px 8px;
              font: inherit;
              font-size: 13px;
            }
            .empty {
              padding: 24px;
              color: var(--muted);
            }
            @media (max-width: 980px) {
              main { grid-template-columns: 1fr; }
              .side { border-left: 0; border-top: 1px solid var(--line); }
              .images { grid-template-columns: 1fr; }
            }
          </style>
        </head>
        <body>
          <header>
            <h1>MyGameBuilder S3 Redactor</h1>
            <div id="position" class="status"></div>
          </header>
          <main>
            <section class="viewer">
              <div id="message" class="empty"></div>
              <div id="images" class="images" hidden>
                <figure>
                  <img id="original" class="preview" alt="">
                  <figcaption id="originalLabel"></figcaption>
                </figure>
                <figure>
                  <img id="double" class="preview" alt="">
                  <figcaption id="doubleLabel"></figcaption>
                </figure>
                <figure>
                  <img id="inspect" class="preview" alt="">
                  <figcaption>512 x 512</figcaption>
                </figure>
              </div>
            </section>
            <aside class="side">
              <div id="status" class="pill unreviewed">unreviewed</div>
              <div class="counts">
                <div class="metric"><strong id="total">0</strong><span>Total</span></div>
                <div class="metric"><strong id="reviewed">0</strong><span>Reviewed</span></div>
                <div class="metric"><strong id="approved">0</strong><span>Approved</span></div>
                <div class="metric"><strong id="redacted">0</strong><span>Redacted</span></div>
                <div class="metric"><strong id="unreviewed">0</strong><span>Unreviewed</span></div>
                <div class="metric"><strong id="processed">0</strong><span>Processed</span></div>
                <div class="metric"><strong id="scanTotal">0</strong><span>PNG Source</span></div>
              </div>
              <div class="actions">
                <button id="prev" title="Previous image">Previous</button>
                <button id="next" title="Next image">Next</button>
                <button id="approve" class="primary" title="Approve current image">Approve</button>
                <button id="redact" class="danger" title="Redact current image">Redact</button>
              </div>
              <dl>
                <dt>Key</dt><dd id="key"></dd>
                <dt>Piece</dt><dd id="piece"></dd>
                <dt>Colors</dt><dd id="colors"></dd>
                <dt>Archive</dt><dd id="archive"></dd>
                <dt>Review</dt><dd id="review"></dd>
              </dl>
              <input id="output" aria-label="Output database path">
              <button id="submit" class="primary wide">Submit Review</button>
              <div id="submitResult" class="status"></div>
            </aside>
          </main>
          <script>
            let state = null;
            let lastEntryId = null;
            let refreshing = false;

            async function refresh() {
              if (refreshing) return;
              refreshing = true;
              state = await (await fetch('/api/state')).json();
              refreshing = false;
              render();
            }

            function render() {
              document.getElementById('archive').textContent = state.archivePath || '';
              document.getElementById('review').textContent = state.reviewPath || '';
              document.getElementById('output').value = state.outputPath || '';
              document.getElementById('total').textContent = state.counts.total;
              document.getElementById('reviewed').textContent = state.counts.reviewed;
              document.getElementById('approved').textContent = state.counts.approved;
              document.getElementById('redacted').textContent = state.counts.redacted;
              document.getElementById('unreviewed').textContent = state.counts.unreviewed;
              document.getElementById('processed').textContent = state.scan.processed;
              document.getElementById('scanTotal').textContent = state.scan.total;

              const message = document.getElementById('message');
              const images = document.getElementById('images');
              const current = state.current;
              if (!state.ready || !current) {
                message.hidden = false;
                images.hidden = true;
                message.textContent = state.message || (state.scan.complete ? 'No images matched the review filter.' : 'Scanning PNG entries...');
                document.getElementById('position').textContent = '';
                setDisabled(true);
                document.getElementById('submit').disabled = true;
                return;
              }

              message.hidden = true;
              images.hidden = false;
              setDisabled(false);
              document.getElementById('position').textContent = current.ordinal + ' / ' + state.counts.total;
              if (lastEntryId !== current.entryId) {
                lastEntryId = current.entryId;
                const url = current.imageUrl + '?v=' + Date.now();
                for (const id of ['original', 'double', 'inspect']) {
                  document.getElementById(id).src = url;
                }

                document.getElementById('original').style.width = current.width + 'px';
                document.getElementById('original').style.height = current.height + 'px';
                document.getElementById('double').style.width = (current.width * 2) + 'px';
                document.getElementById('double').style.height = (current.height * 2) + 'px';
                document.getElementById('inspect').style.width = '512px';
                document.getElementById('inspect').style.height = '512px';
                document.getElementById('originalLabel').textContent = current.width + ' x ' + current.height;
                document.getElementById('doubleLabel').textContent = (current.width * 2) + ' x ' + (current.height * 2);
              }

              document.getElementById('key').textContent = current.keyText;
              document.getElementById('piece').textContent = [current.userName, current.projectName, current.pieceType, current.pieceName].filter(Boolean).join(' / ');
              document.getElementById('colors').textContent = current.uniqueColorCount;

              const status = document.getElementById('status');
              status.textContent = current.status;
              status.className = 'pill ' + current.status;
              document.getElementById('prev').disabled = state.currentIndex <= 0;
              document.getElementById('next').disabled = state.currentIndex >= state.counts.total - 1;
              document.getElementById('submit').disabled = !state.scan.complete || state.counts.total === 0 || state.counts.unreviewed !== 0;
            }

            function setDisabled(value) {
              for (const id of ['prev', 'next', 'approve', 'redact', 'submit']) {
                document.getElementById(id).disabled = value;
              }
            }

            async function decide(status) {
              if (!state.current) return;
              state = await (await fetch('/api/decision', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ entryId: state.current.entryId, status })
              })).json();
              render();
            }

            async function move(delta) {
              state = await (await fetch('/api/move', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ delta })
              })).json();
              render();
            }

            async function submitReview() {
              const resultBox = document.getElementById('submitResult');
              resultBox.textContent = 'Submitting...';
              const response = await fetch('/api/submit', {
                method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ outputPath: document.getElementById('output').value })
              });
              if (!response.ok) {
                resultBox.textContent = await response.text();
                return;
              }
              const result = await response.json();
              resultBox.textContent = 'Created ' + result.outputPath + ' with ' + result.totalRedactedCount + ' redacted entries.';
            }

            document.getElementById('approve').addEventListener('click', () => decide('approved'));
            document.getElementById('redact').addEventListener('click', () => decide('redacted'));
            document.getElementById('prev').addEventListener('click', () => move(-1));
            document.getElementById('next').addEventListener('click', () => move(1));
            document.getElementById('submit').addEventListener('click', submitReview);
            document.addEventListener('keydown', event => {
              if (event.target && event.target.tagName === 'INPUT') return;
              if (event.key === 'Enter') {
                event.preventDefault();
                decide('approved');
              } else if (event.key === 'Backspace') {
                event.preventDefault();
                decide('redacted');
              } else if (event.key === 'ArrowLeft') {
                event.preventDefault();
                move(-1);
              } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                move(1);
              }
            });
            refresh();
            setInterval(refresh, 1500);
          </script>
        </body>
        </html>
        """;
}
