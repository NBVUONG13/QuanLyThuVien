using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SmartLibrary.Api.Services;

// Step 84: semantic lookup over *public catalog data only*.
// A separate local EMBEDDING model is required. The Qwen3.5 chat model is not an embedder.
// The vector is stored on the EXISTING public.book_metadata row (no new database/table).
// Automatic incremental indexing runs in a hosted service (also supports SQL imports).
public static class SemanticBookEndpoints
{
    private const int Dimensions = 1024; // Qwen3-Embedding-0.6B, default full vector.
    private static readonly HttpClient EmbeddingHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly SemaphoreSlim IndexGate = new(1, 1);

    public sealed record SemanticMatch(long BookId, double Similarity);
    private sealed record Document(long Id, string Text, string Hash, string? OldHash,
        string? OldModel, bool HasEmbedding);

    public static void MapSemanticBookEndpoints(this WebApplication app)
    {
        // Read-only, session-authorized, catalog-only search. No operational/reader data.
        app.MapGet("/api/library/semantic-books/search", async (
            string? q, HttpRequest req, SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role is not ("Reader" or "Staff" or "Admin"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(q) || q.Length is < 3 or > 400)
                return Results.BadRequest(new { message = "Nhập mô tả sách từ 3 đến 400 ký tự." });
            var hits = await TrySearchAsync(cfg, q.Trim(), 8, ct);
            if (hits is null)
                return Results.Json(new { message = "Tìm kiếm ngữ nghĩa chưa sẵn sàng: kiểm tra migration, LMStudio:EmbeddingModel và Local Server." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new { mode = "semantic", matches = hits.Select(h => new { bookId = h.BookId, similarity = Math.Round(h.Similarity, 4) }) });
        });

        // Optional Admin maintenance tool. Normal create/edit/import flows are indexed
        // automatically by BookEmbeddingAutoIndexer; running a script is not required.
        app.MapPost("/api/admin/semantic-books/reindex", async (
            HttpRequest req, SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Admin") return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(cfg["LMStudio:EmbeddingModel"]))
                return Results.BadRequest(new { message = "Thiếu LMStudio:EmbeddingModel." });
            if (!TryLocalHost(cfg, out _))
                return Results.BadRequest(new { message = "LMStudio:BaseUrl phải là địa chỉ loopback (127.0.0.1/localhost)." });
            try
            {
                IndexStats stats = await IndexChangedAsync(cfg, int.MaxValue, ct);
                if (stats.Status == "busy")
                    return Results.Conflict(new { message = "Đang tự động lập chỉ mục. Có thể thử lại sau." });
                return Results.Ok(new
                {
                    scanned = stats.Scanned,
                    indexed = stats.Indexed,
                    unchanged = stats.Unchanged,
                    failed = stats.Failed,
                    pending = stats.Pending,
                    model = stats.Model,
                    message = stats.Failed == 0 ? "Lập chỉ mục hoàn tất." :
                        "Một số sách chưa có vector; tiến trình tự động sẽ thử lại khi LM Studio sẵn sàng."
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                app.Logger.LogError("Step84 indexing failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Lỗi lập chỉ mục. Kiểm tra Aiven và LM Studio trong backend log." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    public sealed record IndexStats(int Scanned, int Indexed, int Unchanged,
        int Failed, int Pending, string Model, string Status);

    // Shared by background auto indexer and the Admin maintenance endpoint.
    // Recomputes a SHA256 fingerprint from public book content on each pass.
    // Unchanged embeddings are NOT regenerated.
    public static async Task<IndexStats> IndexChangedAsync(
        IConfiguration cfg, int maxChanges, CancellationToken ct)
    {
        string model = (cfg["LMStudio:EmbeddingModel"] ?? "").Trim();
        if (string.IsNullOrEmpty(model) || !TryLocalHost(cfg, out string host))
            return new IndexStats(0, 0, 0, 0, 0, model, "not_configured");
        if (!await IndexGate.WaitAsync(0, ct))
            return new IndexStats(0, 0, 0, 0, 0, model, "busy");
        try
        {
            string cs = cfg.GetConnectionString("AivenPostgres") ??
                throw new InvalidOperationException("AivenPostgres chưa được cấu hình.");
            await using var db = new NpgsqlConnection(cs);
            await db.OpenAsync(ct);
            var docs = new List<Document>();
            await using (var cmd = new NpgsqlCommand(@"
                SELECT b.book_id, COALESCE(b.title,''), COALESCE(b.description,''),
                    COALESCE(m.summary,''), COALESCE(m.keywords,''), COALESCE(m.themes,''),
                    COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                        FROM public.book_authors ba JOIN public.authors a ON a.author_id=ba.author_id
                        WHERE ba.book_id=b.book_id),''),
                    COALESCE((SELECT string_agg(g.name, ', ' ORDER BY g.name)
                        FROM public.book_categories bc JOIN public.categories g ON g.category_id=bc.category_id
                        WHERE bc.book_id=b.book_id),''),
                    COALESCE(b.isbn,''), COALESCE(p.name,''), COALESCE(b.publication_year,0),
                    m.embedding_source_hash, m.embedding_model,
                    (m.embedding IS NOT NULL) AS has_vector
                FROM public.books b
                LEFT JOIN public.book_metadata m ON m.book_id=b.book_id
                LEFT JOIN public.publishers p ON p.publisher_id=b.publisher_id
                WHERE b.is_visible=TRUE ORDER BY b.book_id;", db))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    // Put identifying context before the description; trim long
                    // descriptions so authors and categories never disappear.
                    string bookText = "Tên sách: " + rd.GetString(1) +
                        "\nISBN: " + rd.GetString(8) +
                        "\nTác giả: " + rd.GetString(6) +
                        "\nThể loại: " + rd.GetString(7) +
                        "\nNhà xuất bản: " + rd.GetString(9) +
                        "\nNăm xuất bản: " + rd.GetInt32(10) +
                        "\nMô tả: " + Limit(rd.GetString(2), 2200) +
                        "\nTóm tắt: " + Limit(rd.GetString(3), 850) +
                        "\nTừ khóa: " + Limit(rd.GetString(4), 450) +
                        "\nChủ đề: " + Limit(rd.GetString(5), 450);
                    if (bookText.Length > 5000) bookText = bookText[..5000];
                    // Fingerprint the FULL source fields, including text past the
                    // embedding input limit. Any meaningful book edit is detected.
                    string sourceForHash = string.Join("\u001F", new[] {
                        rd.GetString(1), rd.GetString(8), rd.GetString(6), rd.GetString(7),
                        rd.GetString(9), rd.GetInt32(10).ToString(CultureInfo.InvariantCulture),
                        rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5)
                    });
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceForHash)));
                    docs.Add(new Document(rd.GetInt64(0), bookText, hash,
                        rd.IsDBNull(11) ? null : rd.GetString(11),
                        rd.IsDBNull(12) ? null : rd.GetString(12), rd.GetBoolean(13)));
                }
            }
            var changed = docs.Where(d => !d.HasEmbedding || d.Hash != d.OldHash || model != d.OldModel).ToList();
            int unchanged = docs.Count - changed.Count;
            int indexed = 0, failed = 0;
            foreach (Document doc in changed.Take(Math.Max(0, maxChanges)))
            {
                ct.ThrowIfCancellationRequested();
                var vector = await RequestVectorAsync(host, model, doc.Text, ct);
                if (vector is null)
                {
                    // Avoid hammering an offline LM Studio every time a batch starts.
                    failed++;
                    break;
                }
                await using var save = new NpgsqlCommand(@"
                    INSERT INTO public.book_metadata
                        (book_id, embedding_model, embedding_source_hash, embedding, updated_at)
                    VALUES (@book, @model, @hash, CAST(@vec AS vector(1024)), now())
                    ON CONFLICT (book_id) DO UPDATE SET
                        embedding_model = EXCLUDED.embedding_model,
                        embedding_source_hash = EXCLUDED.embedding_source_hash,
                        embedding = EXCLUDED.embedding,
                        updated_at = now();", db);
                save.Parameters.AddWithValue("book", doc.Id);
                save.Parameters.AddWithValue("model", model);
                save.Parameters.AddWithValue("hash", doc.Hash);
                save.Parameters.AddWithValue("vec", VectorLiteral(vector));
                await save.ExecuteNonQueryAsync(ct);
                indexed++;
            }
            int pending = Math.Max(0, changed.Count - indexed);
            return new IndexStats(docs.Count, indexed, unchanged, failed, pending, model,
                failed > 0 ? "embedding_unavailable" : "ok");
        }
        finally { IndexGate.Release(); }
    }

    private static string Limit(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] : text;

    // Returns null if semantic infrastructure is unavailable; chatbot falls back
    // to its original lexical method without claiming it ran vector search.
    public static async Task<IReadOnlyList<SemanticMatch>?> TrySearchAsync(
        IConfiguration cfg, string query, int limit, CancellationToken ct)
    {
        string? model = cfg["LMStudio:EmbeddingModel"]?.Trim();
        if (string.IsNullOrWhiteSpace(model) || !TryLocalHost(cfg, out var host)) return null;
        try
        {
            var vector = await RequestVectorAsync(host, model, QueryInstruction(query), ct);
            if (vector is null) return null;
            string cs = cfg.GetConnectionString("AivenPostgres") ?? throw new InvalidOperationException();
            await using var db = new NpgsqlConnection(cs);
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                SELECT b.book_id,
                    (1.0 - (m.embedding <=> CAST(@vec AS vector(1024))))::double precision AS similarity
                FROM public.book_metadata m
                JOIN public.books b ON b.book_id=m.book_id
                WHERE b.is_visible=TRUE AND m.embedding IS NOT NULL AND m.embedding_model=@model
                ORDER BY m.embedding <=> CAST(@vec AS vector(1024)), b.book_id
                LIMIT @limit;", db);
            cmd.Parameters.AddWithValue("vec", VectorLiteral(vector));
            cmd.Parameters.AddWithValue("model", model);
            cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 20));
            var hits = new List<SemanticMatch>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                double similarity = rd.GetDouble(1);
                if (double.IsFinite(similarity) && similarity >= 0.35)
                    hits.Add(new SemanticMatch(rd.GetInt64(0), similarity));
            }
            return hits;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // Semantic model/column offline: lexical fallback.
    }

    private static string QueryInstruction(string query) =>
        "Instruct: Retrieve library books whose themes, story, genre or subject match the Vietnamese or English query.\nQuery: " + query;

    private static bool TryLocalHost(IConfiguration cfg, out string host)
    {
        host = "";
        string value = cfg["LMStudio:BaseUrl"] ?? "http://127.0.0.1:1234";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var u) ||
            u.Scheme != Uri.UriSchemeHttp ||
            u.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            u.Port is < 1 or > 65535 || u.AbsolutePath.Trim('/') != "" ||
            u.Query.Length > 0 || u.Fragment.Length > 0) return false;
        host = u.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static async Task<float[]?> RequestVectorAsync(
        string host, string model, string input, CancellationToken ct)
    {
        try
        {
            using var response = await EmbeddingHttp.PostAsJsonAsync(host + "/v1/embeddings",
                new { model, input }, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            var arr = data[0].GetProperty("embedding");
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != Dimensions) return null;
            var vector = new float[Dimensions];
            int i = 0;
            foreach (var value in arr.EnumerateArray())
            {
                vector[i] = value.GetSingle();
                if (!float.IsFinite(vector[i])) return null;
                i++;
            }
            return vector;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static string VectorLiteral(float[] values) =>
        "[" + string.Join(",", values.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static string? Bearer(HttpRequest req)
    {
        string value = req.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }
}
