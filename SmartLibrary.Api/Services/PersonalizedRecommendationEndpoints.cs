using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

// STEP 83: entirely database-driven personalized suggestions. No model, vector extension,
// migration, or background worker required. Access is always scoped to actor.UserId.
public static class PersonalizedRecommendationEndpoints
{
    private sealed record Candidate(long Id, string Title, string Authors, string Categories,
        long[] AuthorIds, long[] CategoryIds, int Available, int Popularity, int Year,
        string CoverUrl, string CoverObjectKey);

    public static void MapPersonalizedRecommendationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/reader/recommendations", async (
            HttpRequest req, SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            if (!app.Environment.IsDevelopment()) return Results.NotFound();
            var session = sessions.Validate(Bearer(req));
            if (session is null) return Results.Unauthorized();
            if (session.Role != "Reader") return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var cs = config.GetConnectionString("AivenPostgres") ??
                    throw new InvalidOperationException("Missing AivenPostgres connection");
                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                // Snapshot avoids mixing catalog and preferences from different transactions.
                await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", db, tx))
                    await readOnly.ExecuteNonQueryAsync(ct);

                var candidates = new List<Candidate>();
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT b.book_id, b.title,
                      COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                        FROM public.book_authors ba JOIN public.authors a ON a.author_id=ba.author_id
                        WHERE ba.book_id=b.book_id),''),
                      COALESCE((SELECT string_agg(c.name, ', ' ORDER BY c.name)
                        FROM public.book_categories bc JOIN public.categories c ON c.category_id=bc.category_id
                        WHERE bc.book_id=b.book_id),''),
                      ARRAY(SELECT ba.author_id FROM public.book_authors ba WHERE ba.book_id=b.book_id),
                      ARRAY(SELECT bc.category_id FROM public.book_categories bc WHERE bc.book_id=b.book_id),
                      (SELECT COUNT(*)::integer FROM public.book_copies cp
                        WHERE cp.book_id=b.book_id AND cp.condition_status='active'
                        AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                          WHERE d.book_copy_id=cp.copy_id AND d.closed_at IS NULL)),
                      (SELECT COUNT(*)::integer FROM public.borrow_details d
                        JOIN public.book_copies cp ON cp.copy_id=d.book_copy_id
                        JOIN public.borrows br ON br.borrow_id=d.borrow_id
                        WHERE cp.book_id=b.book_id AND br.borrowed_at >= now()-interval '90 days'),
                      COALESCE(b.publication_year,0),
                      COALESCE(b.cover_url,''), COALESCE(b.cover_object_key,'')
                    FROM public.books b WHERE b.is_visible=TRUE
                    ORDER BY b.book_id DESC LIMIT 1500;", db, tx))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) candidates.Add(new Candidate(
                        rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                        rd.GetFieldValue<long[]>(4), rd.GetFieldValue<long[]>(5),
                        rd.GetInt32(6), rd.GetInt32(7), rd.GetInt32(8),
                        rd.GetString(9), rd.GetString(10)));

                // Counts only real issued loans; pending requests do not imply reading preference.
                var history = new Dictionary<long, (int Count, DateTime Latest)>();
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT cp.book_id, COUNT(*)::integer, MAX(br.borrowed_at)
                    FROM public.borrows br
                    JOIN public.borrow_details d ON d.borrow_id=br.borrow_id
                    JOIN public.book_copies cp ON cp.copy_id=d.book_copy_id
                    WHERE br.reader_user_id=@id
                    GROUP BY cp.book_id;", db, tx))
                {
                    cmd.Parameters.AddWithValue("id", session.UserId);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        history[rd.GetInt64(0)] = (rd.GetInt32(1), rd.GetDateTime(2));
                }

                var ratings = new Dictionary<long, int>();
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT book_id, score::integer FROM public.ratings WHERE user_id=@id;", db, tx))
                {
                    cmd.Parameters.AddWithValue("id", session.UserId);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) ratings[rd.GetInt64(0)] = rd.GetInt32(1);
                }
                await tx.CommitAsync(ct);

                // Optional similarity from ALREADY INDEXED books. No LM Studio request is
                // needed when the Reader opens Home. No user/loan data goes to the model.
                var semanticAffinity = new Dictionary<long, double>();
                string? embeddingModel = config["LMStudio:EmbeddingModel"];
                long[] likedIds = history.Keys
                    .Where(id => !ratings.TryGetValue(id, out int score) || score >= 3)
                    .Concat(ratings.Where(r => r.Value >= 4).Select(r => r.Key))
                    .Distinct().Take(20).ToArray();
                if (!string.IsNullOrWhiteSpace(embeddingModel) && likedIds.Length > 0)
                {
                    try
                    {
                        await using var similar = new NpgsqlCommand(@"
                            WITH liked AS (
                                SELECT embedding FROM public.book_metadata
                                WHERE book_id = ANY(@liked) AND embedding IS NOT NULL
                                  AND embedding_model=@model
                            )
                            SELECT c.book_id,
                                   MAX(1.0 - (c.embedding <=> liked.embedding))::double precision
                            FROM public.book_metadata c
                            JOIN public.books b ON b.book_id=c.book_id
                            CROSS JOIN liked
                            WHERE b.is_visible=TRUE AND c.embedding IS NOT NULL
                              AND c.embedding_model=@model
                            GROUP BY c.book_id;", db);
                        similar.Parameters.AddWithValue("liked", likedIds);
                        similar.Parameters.AddWithValue("model", embeddingModel);
                        await using var sr = await similar.ExecuteReaderAsync(ct);
                        while (await sr.ReadAsync(ct))
                        {
                            double score = sr.GetDouble(1);
                            if (double.IsFinite(score)) semanticAffinity[sr.GetInt64(0)] = score;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // Optional pgvector not ready: preserve Step83 homepage recommendations.
                        app.Logger.LogWarning("Personalized semantic boost disabled: {Type}", ex.GetType().Name);
                    }
                }
                var cats = new Dictionary<long, double>();
                var authors = new Dictionary<long, double>();
                foreach (var book in candidates)
                {
                    history.TryGetValue(book.Id, out var loan);
                    ratings.TryGetValue(book.Id, out int rating);
                    double interest = 0;
                    if (loan.Count > 0)
                        interest += Math.Min(4, loan.Count) * 1.3 +
                            ((DateTime.UtcNow - loan.Latest.ToUniversalTime()).TotalDays <= 180 ? 1.5 : 0);
                    if (rating >= 4) interest += (rating - 2) * 2.5;
                    if (rating <= 2 && rating > 0) interest -= 3;
                    foreach (long id in book.CategoryIds.Distinct())
                        cats[id] = (cats.TryGetValue(id, out var v) ? v : 0) + interest;
                    foreach (long id in book.AuthorIds.Distinct())
                        authors[id] = (authors.TryGetValue(id, out var v) ? v : 0) + interest;
                }
                bool personalized = history.Count > 0 || ratings.Count > 0;
                var ranked = candidates
                    .Where(b => !history.ContainsKey(b.Id) &&
                        !(ratings.TryGetValue(b.Id, out var r) && r <= 2))
                    .Select(b =>
                    {
                        var topCat = b.CategoryIds.Select(id => cats.TryGetValue(id, out var v) ? v : 0d)
                            .DefaultIfEmpty(0).Max();
                        var topAuthor = b.AuthorIds.Select(id => authors.TryGetValue(id, out var v) ? v : 0d)
                            .DefaultIfEmpty(0).Max();
                        // Logarithmic affinity to avoid one dominant category swallowing everything.
                        double affinity = personalized ? Math.Log(1 + Math.Max(0, topCat)) * 4 +
                            Math.Log(1 + Math.Max(0, topAuthor)) * 5 : 0;
                        semanticAffinity.TryGetValue(b.Id, out double similarity);
                        double semanticBoost = similarity >= 0.4 ? (similarity - 0.35) * 20 : 0;
                        double score = affinity + semanticBoost + Math.Min(b.Popularity, 15) * 0.6 +
                            (b.Year >= DateTime.UtcNow.Year - 2 ? 0.7 : 0) +
                            (b.Available > 0 ? 4 : -9);
                        string reason = semanticBoost >= 4
                            ? "Nội dung tương tự sách bạn từng đọc hoặc yêu thích"
                            : topAuthor > 0 && topAuthor >= topCat
                            ? "Có tác giả bạn từng đọc hoặc đánh giá cao"
                            : topCat > 0 ? "Phù hợp với thể loại bạn thường đọc"
                            : b.Popularity > 0 ? "Được mượn nhiều gần đây"
                            : "Khám phá thêm một đầu sách trong thư viện";
                        return new { Book = b, Score = score, Reason = reason };
                    })
                    .OrderByDescending(x => x.Book.Available > 0)
                    .ThenByDescending(x => x.Score)
                    .ThenByDescending(x => x.Book.Popularity)
                    .ThenBy(x => x.Book.Title, StringComparer.OrdinalIgnoreCase)
                    .Take(8).ToArray();

                return Results.Ok(new
                {
                    personalized,
                    note = personalized ? "Gợi ý dựa trên lịch sử mượn và đánh giá của bạn."
                        : "Bạn chưa có lịch sử mượn hoặc đánh giá; đang giới thiệu sách phổ biến và đa dạng.",
                    recommendations = ranked.Select(x => new
                    {
                        bookId = x.Book.Id,
                        title = x.Book.Title,
                        authors = x.Book.Authors,
                        categories = x.Book.Categories,
                        availableCopies = x.Book.Available,
                        coverUrl = BookCoverUrlResolver.Resolve(
                            x.Book.CoverUrl, x.Book.CoverObjectKey, config, app.Environment),
                        reason = x.Reason
                    }).ToArray()
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                app.Logger.LogError("Reader recommendation query failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Không tải được gợi ý cá nhân; kiểm tra API/PostgreSQL." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static string? Bearer(HttpRequest req)
    {
        string auth = req.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
    }
}
