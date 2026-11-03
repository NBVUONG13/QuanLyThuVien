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
            if (!app.Environment.IsDevelopment()) return Results.NotFound();
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

        // Manual indexing; no automatic migrations, no endless background loop.
        app.MapPost("/api/admin/semantic-books/reindex", async (
            HttpRequest req, SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!app.Environment.IsDevelopment()) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Admin") return Results.StatusCode(StatusCodes.Status403Forbidden);
            string? model = cfg["LMStudio:EmbeddingModel"]?.Trim();
            if (string.IsNullOrEmpty(model))
                return Results.BadRequest(new { message = "Thiếu LMStudio:EmbeddingModel (ID model embedding trong LM Studio)." });
            if (!TryLocalHost(cfg, out string host))
                return Results.BadRequest(new { message = "LMStudio:BaseUrl chỉ được trỏ tới 127.0.0.1/localhost." });
            if (!await IndexGate.WaitAsync(0, ct))
                return Results.Conflict(new { message = "Đã có một lượt lập chỉ mục đang chạy." });
            try
            {
                string cs = cfg.GetConnectionString("AivenPostgres") ??
                    throw new InvalidOperationException("AivenPostgres chưa được cấu hình.");
                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                var docs = new List<Document>();
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT b.book_id, b.title, COALESCE(b.description,''),
                        COALESCE(m.summary,''), COALESCE(m.keywords,''), COALESCE(m.themes,''),
                        COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                            FROM public.book_authors ba JOIN public.authors a ON a.author_id=ba.author_id
                            WHERE ba.book_id=b.book_id),''),
                        COALESCE((SELECT string_agg(g.name, ', ' ORDER BY g.name)
                            FROM public.book_categories bc JOIN public.categories g ON g.category_id=bc.category_id
                            WHERE bc.book_id=b.book_id),''),
                        m.embedding_source_hash, m.embedding_model,
                        (m.embedding IS NOT NULL) AS has_vector
                    FROM public.books b
                    LEFT JOIN public.book_metadata m ON m.book_id=b.book_id
                    WHERE b.is_visible=TRUE ORDER BY b.book_id LIMIT 500;", db))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await rd.ReadAsync(ct))
                    {
                        string bookText = "Tên sách: " + rd.GetString(1) + "\nMô tả: " + rd.GetString(2) +
                            "\nTóm tắt: " + rd.GetString(3) + "\nTừ khóa: " + rd.GetString(4) +
                            "\nChủ đề: " + rd.GetString(5) + "\nTác giả: " + rd.GetString(6) +
                            "\nThể loại: " + rd.GetString(7);
                        bookText = bookText.Length > 4000 ? bookText[..4000] : bookText;
                        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bookText)));
                        docs.Add(new Document(rd.GetInt64(0), bookText, hash,
                            rd.IsDBNull(8) ? null : rd.GetString(8),
                            rd.IsDBNull(9) ? null : rd.GetString(9), rd.GetBoolean(10)));
                    }
                }
                int indexed = 0, unchanged = 0, failed = 0;
                // Embed only changed content. Long-running setup is explicitly initiated by Admin.
                foreach (var doc in docs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (doc.HasEmbedding && doc.Hash == doc.OldHash && model == doc.OldModel)
                    { unchanged++; continue; }
                    var vector = await RequestVectorAsync(host, model, doc.Text, ct);
                    if (vector is null) { failed++; continue; }
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
                return Results.Ok(new
                {
                    scanned = docs.Count,
                    indexed,
                    unchanged,
                    failed,
                    model,
                    message = failed == 0 ? "Lập chỉ mục hoàn tất." :
                        "Một số sách không tạo được embedding. Kiểm tra LM Studio/Developer Logs."
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Step84 indexing failed while reindexing book embeddings.");
                return Results.Json(new { message = "Không lập được chỉ mục. Kiểm tra SQL migration, kết nối Aiven và Local Server; xem backend log." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally { IndexGate.Release(); }
        });
    }

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
