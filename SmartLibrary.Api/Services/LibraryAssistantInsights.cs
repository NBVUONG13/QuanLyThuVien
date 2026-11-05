using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Read-only, role-checked questions for the library assistant. The LLM supplies
/// only an intent name. It never supplies SQL, IDs, filters, user roles or amounts.
/// </summary>
internal static class LibraryAssistantInsights
{
    internal sealed record Answer(string Reply, object[] Suggestions);
    private sealed record RankedBook(long Id, string Title, string Author, string Category,
        int Year, int Available, int Borrowed90Days, decimal AverageRating, int RatingCount,
        DateTime CreatedAt);

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "top_borrowed", "top_rated", "new_arrivals", "new_publications",
        "available_books", "out_of_stock_books", "category_stats",
        "inventory_low", "inventory_condition", "today_activity", "loan_overview",
        "pending_renewals", "overdue_overview", "reader_overview", "staff_overview",
        "outstanding_fines", "my_attendance", "my_salary", "salary_overview",
        "my_loan_limits", "loan_policy", "capabilities", "library_stats"
    };

    internal static string? ResolveIntent(string? modelIntent, string question)
    {
        if (!string.IsNullOrWhiteSpace(modelIntent) && Supported.Contains(modelIntent))
            return modelIntent;
        // Safe fallback for common analytics: model parsing can be unavailable or
        // mistakenly output "unsupported". This is NOT the primary NLP engine.
        string q = Normalize(question);
        bool ranking = Has(q, "nhat", "top", "xep hang", "cao", "nhieu", "tot", "noi bat", "duoc yeu thich");
        if (Has(q, "danh gia", "cham diem", "rating", "sao", "diem so") &&
            (ranking || Has(q, "sach nao", "truyen nao"))) return "top_rated";
        if (Has(q, "sach hot", "truyen hot", "hot nhat", "muon nhieu nhat", "duoc muon nhieu", "sach pho bien", "sach thinh hanh", "sach muon nhieu", "top sach", "sach nao hot"))
            return "top_borrowed";
        if (Has(q, "lam duoc gi", "hoi duoc gi", "chatbot co the", "tro ly co the", "chuc nang chatbot")) return "capabilities";
        if (Has(q, "toi duoc muon bao nhieu", "toi duoc muon may", "toi muon toi da", "han muc muon", "hang doc gia cua toi", "toi duoc muon may ngay", "toi duoc muon toi da"))
            return "my_loan_limits";
        if (Has(q, "quy dinh muon", "chinh sach muon", "quy dinh gia han", "muc phat tre", "phi phat qua han", "cho muon bao nhieu ngay"))
            return "loan_policy";
        if (Has(q, "bao nhieu dau sach", "tong dau sach", "thong ke kho sach", "tong so sach trong thu vien"))
            return "library_stats";
        if (Has(q, "sach moi nhap", "sach moi them", "moi them vao", "moi ve thu vien")) return "new_arrivals";
        if (Has(q, "moi xuat ban", "nam xuat ban moi", "xuat ban gan day")) return "new_publications";
        if (Has(q, "the loai pho bien", "the loai nhieu sach", "thong ke the loai")) return "category_stats";
        if (Has(q, "sach het hang", "sach het cuon", "nhung sach da het", "danh sach sach het", "dau sach nao het"))
            return "out_of_stock_books";
        if (Has(q, "sach con san", "cac sach co san", "sach dang con san", "danh sach sach co the muon"))
            return "available_books";
        if (Has(q, "sach sap het", "sach it cuon", "ton kho thap", "sach can nhap them"))
            return "inventory_low";
        if (Has(q, "sach bi hong", "sach bi mat", "sach hu hong", "thong ke tinh trang sach"))
            return "inventory_condition";
        if (Has(q, "hom nay muon bao nhieu", "hom nay tra bao nhieu", "luot muon hom nay", "hoat dong hom nay"))
            return "today_activity";
        if (Has(q, "tong luot muon", "bao nhieu luot muon", "bao nhieu sach dang muon", "thong ke muon tra"))
            return "loan_overview";
        if (Has(q, "yeu cau gia han dang cho", "don gia han cho duyet", "bao nhieu don gia han"))
            return "pending_renewals";
        if (Has(q, "tong so qua han", "so cuon qua han", "bao nhieu cuon qua han", "thong ke qua han"))
            return "overdue_overview";
        if (Has(q, "tong doc gia", "so luong doc gia", "thong ke doc gia", "bao nhieu doc gia"))
            return "reader_overview";
        if (Has(q, "tong nhan vien", "bao nhieu nhan vien", "thong ke nhan vien"))
            return "staff_overview";
        if (Has(q, "tien phat chua thu", "tong tien phat", "tong no phat", "cong no tien phat") &&
            !Has(q, "cua toi", "cua minh", "toi no", "minh no")) return "outstanding_fines";
        if (Has(q, "cham cong cua toi", "gio lam cua toi", "ca lam cua minh")) return "my_attendance";
        if (Has(q, "luong cua toi", "luong cua minh", "bang luong ca nhan")) return "my_salary";
        if (Has(q, "tong quy luong", "thong ke luong nhan vien", "chi phi luong")) return "salary_overview";
        return null;
    }

    internal static bool LooksLikeUnmappedDataRequest(string question)
    {
        string q = Normalize(question);
        return Has(q, "bao nhieu", "con may", "tong so", "tong tien", "xep hang", "thong ke",
            "doanh thu", "so luong", "trang thai don", "lich su muon", "luong nhan vien",
            "bang luong", "ai muon", "ai qua han", "danh sach qua han", "du lieu nguoi dung");
    }

    internal static async Task<Answer?> TryAnswerAsync(string? intent, string role, long userId,
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (intent is null || !Supported.Contains(intent)) return null;
        bool staff = role is "Staff" or "Admin";
        bool admin = role == "Admin";
        switch (intent)
        {
            case "top_borrowed":
            case "top_rated":
            case "new_arrivals":
            case "new_publications":
            case "available_books":
            case "out_of_stock_books":
            case "inventory_low":
                if (intent == "inventory_low" && !staff) return Denied("Sách cần nhập thêm chỉ dành cho Staff/Admin.");
                return await BookRankingAsync(intent, connection, cancellationToken);
            case "category_stats": return await CategoryStatsAsync(connection, cancellationToken);
            case "capabilities": return Capabilities(role);
            case "my_loan_limits":
                if (role != "Reader") return Denied("Hạn mức mượn cá nhân áp dụng cho tài khoản Reader. Staff/Admin có thể xem chính sách mượn chung.");
                return await MyLoanLimitsAsync(userId, connection, cancellationToken);
            case "loan_policy": return await LoanPolicyAsync(connection, cancellationToken);
            case "library_stats": return await LibrarySummaryAsync(connection, cancellationToken);
            case "inventory_condition":
            case "today_activity":
            case "loan_overview":
            case "pending_renewals":
            case "overdue_overview":
                if (!staff) return Denied("Thống kê vận hành chỉ dành cho Staff/Admin.");
                return await OperationalSummaryAsync(intent, connection, cancellationToken);
            case "reader_overview":
                if (!staff) return Denied("Thống kê độc giả chỉ dành cho Staff/Admin.");
                return await ScalarSummaryAsync(connection, "SELECT COUNT(*) FROM public.reader_profiles;",
                    "Thư viện hiện có {0:N0} hồ sơ độc giả (bao gồm Guest, nếu có).", cancellationToken);
            case "staff_overview":
                if (!admin) return Denied("Thống kê nhân sự chỉ dành cho Admin.");
                return await ScalarSummaryAsync(connection, "SELECT COUNT(*) FROM public.staff_profiles;",
                    "Hiện có {0:N0} hồ sơ nhân viên.", cancellationToken);
            case "outstanding_fines":
                if (!admin) return Denied("Thống kê dư nợ tiền phạt toàn thư viện chỉ dành cho Admin.");
                return await FineSummaryAsync(connection, cancellationToken);
            case "my_attendance":
                if (!staff) return Denied("Tài khoản Reader không có dữ liệu chấm công nhân viên.");
                return await MyAttendanceAsync(userId, connection, cancellationToken);
            case "my_salary":
                if (!staff) return Denied("Tài khoản Reader không có bảng lương nhân viên.");
                return await MySalaryAsync(userId, connection, cancellationToken);
            case "salary_overview":
                if (!admin) return Denied("Thống kê tổng quỹ lương chỉ dành cho Admin.");
                return await SalarySummaryAsync(connection, cancellationToken);
        }
        return null;
    }

    private static Answer Denied(string msg) => new(msg, Array.Empty<object>());
    private static Answer Capabilities(string role)
    {
        string common = "Tôi có thể tra: sách còn để mượn, tìm theo tên/chủ đề, sách được mượn nhiều, điểm đánh giá cao, sách mới, thể loại phổ biến và chính sách mượn.";
        string reader = " Với Reader: hỏi sách đang mượn, hạn trả, lịch sử mượn, khoản phạt và hạn mức mượn của chính mình.";
        string staff = " Với Staff: hỏi thống kê mượn/trả hôm nay, cuốn quá hạn, đầu sách sắp hết, sách hỏng/mất, số yêu cầu gia hạn chờ duyệt và chấm công/bảng lương của chính mình.";
        string admin = " Với Admin: còn có số hồ sơ độc giả/nhân viên, tổng tiền phạt cần thu và quỹ lương.";
        return Denied(common + (role == "Reader" ? reader : staff + (role == "Admin" ? admin : "")) +
            " Chatbot chỉ tra cứu, không tự thêm/xóa sách, duyệt đơn hoặc tiết lộ tài khoản khác.");
    }

    private static async Task<Answer> MyLoanLimitsAsync(long userId, NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT rt.tier_name,
                   COALESCE(rp.override_max_active_copies,rt.max_active_copies),
                   COALESCE(rp.override_max_loan_days,rt.max_loan_days)
            FROM public.reader_profiles rp JOIN public.reader_tiers rt ON rt.tier_id=rp.tier_id
            WHERE rp.user_id=@user;", db);
        cmd.Parameters.AddWithValue("user", userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return Denied("Không tìm thấy hồ sơ Reader gắn với phiên đăng nhập.");
        return Denied($"Hạng độc giả của bạn: {rd.GetString(0)}. Hạn mức hiện tại: tối đa {rd.GetInt32(1)} cuốn đang mượn, mỗi lần mượn tối đa {rd.GetInt32(2)} ngày (còn phụ thuộc điều kiện nghiệp vụ và phê duyệt).");
    }

    private static async Task<Answer> LoanPolicyAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT max_renewals, renewal_days, overdue_fee_per_day, overdue_grace_days,
                   max_overdue_fee_per_detail
            FROM public.borrowing_policy_versions
            WHERE effective_at <= now() ORDER BY effective_at DESC LIMIT 1;", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return Denied("Chưa có phiên bản chính sách mượn đang có hiệu lực.");
        return Denied($"Chính sách hiện hành: tối đa {rd.GetInt32(0)} lần gia hạn, mỗi lần {rd.GetInt32(1)} ngày; " +
            $"phạt quá hạn {rd.GetDecimal(2):N0} VND/ngày, ân hạn {rd.GetInt32(3)} ngày, " +
            $"mức phạt trần theo cuốn {rd.GetDecimal(4):N0} VND. Gia hạn phải được xét duyệt theo điều kiện đang áp dụng.");
    }

    private static async Task<Answer> LibrarySummaryAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT (SELECT COUNT(*)::bigint FROM public.books WHERE is_visible=TRUE),
                   (SELECT COUNT(*)::bigint FROM public.book_copies WHERE condition_status='active'),
                   (SELECT COUNT(*)::bigint FROM public.book_copies c
                    WHERE c.condition_status='active' AND NOT EXISTS (
                      SELECT 1 FROM public.borrow_details d
                      WHERE d.book_copy_id=c.copy_id AND d.closed_at IS NULL));", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return Denied($"Danh mục có {rd.GetInt64(0)} đầu sách đang hiển thị; {rd.GetInt64(1)} cuốn vật lý trạng thái active; {rd.GetInt64(2)} cuốn hiện sẵn sàng cho mượn.");
    }

    private static object Suggestion(RankedBook b) => new
    {
        bookId = b.Id,
        title = b.Title,
        author = b.Author,
        category = b.Category,
        publicationYear = b.Year,
        availableCopies = b.Available,
        recentBorrows = b.Borrowed90Days
    };

    private static async Task<Answer> BookRankingAsync(string intent, NpgsqlConnection db, CancellationToken ct)
    {
        var catalog = new List<RankedBook>();
        const string sql = @"
            SELECT b.book_id, b.title,
                   COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                     FROM public.book_authors ba JOIN public.authors a ON a.author_id=ba.author_id
                     WHERE ba.book_id=b.book_id),''),
                   COALESCE((SELECT string_agg(c.name, ', ' ORDER BY c.name)
                     FROM public.book_categories bc JOIN public.categories c ON c.category_id=bc.category_id
                     WHERE bc.book_id=b.book_id),''),
                   COALESCE(b.publication_year,0),
                   (SELECT COUNT(*)::int FROM public.book_copies cp
                     WHERE cp.book_id=b.book_id AND cp.condition_status='active'
                       AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                         WHERE d.book_copy_id=cp.copy_id AND d.closed_at IS NULL)),
                   (SELECT COUNT(*)::int FROM public.borrow_details d
                     JOIN public.book_copies cp ON cp.copy_id=d.book_copy_id
                     JOIN public.borrows br ON br.borrow_id=d.borrow_id
                     WHERE cp.book_id=b.book_id AND br.borrowed_at >= now()-INTERVAL '90 days'),
                   COALESCE((SELECT ROUND(AVG(r.score)::numeric,2)
                     FROM public.ratings r WHERE r.book_id=b.book_id), 0::numeric),
                   (SELECT COUNT(*)::int FROM public.ratings r WHERE r.book_id=b.book_id),
                   (b.created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')
            FROM public.books b WHERE b.is_visible=TRUE
            ORDER BY b.book_id LIMIT 1000;";
        await using (var cmd = new NpgsqlCommand(sql, db))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                catalog.Add(new RankedBook(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                    reader.GetDecimal(7), reader.GetInt32(8), reader.GetDateTime(9)));
        }
        if (catalog.Count == 0) return Denied("Chưa có đầu sách nào đang hiển thị.");

        var rows = intent switch
        {
            "top_borrowed" => catalog.Where(b => b.Borrowed90Days > 0)
                .OrderByDescending(b => b.Borrowed90Days).ThenBy(b => b.Title),
            "top_rated" => catalog.Where(b => b.RatingCount > 0)
                .OrderByDescending(b => b.AverageRating).ThenByDescending(b => b.RatingCount).ThenBy(b => b.Title),
            "new_arrivals" => catalog.OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id),
            "new_publications" => catalog.Where(b => b.Year > 0)
                .OrderByDescending(b => b.Year).ThenBy(b => b.Title),
            "available_books" => catalog.Where(b => b.Available > 0)
                .OrderByDescending(b => b.Available).ThenBy(b => b.Title),
            "out_of_stock_books" => catalog.Where(b => b.Available == 0)
                .OrderBy(b => b.Title),
            _ => catalog.OrderBy(b => b.Available).ThenBy(b => b.Title)
        };
        var top = rows.Take(6).ToList();
        if (top.Count == 0)
        {
            return intent switch
            {
                "top_borrowed" => Denied("Chưa ghi nhận lượt mượn trong 90 ngày gần đây, nên chưa có cơ sở xác định sách được mượn nhiều nhất."),
                "top_rated" => Denied("Chưa có đánh giá sách nào trong hệ thống, nên chưa thể xếp hạng theo điểm."),
                "out_of_stock_books" => Denied("Các đầu sách đang hiển thị đều có ít nhất một cuốn sẵn sàng cho mượn."),
                "available_books" => Denied("Hiện không có cuốn vật lý nào sẵn sàng cho mượn."),
                _ => Denied("Chưa có dữ liệu phù hợp để thống kê.")
            };
        }
        string heading = intent switch
        {
            "top_borrowed" => "Sách được mượn nhiều nhất trong 90 ngày gần đây (dựa trên phiếu mượn đã ghi nhận):",
            "top_rated" => "Sách có điểm đánh giá trung bình cao nhất (số lượt đánh giá ở bên cạnh):",
            "new_arrivals" => "Các đầu sách được thêm vào hệ thống gần đây:",
            "new_publications" => "Các đầu sách có năm xuất bản mới nhất trong danh mục:",
            "available_books" => "Một số đầu sách hiện có nhiều cuốn sẵn sàng:",
            "out_of_stock_books" => "Một số đầu sách hiện không có cuốn sẵn sàng để mượn:",
            _ => "Các đầu sách có tồn kho sẵn sàng thấp (cần kiểm tra và nhập thêm):"
        };
        string[] lines = top.Select((b, i) =>
            $"{i + 1}. {b.Title} — " + (intent switch
            {
                "top_borrowed" => $"{b.Borrowed90Days} lượt mượn/90 ngày",
                "top_rated" => $"{b.AverageRating:0.00}/5 ({b.RatingCount} lượt đánh giá)",
                "new_arrivals" => $"thêm ngày {b.CreatedAt:dd/MM/yyyy}",
                "new_publications" => $"xuất bản năm {b.Year}",
                _ => $"{b.Available} cuốn sẵn sàng"
            })).ToArray();
        return new Answer(heading + "\n" + string.Join("\n", lines), top.Select(Suggestion).ToArray());
    }

    private static async Task<Answer> CategoryStatsAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT c.name, COUNT(DISTINCT b.book_id)::int AS titles
            FROM public.categories c JOIN public.book_categories bc ON bc.category_id=c.category_id
            JOIN public.books b ON b.book_id=bc.book_id AND b.is_visible=TRUE
            GROUP BY c.category_id,c.name
            ORDER BY titles DESC,c.name LIMIT 8;", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) lines.Add($"• {rd.GetString(0)}: {rd.GetInt32(1)} đầu sách");
        return lines.Count == 0 ? Denied("Chưa có đủ dữ liệu thể loại để thống kê.") :
            new Answer("Các thể loại có nhiều đầu sách nhất trong danh mục:\n" + string.Join("\n", lines), Array.Empty<object>());
    }

    private static async Task<Answer> OperationalSummaryAsync(string intent, NpgsqlConnection db, CancellationToken ct)
    {
        string sql = intent switch
        {
            "inventory_condition" => @"
                SELECT COUNT(*) FILTER (WHERE condition_status='active')::bigint,
                       COUNT(*) FILTER (WHERE condition_status='damaged')::bigint,
                       COUNT(*) FILTER (WHERE condition_status='lost')::bigint,
                       COUNT(*) FILTER (WHERE condition_status='retired')::bigint
                FROM public.book_copies;",
            "today_activity" => @"
                SELECT (SELECT COUNT(*)::bigint FROM public.borrows
                  WHERE (borrowed_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date =
                        (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date),
                       (SELECT COUNT(*)::bigint FROM public.borrow_details
                  WHERE (returned_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date =
                        (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date);",
            "loan_overview" => @"
                SELECT (SELECT COUNT(*)::bigint FROM public.borrow_details WHERE closed_at IS NULL),
                       (SELECT COUNT(*)::bigint FROM public.borrow_details
                        WHERE closed_at IS NULL AND due_at<now()),
                       (SELECT COUNT(*)::bigint FROM public.borrow_details);",
            "pending_renewals" => @"
                SELECT COUNT(*)::bigint FROM public.borrow_renewal_requests WHERE status='pending';",
            _ => @"
                SELECT COUNT(*)::bigint, COUNT(DISTINCT br.reader_user_id)::bigint
                FROM public.borrow_details d
                JOIN public.borrows br ON br.borrow_id=d.borrow_id
                WHERE d.closed_at IS NULL AND d.due_at<now();"
        };
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        string reply = intent switch
        {
            "inventory_condition" => $"Tình trạng cuốn vật lý: {rd.GetInt64(0)} active, {rd.GetInt64(1)} hỏng, {rd.GetInt64(2)} mất, {rd.GetInt64(3)} ngừng sử dụng.",
            "today_activity" => $"Hôm nay đã lập {rd.GetInt64(0)} phiếu mượn và trả {rd.GetInt64(1)} cuốn (giờ Việt Nam).",
            "loan_overview" => $"Hiện có {rd.GetInt64(0)} cuốn đang mượn chưa đóng, trong đó {rd.GetInt64(1)} cuốn quá hạn; tổng cộng {rd.GetInt64(2)} lượt mượn cuốn đã ghi nhận.",
            "pending_renewals" => $"Có {rd.GetInt64(0)} yêu cầu gia hạn đang chờ duyệt.",
            _ => $"Hiện có {rd.GetInt64(0)} cuốn quá hạn thuộc {rd.GetInt64(1)} độc giả. Để xử lý cụ thể, hãy mở phần quản lý mượn trả."
        };
        return Denied(reply);
    }

    private static async Task<Answer> ScalarSummaryAsync(NpgsqlConnection db, string sql, string format, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        object? value = await cmd.ExecuteScalarAsync(ct);
        return Denied(string.Format(CultureInfo.GetCultureInfo("vi-VN"), format, Convert.ToInt64(value)));
    }

    private static async Task<Answer> FineSummaryAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT COALESCE(SUM(GREATEST(0,f.amount - f.waived_amount -
              COALESCE((SELECT SUM(p.amount) FROM public.fine_payments p WHERE p.fine_id=f.fine_id),0))),0)
            FROM public.fines f;", db);
        decimal v = Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
        return Denied($"Tổng tiền phạt còn phải thu đã ghi nhận: {v:N0} VND (không gồm khoản chưa lập phiếu phạt).");
    }

    private static async Task<Answer> MyAttendanceAsync(long userId, NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)::bigint,
                   COALESCE(ROUND(SUM(EXTRACT(EPOCH FROM (check_out_at-check_in_at))/3600)::numeric,2),0)
            FROM public.timesheets
            WHERE staff_user_id=@user
              AND (work_date >= date_trunc('month',now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
              AND work_date < (date_trunc('month',now() AT TIME ZONE 'Asia/Ho_Chi_Minh')+INTERVAL '1 month')::date
              AND check_out_at IS NOT NULL;", db);
        cmd.Parameters.AddWithValue("user", userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return Denied($"Tháng hiện tại, bạn có {rd.GetInt64(0)} ca đã chấm công hoàn tất, tổng {rd.GetDecimal(1):0.##} giờ (không tính ca chưa check-out).");
    }

    private static async Task<Answer> MySalaryAsync(long userId, NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT salary_month, total_amount, status
            FROM public.salaries WHERE staff_user_id=@user
            ORDER BY salary_month DESC LIMIT 1;", db);
        cmd.Parameters.AddWithValue("user", userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return Denied("Chưa có bảng lương cho tài khoản của bạn.");
        return Denied($"Bảng lương gần nhất của bạn: tháng {rd.GetDateTime(0):MM/yyyy}, " +
            $"{rd.GetDecimal(1):N0} VND, trạng thái {rd.GetString(2)}.");
    }

    private static async Task<Answer> SalarySummaryAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*)::bigint, COALESCE(SUM(total_amount),0)
            FROM public.salaries
            WHERE salary_month = date_trunc('month',now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date;", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return Denied($"Tháng hiện tại có {rd.GetInt64(0)} bảng lương, tổng số tiền ghi nhận {rd.GetDecimal(1):N0} VND (gồm bản nháp nếu có).");
    }

    private static bool Has(string q, params string[] phrases) => phrases.Any(q.Contains);
    private static string Normalize(string value)
    {
        string form = value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var chars = form.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(chars).Normalize(NormalizationForm.FormC);
    }
}
