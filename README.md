# Smart Library — Hệ thống quản lý thư viện thông minh

> Ứng dụng quản lý thư viện trên Windows, kết hợp WPF, ASP.NET Core Web API, PostgreSQL, tìm kiếm ngữ nghĩa bằng AI và lưu trữ ảnh bìa trên Cloudinary.
>
> **Trạng thái:** Dự án học tập/đồ án lớn, đang phát triển và kiểm thử. Bản mã hiện tại ưu tiên **Development trên `localhost`**, **chưa phải bản phát hành Internet/EXE độc lập có thể đưa cho bất kỳ ai chạy ngay**.

[![.NET](https://img.shields.io/badge/Backend-.NET%2010-512BD4)](https://dotnet.microsoft.com/) [![WPF](https://img.shields.io/badge/Desktop-WPF%20.NET%20Framework%204.7.2-0078D4)](https://learn.microsoft.com/dotnet/desktop/wpf/) [![Database](https://img.shields.io/badge/Database-PostgreSQL-336791)](https://www.postgresql.org/) [![Media](https://img.shields.io/badge/Images-Cloudinary-3448C5)](https://cloudinary.com/)

## Mục lục

1. [Giới thiệu](#1-giới-thiệu)
2. [Chức năng](#2-chức-năng)
3. [Kiến trúc và cấu trúc mã nguồn](#3-kiến-trúc-và-cấu-trúc-mã-nguồn)
4. [Ai cần cài những gì?](#4-ai-cần-cài-những-gì)
5. [Clone và chạy bằng Visual Studio](#5-clone-và-chạy-bằng-visual-studio)
6. [Cấu hình Aiven PostgreSQL](#6-cấu-hình-aiven-postgresql)
7. [Cloudinary và ảnh bìa sách](#7-cloudinary-và-ảnh-bìa-sách)
8. [Tùy chọn AI với LM Studio](#8-tùy-chọn-ai-với-lm-studio)
9. [Quản trị database bằng DBeaver](#9-quản-trị-database-bằng-dbeaver)
10. [Sử dụng ứng dụng theo vai trò](#10-sử-dụng-ứng-dụng-theo-vai-trò)
11. [Người clone có dùng chung database được không?](#11-người-clone-có-dùng-chung-database-được-không)
12. [Phát hành EXE cho người khác](#12-phát-hành-exe-cho-người-khác)
13. [Bảo mật và các hạn chế hiện tại](#13-bảo-mật-và-các-hạn-chế-hiện-tại)
14. [Khắc phục sự cố](#14-khắc-phục-sự-cố)
15. [Đóng góp, trạng thái kiểm thử và tài liệu](#15-đóng-góp-trạng-thái-kiểm-thử-và-tài-liệu)

## 1. Giới thiệu

**Smart Library** là hệ thống hỗ trợ quản lý thư viện và phục vụ độc giả. Ứng dụng desktop cho phép tìm sách, xem ảnh bìa/mô tả, tra cứu cuốn vật lý và vị trí kệ, thực hiện các nghiệp vụ mượn–trả. Backend kiểm tra phiên đăng nhập, phân quyền và truy cập PostgreSQL. Các tính năng thông minh hỗ trợ gợi ý sách, chatbot tiếng Việt và tìm kiếm ngữ nghĩa dựa trên embedding.

**Đối tượng sử dụng:**

- **Reader (độc giả):** duyệt sách, tra cứu tình trạng, gửi yêu cầu mượn, xem lịch sử, thông báo và các tiện ích được phân quyền.
- **Staff (nhân viên):** quản lý đầu sách/cuốn vật lý, tiếp nhận yêu cầu, mượn–trả, hỗ trợ khách tại quầy và những nghiệp vụ được cấp quyền.
- **Admin (quản trị):** cấu hình chính sách, quản lý nhân sự, thống kê/báo cáo, và các chức năng quản trị đã triển khai.
- **Guest (khách tại quầy):** hồ sơ mượn do nhân viên quản lý; không mặc nhiên có tài khoản đăng nhập.

### Công nghệ chính

| Lớp | Công nghệ | Vai trò |
|---|---|---|
| Desktop | C#, WPF, MVVM, .NET Framework **4.7.2** | Giao diện Windows |
| Backend | ASP.NET Core Web API, .NET **10** | Xác thực, nghiệp vụ, REST API |
| Database | Aiven PostgreSQL (`defaultdb`, schema `public`) | Dữ liệu sách, tài khoản, kho, mượn trả, đánh giá... |
| Vector | pgvector, vector **1024** chiều | Lưu embedding sách |
| Kết nối PostgreSQL | Npgsql | Truy cập database từ API |
| Xác thực mật khẩu | BCrypt.Net-Next | Hash/xác minh mật khẩu tại backend |
| Ảnh bìa | **Cloudinary** | Lưu ảnh; Aiven lưu URL/public ID |
| AI local (tùy chọn) | LM Studio, Qwen3.5 4B, Qwen3-Embedding-0.6B | Hội thoại, hiểu câu hỏi và embedding |

## 2. Chức năng

Các nhóm màn hình và endpoint có trong source/những bản cập nhật gần nhất:

- **Danh mục và kho sách:** tìm kiếm theo tên/ISBN/tác giả, lọc thể loại/NXB/năm, trang chi tiết, ảnh bìa; thêm/sửa đầu sách và ảnh bìa; quản lý các cuốn vật lý có `copy_code` riêng.
- **Mượn/trả và chính sách:** tiếp nhận yêu cầu, kiểm tra tình trạng cuốn, xử lý gia hạn, lịch sử, quá hạn, tiền phạt theo quyền.
- **Sơ đồ thư viện 2D:** tra cứu vị trí kệ và chỉ đường; dữ liệu dự án đã được quy ước lại theo các kệ `K1`–`K26` (vị trí trên sơ đồ là quy ước ứng dụng, không phải bản đồ đo đạc thực địa).
- **Tài khoản và phân quyền:** Reader/Staff/Admin; hỗ trợ khách mượn tại quầy (Guest), hồ sơ độc giả, phiên đăng nhập.
- **Thông báo:** cập nhật nhắc hạn từ ứng dụng khi Reader đang sử dụng; **chưa phải dịch vụ nhắc hạn 24/7 độc lập khi toàn bộ ứng dụng đóng**.
- **Gợi ý và trợ lý AI:** chatbot hỏi đáp về sách/nghiệp vụ theo quyền, tìm sách theo ngữ nghĩa, gợi ý từ lịch sử và dữ liệu có sẵn. Trả lời liên quan đến tồn kho/thống kê phải phụ thuộc dữ liệu truy vấn thực tế.
- **Quản trị:** báo cáo thư viện, chính sách, chấm công và lương theo những endpoint/màn hình hiện có.

> Các chức năng đang tiếp tục phát triển: triển khai backend dùng chung trên Internet, đóng gói desktop cho người dùng cuối, thử nghiệm bảo mật đầy đủ và khả năng vận hành khi không có LM Studio. Không coi tất cả tính năng là hoàn tất chỉ vì có màn hình hoặc code.

### Dữ liệu thử nghiệm đã xác nhận

Theo kết quả chạy ứng dụng/DBeaver được cung cấp tại thời điểm biên soạn (05/11/2026), database thử nghiệm có **38 đầu sách**, **38/38 embedding** và **38/38 URL ảnh Cloudinary**. Đây là **số liệu tại một thời điểm**, không phải số lượng cố định hoặc dữ liệu được đính kèm trong Git.

## 3. Kiến trúc và cấu trúc mã nguồn

```text
                [QuanLyThuVien — WPF trên Windows]
                              |
                       HTTP localhost
                              |
                  [SmartLibrary.Api — .NET 10]
                      /        |        \
            PostgreSQL      Cloudinary    LM Studio (tùy chọn)
               Aiven       (ảnh bìa)       chat + embedding
```

Trong bản mã hiện tại, WPF gọi API **`http://127.0.0.1:5082`**; API đang lắng nghe trên **loopback của chính máy chạy**. LM Studio mặc định sử dụng **`http://127.0.0.1:1234`**. Vì vậy không thể chạy riêng EXE trên máy khác rồi tự động kết nối tới API trên máy phát triển.

Cấu trúc repository tham khảo:

```text
SmartLibrary/
├── README.md                       # Tài liệu này
├── DEPENDENCIES.md                 # Phần mềm, phiên bản, NuGet, runtime
├── QuanLyThuVien.slnx              # Solution Visual Studio (nếu có trong nhánh)
├── QuanLyThuVien/
│   ├── QuanLyThuVien.csproj
│   ├── packages.config
│   ├── Services/                  # Client gọi API
│   ├── ViewModels/
│   └── Views/                    # XAML giao diện
└── SmartLibrary.Api/
    ├── SmartLibrary.Api.csproj
    ├── Program.cs
    ├── Services/                 # API nghiệp vụ, Cloudinary, AI, auth...
    ├── appsettings.json
    ├── appsettings.Development.json
    └── wwwroot/                 # Giữ thư mục (và .gitkeep) để tránh lỗi static assets
```

> **Lưu ý đồng bộ mã nguồn:** ZIP `QuanLyThuVien-master.zip` đã kiểm tra có hai project nhưng không nhất thiết bao gồm **tất cả bản vá mới nhất** (tự động embedding, chatbot nhiều vai trò, Cloudinary). Trước khi public GitHub, phải commit các file code đã chạy thành công trên máy chủ dự án, không chỉ commit ZIP cũ.

## 4. Ai cần cài những gì?

| Mục đích | Bắt buộc | Không bắt buộc |
|---|---|---|
| **Clone để build/chạy đủ WPF + API** | Windows, Visual Studio 2026 (**18.0+**), .NET 10 SDK, .NET Framework 4.7.2 **Targeting/Developer Pack**, quyền dùng DB qua API | DBeaver, LM Studio, Python, PostgreSQL local |
| **Chạy WPF từ bản Release/EXE** | Windows, .NET Framework runtime tương thích (Windows 11 thường đã có bản mới hơn), **API truy cập được** và tài khoản hợp lệ | Visual Studio, .NET 10 SDK trên máy WPF nếu API đã đặt trên máy khác |
| **Chạy API từ bản publish** | .NET 10 **ASP.NET Core Runtime** nếu publish framework-dependent; cấu hình Aiven/Cloudinary; quyền mạng | Visual Studio trên máy chủ, LM Studio nếu không dùng AI |
| **Dùng AI** | LM Studio + model chat/embedding phù hợp, Local Server | DBeaver |
| **Tự xem/sửa database** | DBeaver hoặc công cụ PostgreSQL, quyền DB do chủ dự án cấp | Cài PostgreSQL cục bộ nếu đang dùng Aiven |

Nguồn tải chính thức: [Visual Studio](https://visualstudio.microsoft.com/downloads/), [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0), [.NET Framework 4.7.2 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net472), [Git](https://git-scm.com/downloads), [DBeaver](https://dbeaver.io/download/), [LM Studio](https://lmstudio.ai/).

> **Trong Visual Studio Installer:** chọn workload **.NET desktop development** và **ASP.NET and web development**; kiểm tra đã có **.NET Framework 4.7.2 targeting pack**. Visual Studio 2022 không hỗ trợ target `net10.0` như một dự án .NET 10 chính thức.
>
> Danh sách NuGet chính xác nằm trong [DEPENDENCIES.md](DEPENDENCIES.md). Thông thường **không cài tay từng package**: Visual Studio/NuGet tự khôi phục từ file dự án.

## 5. Clone và chạy bằng Visual Studio

### 5.1. Clone

```powershell
git clone <DIA_CHI_REPOSITORY_GITHUB>
cd <TEN_THU_MUC_REPO>
```

Hoặc trên GitHub chọn **Code → Download ZIP**, rồi giải nén. Không cần Git CLI khi tải ZIP.

### 5.2. Mở source và restore

1. Mở **`QuanLyThuVien.slnx`** nếu nhánh có file này; nếu không, mở từng project `.csproj` và tạo solution chung trong Visual Studio.
2. Chọn **Restore NuGet Packages** (hoặc để Visual Studio tự restore). API sử dụng `PackageReference`, WPF có `packages.config` kiểu cũ.
3. Đảm bảo project API có **`wwwroot/`**; nếu bị thiếu sau clone, tạo `SmartLibrary.Api/wwwroot/.gitkeep` rồi commit. Git không lưu thư mục hoàn toàn rỗng.
4. Chỉ cấu hình secret trên máy đang chạy API; **không chép connection string vào Git, WPF hay README**.

### 5.3. Cấu hình database cho API (bắt buộc nếu dùng nghiệp vụ có dữ liệu)

Xem [mục 6](#6-cấu-hình-aiven-postgresql). Nếu chỉ có source mà chưa được cấp cấu hình/kết nối DB, giao diện có thể mở nhưng các thao tác đăng nhập, tra sách và mượn trả **không hoạt động đầy đủ**.

### 5.4. Chạy API

Mở terminal tại thư mục chứa `SmartLibrary.Api.csproj`:

```powershell
cd .\SmartLibrary.Api
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet restore
dotnet build
dotnet run
```

API của bản mã hiện tại lắng nghe ở `http://127.0.0.1:5082`. **Giữ terminal API mở**.

Kiểm tra:

```powershell
Invoke-RestMethod http://127.0.0.1:5082/api/health
```

Kết quả thành công có `status: ok` và `database: connected` (số `books` tùy thời điểm). Nếu trả `database: not_configured` hoặc 503, kiểm tra User Secrets/kết nối Aiven.

### 5.5. Chạy WPF

Trong Visual Studio, đặt **`QuanLyThuVien`** làm startup project và nhấn **F5** hoặc **Ctrl+F5**. Mở màn hình đăng nhập, sử dụng tài khoản đã được Admin/nhóm dự án cấp hoặc luồng đăng ký có sẵn.

Nếu muốn debug song song, có thể cấu hình **Multiple startup projects**: API khởi động trước (hoặc cùng lúc), WPF sau.

> **Không cần chạy SQL seed mỗi lần clone** nếu API đang kết nối tới database Aiven đã có sẵn schema/dữ liệu. Không tự chạy script `DROP`, `TRUNCATE`, reset hay dữ liệu mẫu lên database dùng chung.

## 6. Cấu hình Aiven PostgreSQL

### 6.1. Người clone có dùng chung Aiven của chủ dự án không?

**Không tự động.** `git clone` chỉ tải **mã nguồn**, không bao gồm database đang chạy, mật khẩu, dữ liệu tài khoản hay quyền quản trị. Hai lựa chọn:

**A. Phát triển local (kiến trúc hiện tại):** mỗi người chạy **API riêng trên máy mình**, Admin cấp tài khoản PostgreSQL có quyền phù hợp, rồi mỗi máy lưu connection string vào User Secrets. Khi cùng kết nối tới một database, **mọi thao tác ghi đều tác động lên dữ liệu chung**. Chỉ cấp quyền này cho thành viên tin cậy; ưu tiên database riêng cho thử nghiệm để tránh sửa nhầm dữ liệu thực.

**B. Người sử dụng bình thường (kiến trúc nên hướng tới):** triển khai **một API chung** trên máy chủ có HTTPS, API giữ toàn bộ thông tin Aiven; các bản WPF gọi API chung. Khi đó người dùng **không cần và không được nhận** mật khẩu Aiven. Cách B cần chỉnh cấu hình URL WPF và các giới hạn Development/localhost trong source hiện tại; **chưa phải hành vi mặc định của bản mã đang có**.

### 6.2. Cấu hình bằng User Secrets

Trên **máy chạy API**, mở PowerShell tại `SmartLibrary.Api`:

```powershell
cd .\SmartLibrary.Api

dotnet user-secrets set "ConnectionStrings:AivenPostgres" "Host=<AIVEN_HOST>;Port=<AIVEN_PORT>;Database=defaultdb;Username=<DB_USER>;Password=<DB_PASSWORD>;SSL Mode=Require"
```

- Lấy host, port, username và password từ trang quản lý Aiven hoặc người quản lý dự án. **Không có thông tin này thì không thể tự truy cập DB của chủ dự án.**
- Sử dụng **user DB có quyền tối thiểu**, không chia sẻ tài khoản chủ sở hữu/quản trị nếu không cần.
- User Secrets dành cho **Development**, không phải cơ chế lưu secret production. Khi triển khai server, sử dụng secret manager/biến môi trường phù hợp.
- Nếu mật khẩu DB đã xuất hiện trong `App.config`, source hoặc lịch sử Git: **đổi mật khẩu trên Aiven** và xóa khỏi lịch sử được chia sẻ. Chỉ xóa trong commit mới không làm bí mật cũ biến mất.

Có thể kiểm tra tên khóa đã lưu (cẩn thận, lệnh có thể hiện bí mật): `dotnet user-secrets list`. **Không gửi kết quả có mật khẩu qua chat hoặc commit log chứa secret.**

### 6.3. Schema và dữ liệu

Cơ sở dữ liệu Aiven hiện dùng `defaultdb.public`; dự án có mô hình sách/ISBN, bảng `book_copies` (mỗi cuốn có mã riêng), Reader/Staff/Admin, phiếu mượn, thông báo, đánh giá, `book_metadata` và các bảng quản trị. **Chỉ dựng database mới từ migration/seed đã được kiểm tra và được chủ dự án phê duyệt**; các script seed lịch sử không nên chạy trên database đã có dữ liệu.

## 7. Cloudinary và ảnh bìa sách

**Đã chuyển từ Cloudflare R2 sang Cloudinary**; **không dùng Cloudflare D1**. Ảnh bìa ở Cloudinary, `books.cover_url` lưu URL HTTPS và `books.cover_object_key` lưu public ID. Người dùng chỉ xem ảnh **không cần Cloudinary API Key/Secret**; thông tin upload chỉ nằm trên máy chủ API.

### Chỉ cần xem ảnh

Nếu DB hiện có URL dạng `https://res.cloudinary.com/...`, máy WPF có Internet và tài nguyên Cloudinary đang được phép truy cập công khai, ảnh có thể hiển thị mà **không phải tải file ảnh local**. Các chức năng thêm/sửa bìa vẫn cần API được cấu hình Cloudinary.

### Staff/Admin thêm hoặc thay ảnh

Đăng ký Cloudinary: <https://cloudinary.com/users/register_free>. Trên **máy chạy API**, lưu secret:

```powershell
cd .\SmartLibrary.Api

dotnet user-secrets set "Cloudinary:CloudName" "<CLOUD_NAME>"
dotnet user-secrets set "Cloudinary:ApiKey" "<API_KEY>"
dotnet user-secrets set "Cloudinary:ApiSecret" "<API_SECRET>"
```

Khởi động lại API. Trong WPF, vào quản lý đầu sách, **Chọn ảnh** (JPG/PNG/WebP theo giới hạn endpoint), xem trước và **Lưu đầu sách**. Backend xử lý upload và cập nhật URL vào Aiven. **Không cấu hình secret trực tiếp trên client WPF.**

> Chuyển 38/38 ảnh sang Cloudinary đã được kiểm tra trên Aiven ở thời điểm 05/11/2026. **Không chạy lại script di chuyển ảnh** nếu URL đã là Cloudinary. Chỉ sử dụng migration nếu có dữ liệu ảnh local cũ thực sự cần chuyển và sau khi đã sao lưu.

### Thư mục `wwwroot`

Backend vẫn sử dụng static web assets trong Development. Dù toàn bộ ảnh đã lên cloud, **giữ thư mục `SmartLibrary.Api/wwwroot/`** để tránh `DirectoryNotFoundException` khi khởi động. Commit `wwwroot/.gitkeep` để Git giữ thư mục.

## 8. Tùy chọn AI với LM Studio

AI là **phần mở rộng**, không bắt buộc để người dùng tra cứu sách và sử dụng các nghiệp vụ cơ bản. Nếu bỏ LM Studio, chatbot, tìm kiếm ngữ nghĩa theo truy vấn mới và tự cập nhật embedding **có thể không sẵn sàng**; các dữ liệu sách, tồn kho, mượn/trả vẫn do API/Aiven xử lý.

### 8.1. Cài và chạy model

1. Tải [LM Studio](https://lmstudio.ai/) trên máy chạy **API**.
2. Tải model **chat** được hỗ trợ, ví dụ ID đã dùng trong quá trình phát triển: `unsloth/qwen3.5-4b`.
3. Tải model **embedding** đã kiểm thử: `text-embedding-qwen3-embedding-0.6b` (**1024 chiều**).
4. Vào **Developer → Local Server → Running**; địa chỉ mặc định `http://127.0.0.1:1234`.
5. Xem ID thật của các model đang mở:

   ```powershell
   (Invoke-RestMethod http://127.0.0.1:1234/v1/models).data | Select-Object id
   ```

### 8.2. Cấu hình API

```powershell
cd .\SmartLibrary.Api

dotnet user-secrets set "LMStudio:BaseUrl" "http://127.0.0.1:1234"
dotnet user-secrets set "LMStudio:Model" "unsloth/qwen3.5-4b"
dotnet user-secrets set "LMStudio:EmbeddingModel" "text-embedding-qwen3-embedding-0.6b"
```

Khởi động lại API. Nếu dùng model embedding khác, **không tự ý đổi khi DB đang có vector của model cũ**: cần đảm bảo số chiều, cách lập chỉ mục và dữ liệu đã lưu tương thích.

### 8.3. Semantic Search và tự cập nhật embedding

Sau các bản nâng cấp gần nhất:

- API có endpoint **reindex** và **semantic search** theo quyền.
- Worker có thể tự phát hiện sách mới/sửa nội dung, tạo embedding nếu LM Studio và Aiven hoạt động; tính năng này chỉ chạy trong **tiến trình API đang mở**.
- Đã có lần kiểm thử reindex **38/38**, `failed=0`, truy vấn ngữ nghĩa trả kết quả; có kiểm thử chỉnh mô tả sách làm đổi `embedding_source_hash`.
- Trường `book_metadata.embedding` của môi trường thử nghiệm là `vector(1024)`. **Không cần chạy lại SQL tạo vector nếu dùng chính DB đã cấu hình.**

Không cần reindex mỗi lần thêm sách nếu backend tự động đang chạy đúng. Trường hợp không có AI, các phần phụ thuộc model cần báo trạng thái thay vì bịa câu trả lời hoặc số liệu.

## 9. Quản trị database bằng DBeaver

Chỉ dành cho người được cấp quyền làm việc trực tiếp với database; **không phải yêu cầu cho người dùng WPF**.

1. Tải <https://dbeaver.io/download/>; thêm kết nối **PostgreSQL**.
2. Nhập host/port/DB/user và cấu hình SSL theo trang kết nối Aiven.
3. Dùng tài khoản chỉ đọc nếu chỉ cần kiểm tra dữ liệu.
4. Một số kiểm tra an toàn:

```sql
-- Số đầu sách đang hiển thị
SELECT COUNT(*) AS visible_books FROM public.books WHERE is_visible = TRUE;

-- Đầu sách và tổng cuốn vật lý
SELECT b.book_id, b.title, COUNT(c.copy_id) AS physical_copies
FROM public.books b
LEFT JOIN public.book_copies c ON c.book_id=b.book_id
GROUP BY b.book_id, b.title
ORDER BY b.book_id;

-- Số metadata đã có embedding
SELECT COUNT(*) AS books_with_embedding
FROM public.book_metadata WHERE embedding IS NOT NULL;

-- Tình trạng URL bìa
SELECT COUNT(*) FILTER (WHERE cover_url LIKE 'https://res.cloudinary.com/%') AS cloudinary_covers,
       COUNT(*) FILTER (WHERE cover_url LIKE '/covers/%') AS local_covers
FROM public.books WHERE is_visible = TRUE;
```

Không sao chép database sản xuất hoặc dữ liệu người dùng thật sang môi trường công khai khi chưa được phép. Aiven backup database **không đồng nghĩa** đã sao lưu mọi tệp ảnh bên ngoài (Cloudinary).

## 10. Sử dụng ứng dụng theo vai trò

1. **Reader:** đăng nhập/đăng ký hợp lệ → trang chủ → tìm sách, xem ảnh/mô tả/tình trạng → sử dụng yêu cầu mượn/thông báo/lịch sử và tìm kiếm theo quyền.
2. **Staff:** đăng nhập nhân viên → quản lý đầu sách/cuốn, nhập kho, ảnh bìa, xử lý mượn trả và khách tại quầy; các thao tác cập nhật thực hiện qua API.
3. **Admin:** đăng nhập quản trị → quản lý chính sách, người dùng/nhân viên, báo cáo và phần chức năng quản trị hiện có.
4. **AI:** nếu LM Studio đang chạy, sử dụng trợ lý để hỏi bằng tiếng Việt; câu hỏi về tồn kho, lượt mượn, đánh giá hoặc nhân sự phải dựa trên truy vấn có quyền, không phải câu trả lời do model tự đoán.

Không chia sẻ tài khoản Admin trong README hoặc bản phát hành; Admin tạo/cấp tài khoản phù hợp cho người dùng trong môi trường được phép.

## 11. Người clone có dùng chung database được không?

**Có, nếu được người quản lý cấp quyền đúng cách; không phải vì clone Git là được tự động dùng.**

| Tình huống | Kết quả |
|---|---|
| Clone source nhưng chưa cấu hình DB | API có thể build, nhưng phần nghiệp vụ cần DB không chạy đầy đủ |
| Clone source + API local + User Secrets Aiven hợp lệ | Sử dụng **cùng database**, mọi thay đổi cùng ảnh hưởng dữ liệu |
| Chỉ chạy WPF, không có API trên `127.0.0.1:5082` | Không đăng nhập/tra cứu qua API được |
| WPF kết nối tới một **API chung đã triển khai** | Có thể sử dụng chung DB mà **không cấp password database cho người dùng WPF** |
| Mỗi thành viên cấu hình **database riêng** | Độc lập, cần chuẩn bị schema/dữ liệu riêng |

Khuyến nghị: **nhóm phát triển dùng database thử nghiệm hoặc tài khoản DB giới hạn quyền**; người chỉ dùng app nên đi qua API chung, không cài DBeaver và không biết credentials Aiven.

## 12. Phát hành EXE cho người khác

### 12.1. EXE WPF **không** chứa backend và database

`QuanLyThuVien.exe` chỉ là chương trình giao diện. Khi phát hành phải đi kèm các DLL/`*.exe.config` và tài nguyên liên quan trong thư mục `bin/Release` (hoặc bộ cài đã đóng gói). **Không gửi một EXE đơn lẻ nếu ứng dụng cần các file đi kèm.**

Máy người dùng cần Windows và **.NET Framework runtime tương thích với 4.7.2** (Windows 11 thường có 4.8/4.8.1). Người nhận **không cần Visual Studio** để chạy bản Release được đóng gói đúng.

### 12.2. Hai cách phát hành

**Cách A — demo nội bộ trên từng máy (phù hợp mã hiện tại):**

1. Cài/đóng gói **API .NET 10** trên mỗi máy dùng WPF, chạy ở `127.0.0.1:5082`.
2. Mỗi API được cấp cấu hình Aiven (và Cloudinary upload nếu cần), lưu ở nơi an toàn **trên máy đó**; chỉ nên dùng trên máy tin cậy.
3. Máy phải có **ASP.NET Core Runtime 10** để chạy API framework-dependent (hoặc publish API self-contained).
4. Chạy API trước, sau đó chạy thư mục Release của WPF.

**Cách B — phát hành cho độc giả/nhân viên thông thường (khuyến nghị, chưa được triển khai sẵn):**

1. Triển khai API lên server có **HTTPS**, cấu hình Aiven/Cloudinary và quản lý secret ở server.
2. Refactor WPF để **base URL API có thể cấu hình**, không hardcode `127.0.0.1:5082`.
3. Điều chỉnh các endpoint đang kiểm tra `IsDevelopment()` và bảo mật phù hợp Production; thêm xác thực, CORS nếu cần, hạn chế đăng nhập sai, quản lý session bền vững, log/backup/giám sát.
4. Tạo bộ cài WPF và thử nghiệm trên máy sạch; người dùng chỉ tải/bật app, không cần Aiven credentials hay API local.

> **Quan trọng:** Không thể chỉ đổi `ASPNETCORE_ENVIRONMENT=Production` rồi đưa API lên Internet. Nhiều endpoint hiện trả 404 bên ngoài Development, session đang lưu tạm thời trong RAM và URL WPF được cố định là localhost. Những hạn chế này phải giải quyết trước khi public.

### 12.3. Build Release (dành cho nhà phát triển)

- **WPF:** trong Visual Studio chọn **Release** → **Build QuanLyThuVien**; kiểm tra thư mục `QuanLyThuVien/bin/Release/`.
- **API:** chạy từ thư mục `SmartLibrary.Api`:

```powershell
dotnet publish -c Release -o .\publish
```

Thư mục `publish` là **bản triển khai API**, không phải bộ cài WPF. Với kiểu publish framework-dependent, máy chạy API cần ASP.NET Core Runtime 10. Có thể cân nhắc self-contained khi chuẩn bị cấu hình phát hành riêng; không đưa secret vào output công khai.

## 13. Bảo mật và các hạn chế hiện tại

- **Không commit:** connection string Aiven, password database, Admin password, Cloudinary `ApiSecret`, mật khẩu người dùng, token đăng nhập, local User Secrets, file cấu hình chứa bí mật, dữ liệu cá nhân/thông tin mượn của độc giả.
- **Đổi ngay các khóa đã từng lộ:** xóa secret khỏi file mới **không xóa được lịch sử Git**. Nếu đã commit, rotate Aiven/Cloudinary credential và xử lý lịch sử nguồn/commit công khai theo quy trình thích hợp.
- **Không cho WPF kết nối trực tiếp PostgreSQL:** mọi truy cập dữ liệu nghiệp vụ cần qua API phân quyền. Một số mã hỗ trợ database trực tiếp có thể còn trong cây source cũ; cần loại bỏ trước khi phát hành.
- **Mật khẩu:** backend có triển khai BCrypt; phải rà đầy đủ các luồng tạo/đổi/reset mật khẩu và chống brute-force khi chuẩn bị Production.
- **Guest/CCCD/SĐT:** không đưa thông tin định danh nhạy cảm vào log công khai; cấu hình HMAC và kiểm tra quy trình xác minh trước khi dùng với dữ liệu thật.
- **Môi trường:** bản mã đã kiểm tra chủ yếu giới hạn Development/localhost; chưa được đánh giá bảo mật hoặc kiểm thử tải cho môi trường Internet.
- **Cloudinary:** URL ảnh công khai không phải nơi lưu dữ liệu nhạy cảm; ảnh cũ trên Cloudinary hiện không nhất thiết tự được xóa khi đổi bìa.
- **Không kiểm tra hết bằng build:** build thành công không chứng minh mọi nghiệp vụ, thanh toán/phạt, phân quyền và an toàn dữ liệu đều đúng.

## 14. Khắc phục sự cố

| Hiện tượng | Nguyên nhân phổ biến / cách xử lý |
|---|---|
| WPF báo không kết nối API | Kiểm tra API chạy ở `http://127.0.0.1:5082` trên **cùng máy**; thử `/api/health`. |
| `dotnet build` xong mà app chưa chạy | `build` chỉ biên dịch; phải **`dotnet run`** hoặc chạy trong Visual Studio. |
| `/api/health` 503 / DB `not_configured` | Chưa lưu `ConnectionStrings:AivenPostgres` hoặc không mở được kết nối Aiven. |
| WPF thiếu .NET Framework targeting pack | Cài Developer Pack **4.7.2** hoặc chọn thành phần tương ứng trong Visual Studio Installer. |
| API không build vì .NET 10 | Cài **.NET 10 SDK**, dùng **Visual Studio 2026 18.0+**. |
| Lỗi NuGet | Restore lại, kiểm tra mạng/NuGet.org; WPF là project `packages.config` kiểu cũ. |
| `DirectoryNotFoundException` ở `wwwroot` | Tạo `SmartLibrary.Api/wwwroot/.gitkeep`, rebuild/restart. |
| Ảnh không hiện | Kiểm tra `books.cover_url` và mở thử HTTPS Cloudinary; kiểm tra ảnh còn public và Internet hoạt động. |
| Không upload được ảnh | Kiểm tra `Cloudinary:CloudName`, `ApiKey`, `ApiSecret` trên máy **API**; chỉ Staff/Admin được phép. |
| Chatbot trả lời thiếu/từ chối | Kiểm tra LM Studio Local Server, model ID, API đã dùng bản service mới; không suy ra các truy vấn được hỗ trợ 100%. |
| Vector không tự cập nhật | Kiểm tra worker auto-indexer có trong API, `LMStudio:EmbeddingModel`, model đã load, vector DB 1024 chiều và log API. |
| Mới clone, đăng nhập không được | Chưa cấu hình Aiven, không có tài khoản hợp lệ hoặc API chạy sai môi trường/cổng. |
| Chạy EXE trên máy khác nhưng không có dữ liệu | Không có API local tại `127.0.0.1:5082`; phải có API local đã cấu hình hoặc triển khai API chung. |

## 15. Đóng góp, trạng thái kiểm thử và tài liệu

### Quy trình làm việc đề xuất

1. Tạo branch tính năng; không commit khóa bí mật hoặc dữ liệu cá nhân.
2. Restore dependencies; build cả `SmartLibrary.Api` và `QuanLyThuVien`.
3. Chạy trên database thử nghiệm; kiểm tra các vai trò Reader/Staff/Admin theo quyền.
4. Nếu thay schema, tạo migration riêng có khả năng kiểm tra và sao lưu trước khi chạy; tránh reset DB đang dùng.
5. Kiểm thử API, UI, upload Cloudinary, AI nếu có, rồi review/merge pull request.

### Tình trạng kiểm thử được ghi nhận

- ✅ Người vận hành xác nhận **38/38 embedding**, kết quả reindex `failed=0` và có trả kết quả semantic search.
- ✅ Đã thấy `embedding_source_hash` thay đổi khi sửa mô tả sách (đã có kiểm thử tự cập nhật ở máy vận hành).
- ✅ Đã xác nhận **38/38 đường dẫn ảnh Cloudinary**, 0 đường dẫn local trong dữ liệu sách đang hiển thị.
- ✅ Bản API Cloudinary đã được báo **build thành công** trên máy Windows của người vận hành.
- ⚠️ Chưa có bằng chứng kiểm thử **bản cài EXE trên máy khác** hoặc triển khai API Production HTTPS; không ghi đây là chức năng đã hoàn tất.

### Tài liệu liên quan

- [DEPENDENCIES.md](DEPENDENCIES.md) — phần mềm cần cài, NuGet chính xác, runtime, cách kiểm tra máy.
- [Microsoft: Cài .NET trên Windows](https://learn.microsoft.com/dotnet/core/install/windows)
- [Microsoft: .NET Framework Developer Pack](https://learn.microsoft.com/dotnet/framework/install/guide-for-developers)
- [Cloudinary Free](https://cloudinary.com/pricing)

---

**Phạm vi tài liệu:** dựa trên các tệp solution/project trong bản ZIP mới nhất đã cung cấp và các bản nâng cấp thực tế được xác nhận sau đó (Cloudinary, semantic search, tự động embedding, chatbot). Các đặc điểm không có mã hoặc chưa được thử trên máy thứ hai được ghi là hạn chế/thao tác cần thực hiện, không coi là hoàn tất. Không có giấy phép sử dụng mã nguồn (`LICENSE`) được xác nhận trong nguồn đã kiểm tra; trước khi công khai, chủ repository cần quyết định điều khoản cấp phép.
