using OpenGameBuilder.Mgb.Archive.S3Redactor;

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

app.MapGet("/api/state", (int? index, RedactorRuntime runtime) =>
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
            null,
            null,
            null,
            Array.Empty<ReviewCandidateDto>()));
    }

    return Results.Ok(runtime.ReviewStore.GetStateDto(runtime.Options, index));
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

app.MapPost("/api/review-batch", (ReviewBatchRequest request, RedactorRuntime runtime) =>
{
    if (!runtime.IsReady)
    {
        return Results.BadRequest(runtime.Message);
    }

    runtime.ReviewStore.ApplyBatch(request.Decisions, request.CurrentIndex);
    return Results.Ok(runtime.ReviewStore.GetStateDto(runtime.Options, request.CurrentIndex));
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
            const imageIds = ['original', 'double', 'inspect'];
            const maxCachedImages = 12;
            const preloadAheadCount = 12;
            const fetchAheadLowWatermark = 20;
            const flushDelayMs = 75;
            const imageCache = new Map();
            const candidatesByIndex = new Map();
            const localDecisionStatusByEntryId = new Map();
            let state = null;
            let currentIndex = 0;
            let lastEntryId = null;
            let refreshSequence = 0;
            let windowFetchSequence = 0;
            let pendingDecisions = [];
            let dirtyCursor = false;
            let flushTimer = null;
            let flushInFlight = false;
            let submitInFlight = false;
            let flushWaiters = [];

            async function fetchJson(url, options) {
              const response = await fetch(url, options);
              if (!response.ok) {
                throw new Error(await response.text());
              }

              return await response.json();
            }

            async function refresh(force = false) {
              const sequence = ++refreshSequence;
              try {
                const nextState = await fetchJson(stateUrl(state ? currentIndex : null));
                if (sequence !== refreshSequence) return;
                applyServerState(nextState, { preserveCursor: Boolean(state) });
              } catch (error) {
                console.error('Failed to refresh redactor state.', error);
              }
            }

            function stateUrl(index) {
              return index === null || index === undefined
                ? '/api/state'
                : '/api/state?index=' + encodeURIComponent(index);
            }

            function applyServerState(nextState, options = {}) {
              const hadPendingDecisions = hasPendingDecisionWork();
              const previousCounts = state && state.counts;
              state = nextState;
              mergeCandidatesFromState(nextState);

              if (!options.preserveCursor) {
                currentIndex = nextState.currentIndex;
              }

              currentIndex = clampIndex(currentIndex);
              state.currentIndex = currentIndex;
              if (previousCounts && hadPendingDecisions) {
                state.counts = mergePendingCounts(previousCounts, nextState.counts);
              }

              updateStateNeighborsFromCache();
              preloadAroundCurrent();
              render();
              maybeFetchMoreCandidates();
            }

            function mergePendingCounts(localCounts, serverCounts) {
              const total = Math.max(localCounts.total, serverCounts.total);
              const added = Math.max(0, total - localCounts.total);
              return {
                ...localCounts,
                total,
                unreviewed: localCounts.unreviewed + added
              };
            }

            function mergeCandidatesFromState(nextState) {
              for (const candidate of [nextState.previous, nextState.current, nextState.next]) {
                mergeCandidate(candidate);
              }

              for (const candidate of nextState.reviewWindow || []) {
                mergeCandidate(candidate);
              }
            }

            function mergeCandidate(candidate) {
              if (!candidate) return;

              const localStatus = localDecisionStatusByEntryId.get(candidate.entryId);
              setCandidate(candidate.ordinal - 1, localStatus ? {...candidate, status: localStatus} : candidate);
            }

            function setCandidate(index, candidate) {
              candidatesByIndex.set(index, candidate);
            }

            function candidateAt(index) {
              return candidatesByIndex.get(index) || null;
            }

            function currentCandidate() {
              return candidateAt(currentIndex) || null;
            }

            function updateStateNeighborsFromCache() {
              if (!state) return;

              state.current = currentCandidate();
              state.previous = currentIndex > 0 ? candidateAt(currentIndex - 1) : null;
              state.next = currentIndex < state.counts.total - 1 ? candidateAt(currentIndex + 1) : null;
            }

            function clampIndex(index) {
              if (!state || state.counts.total <= 0) return 0;
              return Math.max(0, Math.min(state.counts.total - 1, index));
            }

            function preloadCandidate(candidate) {
              if (!candidate || imageCache.has(candidate.entryId)) return;

              const image = new Image();
              image.decoding = 'async';
              image.src = candidate.imageUrl;
              imageCache.set(candidate.entryId, image);
              while (imageCache.size > maxCachedImages) {
                imageCache.delete(imageCache.keys().next().value);
              }
            }

            function preloadAroundCurrent() {
              for (let index = Math.max(0, currentIndex - 1); index <= currentIndex + preloadAheadCount; index++) {
                preloadCandidate(candidateAt(index));
              }
            }

            function highestLoadedIndex() {
              let highest = -1;
              for (const index of candidatesByIndex.keys()) {
                highest = Math.max(highest, index);
              }

              return highest;
            }

            async function maybeFetchMoreCandidates() {
              if (!state || state.counts.total <= 0) return;
              if (highestLoadedIndex() - currentIndex > fetchAheadLowWatermark) return;
              if (highestLoadedIndex() >= state.counts.total - 1) return;

              const sequence = ++windowFetchSequence;
              try {
                const nextState = await fetchJson(stateUrl(currentIndex));
                if (sequence !== windowFetchSequence) return;
                applyServerState(nextState, { preserveCursor: true });
              } catch (error) {
                console.error('Failed to fetch more candidates.', error);
              }
            }

            function render() {
              if (!state) return;

              document.getElementById('archive').textContent = state.archivePath || '';
              document.getElementById('review').textContent = state.reviewPath || '';
              const output = document.getElementById('output');
              if (document.activeElement !== output) {
                output.value = state.outputPath || '';
              }

              document.getElementById('total').textContent = state.counts.total;
              document.getElementById('reviewed').textContent = state.counts.reviewed;
              document.getElementById('approved').textContent = state.counts.approved;
              document.getElementById('redacted').textContent = state.counts.redacted;
              document.getElementById('unreviewed').textContent = state.counts.unreviewed;
              document.getElementById('processed').textContent = state.scan.processed;
              document.getElementById('scanTotal').textContent = state.scan.total;

              const message = document.getElementById('message');
              const images = document.getElementById('images');
              updateStateNeighborsFromCache();
              const current = currentCandidate();
              if (!state.ready || !current) {
                message.hidden = false;
                images.hidden = true;
                lastEntryId = null;
                message.textContent = state.message || (state.counts.total > 0 ? 'Loading image metadata...' : (state.scan.complete ? 'No images matched the review filter.' : 'Scanning PNG entries...'));
                document.getElementById('position').textContent = '';
                setDisabled(true);
                document.getElementById('submit').disabled = true;
                maybeFetchMoreCandidates();
                return;
              }

              message.hidden = true;
              images.hidden = false;
              document.getElementById('position').textContent = (currentIndex + 1) + ' / ' + state.counts.total;
              if (lastEntryId !== current.entryId) {
                lastEntryId = current.entryId;
                document.getElementById('original').style.width = current.width + 'px';
                document.getElementById('original').style.height = current.height + 'px';
                document.getElementById('double').style.width = (current.width * 2) + 'px';
                document.getElementById('double').style.height = (current.height * 2) + 'px';
                document.getElementById('inspect').style.width = '512px';
                document.getElementById('inspect').style.height = '512px';
                document.getElementById('originalLabel').textContent = current.width + ' x ' + current.height;
                document.getElementById('doubleLabel').textContent = (current.width * 2) + ' x ' + (current.height * 2);

                for (const id of imageIds) {
                  document.getElementById(id).src = current.imageUrl;
                }
              }

              document.getElementById('key').textContent = current.keyText;
              document.getElementById('piece').textContent = [current.userName, current.projectName, current.pieceType, current.pieceName].filter(Boolean).join(' / ');
              document.getElementById('colors').textContent = current.uniqueColorCount;

              const status = document.getElementById('status');
              status.textContent = current.status;
              status.className = 'pill ' + current.status;
              document.getElementById('prev').disabled = submitInFlight || currentIndex <= 0 || !candidateAt(currentIndex - 1);
              document.getElementById('next').disabled = submitInFlight || currentIndex >= state.counts.total - 1 || !candidateAt(currentIndex + 1);
              document.getElementById('approve').disabled = submitInFlight;
              document.getElementById('redact').disabled = submitInFlight;
              document.getElementById('submit').disabled = submitInFlight || !state.scan.complete || state.counts.total === 0 || state.counts.unreviewed !== 0;
            }

            function setDisabled(value) {
              for (const id of ['prev', 'next', 'approve', 'redact', 'submit']) {
                document.getElementById(id).disabled = value;
              }
            }

            function updateCountsForDecision(counts, oldStatus, newStatus) {
              const next = {...counts};
              if (oldStatus === newStatus) return next;

              if (oldStatus === 'unreviewed') {
                next.unreviewed = Math.max(0, next.unreviewed - 1);
                next.reviewed += 1;
              } else if (oldStatus === 'approved') {
                next.approved = Math.max(0, next.approved - 1);
              } else if (oldStatus === 'redacted') {
                next.redacted = Math.max(0, next.redacted - 1);
              }

              if (newStatus === 'approved') {
                next.approved += 1;
              } else if (newStatus === 'redacted') {
                next.redacted += 1;
              }

              return next;
            }

            function hasPendingDecisionWork() {
              return pendingDecisions.length > 0 || flushInFlight;
            }

            function enqueueDecision(entryId, status) {
              pendingDecisions.push({ entryId, status });
              localDecisionStatusByEntryId.set(entryId, status);
              scheduleFlush();
            }

            function markCursorDirty() {
              dirtyCursor = true;
              scheduleFlush();
            }

            function scheduleFlush(delay = flushDelayMs) {
              if (flushTimer) {
                clearTimeout(flushTimer);
              }

              flushTimer = setTimeout(() => {
                flushTimer = null;
                flushReview();
              }, delay);
            }

            async function flushReview() {
              if (flushInFlight) return;
              if (pendingDecisions.length === 0 && !dirtyCursor) {
                notifyFlushWaiters();
                return;
              }

              const decisions = pendingDecisions;
              const cursor = currentIndex;
              pendingDecisions = [];
              dirtyCursor = false;
              flushInFlight = true;
              try {
                const nextState = await fetchJson('/api/review-batch', {
                  method: 'POST',
                  headers: {'Content-Type': 'application/json'},
                  body: JSON.stringify({ decisions, currentIndex: cursor })
                });
                clearAcknowledgedDecisions(decisions);
                applyServerState(nextState, { preserveCursor: true });
              } catch (error) {
                console.error('Failed to persist review changes.', error);
                pendingDecisions = decisions.concat(pendingDecisions);
                dirtyCursor = true;
                scheduleFlush(1000);
              } finally {
                flushInFlight = false;
                if (pendingDecisions.length > 0 || dirtyCursor) {
                  scheduleFlush(0);
                } else {
                  notifyFlushWaiters();
                  refresh(true);
                }
              }
            }

            function clearAcknowledgedDecisions(decisions) {
              const sent = new Map();
              for (const decision of decisions) {
                sent.set(decision.entryId, decision.status);
              }

              const stillPending = new Set(pendingDecisions.map(decision => decision.entryId));
              for (const [entryId, status] of sent) {
                if (!stillPending.has(entryId) && localDecisionStatusByEntryId.get(entryId) === status) {
                  localDecisionStatusByEntryId.delete(entryId);
                }
              }
            }

            function notifyFlushWaiters() {
              const waiters = flushWaiters;
              flushWaiters = [];
              for (const resolve of waiters) {
                resolve();
              }
            }

            async function flushAll() {
              if (flushTimer) {
                clearTimeout(flushTimer);
                flushTimer = null;
              }

              while (pendingDecisions.length > 0 || dirtyCursor || flushInFlight) {
                if (flushInFlight) {
                  await new Promise(resolve => flushWaiters.push(resolve));
                } else {
                  await flushReview();
                }
              }
            }

            function decide(status) {
              if (!state) return;

              const current = currentCandidate();
              if (!current || submitInFlight) return;

              setCandidate(currentIndex, {...current, status});
              state.counts = updateCountsForDecision(state.counts, current.status, status);
              enqueueDecision(current.entryId, status);
              if (currentIndex < state.counts.total - 1 && candidateAt(currentIndex + 1)) {
                currentIndex++;
              } else if (currentIndex < state.counts.total - 1) {
                maybeFetchMoreCandidates();
              }

              markCursorDirty();
              updateStateNeighborsFromCache();
              preloadAroundCurrent();
              render();
              maybeFetchMoreCandidates();
            }

            function move(delta) {
              if (!state || submitInFlight) return;

              const nextIndex = currentIndex + delta;
              if (nextIndex < 0 || nextIndex >= state.counts.total) return;
              if (!candidateAt(nextIndex)) {
                maybeFetchMoreCandidates();
                return;
              }

              currentIndex = nextIndex;
              markCursorDirty();
              updateStateNeighborsFromCache();
              preloadAroundCurrent();
              render();
              maybeFetchMoreCandidates();
            }

            async function submitReview() {
              const resultBox = document.getElementById('submitResult');
              submitInFlight = true;
              render();
              try {
                resultBox.textContent = 'Saving review...';
                await flushAll();
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
              } finally {
                submitInFlight = false;
                render();
              }
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
