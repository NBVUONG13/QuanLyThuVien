# Smart Library — Dependencies & Setup Matrix

> Tài liệu kỹ thuật đi kèm [README.md](README.md). Đối chiếu từ `QuanLyThuVien-master.zip`, `QuanLyThuVien.csproj`, `packages.config`, `SmartLibrary.Api.csproj` và bản cập nhật backend **Cloudinary Free**. Kiểm tra lại các dependency nếu repository được sửa tiếp.

## 1. Môi trường mục tiêu

| Thành phần | Phiên bản/điều kiện | Cần ở đâu? |
|---|---|---|
| Hệ điều hành | **Windows**, khuyến nghị Windows 10/11 (WPF) | Máy chạy desktop |
| IDE | **Visual Studio 2026** phiên bản **18.0+** | Máy build cả WPF + API |
| Visual Studio workloads | **.NET desktop development** và **ASP.NET and web development** | Máy phát triển |
| .NET SDK | **10.x** (dự án API target `net10.0`) | Máy build API |
| .NET Framework Developer/Targeting Pack | **4.7.2** (WPF target `v4.7.2`) | Máy build WPF |
| .NET Framework runtime | **4.7.2 trở lên** tương thích; Windows 11 thường có 4.8/4.8.1 | Máy chạy WPF Release |
| ASP.NET Core Runtime | **10.x** nếu chạy API publish framework-dependent | Máy chạy API khi không có SDK |
| Git | Bất kỳ bản Git Windows được hỗ trợ | Chỉ khi dùng `git clone` |
| NuGet | Visual Studio tích hợp, truy cập NuGet.org | Build/restore |
| Aiven PostgreSQL | Endpoint/credential của môi trường dự án | Máy chủ API; không yêu cầu cài PostgreSQL cục bộ |
| Cloudinary | Cloud name + API credentials để **upload**; URL HTTPS để **xem** | API dùng credentials; WPF không có credentials |
| LM Studio | Tùy chọn; Local Server OpenAI-compatible | Máy chạy tính năng AI/backend |
| DBeaver | Tùy chọn | Người quản lý DB |

**Chú ý:** VS 2022 không phải tổ hợp được hỗ trợ để target .NET 10. WPF .NET Framework 4.7.2 là project **kiểu cũ**; `dotnet build` từ terminal chỉ là cách đơn giản cho **API**, còn WPF nên build bằng Visual Studio/MSBuild có đúng targeting pack.

Nguồn chính thức: [Visual Studio](https://visualstudio.microsoft.com/downloads/), [.NET SDK / Visual Studio](https://learn.microsoft.com/dotnet/core/install/windows), [.NET Framework targeting pack](https://learn.microsoft.com/dotnet/framework/install/guide-for-developers), [.NET 10 download](https://dotnet.microsoft.com/download/dotnet/10.0).

## 2. NuGet — SmartLibrary.Api (.NET 10)

Các PackageReference trong file `.csproj` bản **Cloudinary Free**:

| Package ID | Phiên bản trong project | Vai trò |
|---|---|---|
| `BCrypt.Net-Next` | `4.2.0` | Hash và xác minh mật khẩu trong backend |
| `Npgsql` | `10.0.3` | Driver PostgreSQL cho .NET 10 |

ASP.NET Core dùng **`Microsoft.NET.Sdk.Web`** (framework reference theo SDK), nên không có PackageReference riêng cho các assembly ASP.NET Core phổ biến.

**Cloudinary hiện được gọi qua REST/HTTPS bằng `HttpClient`** trong `CloudinaryBookCoverStorage.cs`; **không cần SDK Cloudinary NuGet**. Gói `AWSSDK.S3` dành cho thử nghiệm R2 cũ **không còn cần trong API** sau khi thay `.csproj` Cloudinary. Nếu vẫn để lại `R2BookCoverStorage.cs` mà project không còn tham chiếu AWS, sẽ có thể lỗi biên dịch — loại bỏ file R2 cũ khỏi API trước khi build.

**Cách khôi phục:**

```powershell
cd SmartLibrary.Api
dotnet restore
dotnet build
```

**Không cài** `AWSSDK.S3` hoặc `CloudinaryDotNet` vào API trừ khi có thay đổi mã nguồn yêu cầu rõ ràng.

## 3. NuGet — QuanLyThuVien WPF (.NET Framework 4.7.2)

Danh sách có trong **`QuanLyThuVien/packages.config`** của bản ZIP đã đối chiếu (có cả thư viện lịch sử/có thể chưa được sử dụng). Gói được **khai báo trong `packages.config` không chứng minh code hiện tại đang gọi gói đó**; file `.csproj` kiểu cũ phải tham chiếu assembly tương ứng nếu được sử dụng thực tế.

| Package ID | Version trong `packages.config` |
|---|---|
| `AWSSDK.Core` | `4.0.102.8` |
| `AWSSDK.S3` | `4.0.104.1` |
| `Microsoft.Bcl.AsyncInterfaces` | `8.0.0` |
| `Microsoft.Bcl.HashCode` | `1.1.1` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `8.0.0` |
| `Microsoft.Extensions.Logging.Abstractions` | `8.0.0` |
| `Npgsql` | `8.0.5` |
| `Pgvector` | `0.3.2` |
| `System.Buffers` | `4.6.1` |
| `System.Collections.Immutable` | `8.0.0` |
| `System.Configuration.ConfigurationManager` | `11.0.0-rc.1.26425.128` |
| `System.Diagnostics.DiagnosticSource` | `8.0.0` |
| `System.Memory` | `4.6.3` |
| `System.Numerics.Vectors` | `4.6.1` |
| `System.Runtime.CompilerServices.Unsafe` | `6.1.2` |
| `System.Text.Encodings.Web` | `8.0.0` |
| `System.Text.Json` | `8.0.6` |
| `System.Threading.Channels` | `8.0.0` |
| `System.Threading.Tasks.Extensions` | `4.5.4` |
| `System.ValueTuple` | `4.5.0` |

**Nhận xét kỹ thuật:**

- `AWSSDK.Core` / `AWSSDK.S3` là các package cũ còn được liệt kê ở WPF, **không cần cho luồng Cloudinary mới**. Đừng xóa ngẫu nhiên khỏi `packages.config` khi chưa rà lại tham chiếu WPF; sau khi làm sạch mới commit thay đổi vào Git.
- `Npgsql`/`Pgvector` từng xuất hiện ở phía WPF; thiết kế hiện tại nên để **API** truy cập DB. Việc một package có tên trong `packages.config` **không có nghĩa** nên trao database password cho WPF.
- Một package đang ở phiên bản prerelease (`System.Configuration.ConfigurationManager 11.0.0-rc.1.26425.128`); cần đánh giá tương thích nếu làm sạch dependencies, không tự cập nhật hàng loạt ngay trước khi nộp đồ án.
- Version trong tài liệu là **version được chốt ở source**. Không thay thành `latest` trong README vì có thể làm project không build được.

**Restore WPF:** mở solution bằng Visual Studio 2026 → chuột phải Solution → **Restore NuGet Packages** → Rebuild. Với project cũ, lỗi `packages.config` có thể cần NuGet Package Manager/nuget.exe và targeting pack 4.7.2. Không mặc định áp dụng `dotnet add package` cho WPF non-SDK.

## 4. Kiểm tra đã cài đủ chưa?

Mở PowerShell/Visual Studio Developer PowerShell:

```powershell
# Có .NET 10 SDK chưa?
dotnet --list-sdks

# Có ASP.NET Core Runtime 10 trên máy chạy API chưa?
dotnet --list-runtimes

# Git, nếu sử dụng clone
git --version

# Kiểm tra API và PostgreSQL (API cần đang chạy)
Invoke-RestMethod http://127.0.0.1:5082/api/health
```

Nếu SDK hoặc targeting pack chưa được cài, mở **Visual Studio Installer → Modify → Workloads/Individual components**.

Có thể mở Visual Studio → **Help → About** để xác nhận VS 2026 18.0+.

## 5. Cấu hình bắt buộc và tùy chọn của SmartLibrary.Api

| Configuration key | Bắt buộc? | Ví dụ an toàn | Ghi chú |
|---|---|---|---|
| `ConnectionStrings:AivenPostgres` | **Có** để dùng nghiệp vụ DB | `Host=<HOST>;Port=<PORT>;Database=defaultdb;Username=<USER>;Password=<PASS>;SSL Mode=Require` | Chỉ lưu tại backend |
| `Cloudinary:CloudName` | Để upload ảnh | `<CLOUD_NAME>` | Ảnh có URL cloud sẵn vẫn có thể xem |
| `Cloudinary:ApiKey` | Để upload ảnh | `<API_KEY>` | Không commit |
| `Cloudinary:ApiSecret` | Để upload ảnh | `<API_SECRET>` | Không commit |
| `LMStudio:BaseUrl` | Khi bật AI | `http://127.0.0.1:1234` | Model chạy máy API; localhost |
| `LMStudio:Model` | Khi bật chat | `unsloth/qwen3.5-4b` | ID thực tế xem trong LM Studio |
| `LMStudio:EmbeddingModel` | Khi bật embedding/tìm kiếm ngữ nghĩa | `text-embedding-qwen3-embedding-0.6b` | Vector DB đang dùng **1024 chiều** |
| `GuestIdentity:HmacKeyBase64` | Với luồng định danh Guest tương ứng | `<BASE64_CUA_KHOA_BAO_MAT>` | Chỉ định cấu hình theo quy trình Admin; không công khai |
| `BootstrapAdmin:*` | **Chỉ** khi khởi tạo Admin lần đầu có kiểm soát | Không ví dụ password thật | Bootstrap phải được kiểm soát; không dùng lặp lại |

### 5.1. Lưu cấu hình bằng User Secrets (Development)

```powershell
cd SmartLibrary.Api

dotnet user-secrets set "ConnectionStrings:AivenPostgres" "Host=<HOST>;Port=<PORT>;Database=defaultdb;Username=<DB_USER>;Password=<DB_PASSWORD>;SSL Mode=Require"

dotnet user-secrets set "Cloudinary:CloudName" "<CLOUD_NAME>"
dotnet user-secrets set "Cloudinary:ApiKey" "<API_KEY>"
dotnet user-secrets set "Cloudinary:ApiSecret" "<API_SECRET>"

dotnet user-secrets set "LMStudio:BaseUrl" "http://127.0.0.1:1234"
dotnet user-secrets set "LMStudio:Model" "unsloth/qwen3.5-4b"
dotnet user-secrets set "LMStudio:EmbeddingModel" "text-embedding-qwen3-embedding-0.6b"
```

Bỏ qua 5 lệnh Cloudinary/AI nếu chỉ chạy chức năng cơ bản và không upload hay sử dụng model. **Không bỏ kết nối Aiven** nếu muốn đăng nhập và tra cứu dữ liệu.

Thực thi sau khi đã thay placeholder bằng giá trị thực: các giá trị `<...>` trong tài liệu không phải credential thật.

Không đặt dữ liệu thật trong `appsettings.json`, `appsettings.Development.json` (nếu commit Git), WPF config, ảnh chụp màn hình hoặc README. User Secrets chỉ áp dụng cho máy/tài khoản người dùng có cấu hình; **Git clone không copy secret**.

### 5.2. Cấu hình sản xuất

Bản nguồn hiện tại chưa đầy đủ để triển khai công khai. Khi chuyển server Production, dùng secret manager hoặc biến môi trường (ví dụ `ConnectionStrings__AivenPostgres`, `Cloudinary__ApiSecret`) theo hệ thống triển khai; không dựa vào user-secrets trên máy lập trình. Trước đó phải refactor giới hạn `IsDevelopment()`, lắng nghe loopback, URL hardcoded WPF và cơ chế session.

## 6. Dịch vụ ngoài dự án

| Dịch vụ | Có tự được cài khi restore NuGet không? | Ai cần tài khoản? |
|---|---|---|
| **Aiven PostgreSQL** | Không | Người quản lý DB; API được cấp credentials |
| **Cloudinary** | Không | Chủ tài khoản media; backend upload cần key/secret |
| **LM Studio** | Không | Máy cần chạy AI cục bộ |
| **DBeaver** | Không | Tùy chọn cho thành viên quản lý database |

Cloudinary lưu ảnh, **không** thay Aiven. Cloudflare D1/R2 **không cần** cho luồng Cloudinary hiện tại. Người dùng WPF chỉ xem ảnh không cần biết các Cloudinary key.

## 7. Phân phối ứng dụng: Developer và End User

### 7.1. Máy thành viên clone Git và chạy cả hai project

- Bắt buộc: Visual Studio 2026 18.0+, .NET 10 SDK, .NET Framework 4.7.2 targeting pack, NuGet restore; API local có kết nối Aiven.
- Không bắt buộc: DBeaver, LM Studio, Git CLI nếu tải source ZIP.
- Khi chạy API local bằng User Secrets của chủ nhóm: mọi máy dùng **cùng DB** nếu cùng connection string. Cần quyền/DB thử nghiệm để tránh sửa dữ liệu của người khác.

### 7.2. Máy người dùng nhận EXE

- Không cần Visual Studio hoặc SDK nếu bộ phát hành được đóng gói đúng.
- WPF cần Windows, .NET Framework runtime tương thích, toàn bộ DLL/config/tài nguyên đi kèm.
- Bản hiện tại **vẫn cần API chạy trên `127.0.0.1:5082`**; riêng `QuanLyThuVien.exe` không đủ.
- Nếu API được phát hành framework-dependent và chạy trên máy người nhận, cần **ASP.NET Core Runtime 10**; nếu có API chung về sau, không cần runtime API trên máy WPF.
- Bản API hiện tại có nhiều phần giới hạn `Development`, chưa thể tuyên bố đã sẵn sàng deploy public.

### 7.3. Không cấp mật khẩu database cho người chỉ dùng EXE

Để người khác chạy như phần mềm thông thường, **cần thay kiến trúc triển khai**: đưa API lên máy chủ, cấu hình base URL WPF, HTTPS và kiểm soát bảo mật. Hiện GitHub/source không tự tạo khả năng này. **Không bao giờ nhúng connection string Aiven vào EXE hoặc phát hành file User Secrets.**

## 8. Lỗi thường gặp do dependencies

| Lỗi | Cần kiểm tra |
|---|---|
| `NETSDK1045`, target net10 không nhận | .NET 10 SDK và Visual Studio 2026 18.0+ |
| Thiếu .NET Framework reference assemblies | Targeting Pack/Developer Pack 4.7.2 |
| WPF thiếu gói khi mở solution | Restore NuGet `packages.config`, kết nối NuGet.org |
| API lỗi `AWSSDK` sau khi đổi Cloudinary | `.csproj` mới không có AWS: loại `R2BookCoverStorage.cs` cũ |
| `DirectoryNotFoundException ... wwwroot` | Tạo `SmartLibrary.Api/wwwroot/.gitkeep` và commit |
| Cloudinary upload 503 | API chưa có đủ `Cloudinary:*` secret, sai môi trường hoặc không kết nối dịch vụ |
| API `/api/health` báo `not_configured` | Thiếu `ConnectionStrings:AivenPostgres` |
| Không thấy ảnh bìa sau clone | Kiểm tra `books.cover_url` là HTTPS Cloudinary; ảnh có được public không |
| AI không tạo embedding | LM Studio/Local Server, model ID, `vector(1024)`, User Secrets, log worker |
| WPF.exe chạy nhưng không đăng nhập được | API local chưa chạy, không có DB, sai cổng hoặc tài khoản |

## 9. Tài liệu chính thức

- [Microsoft — Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft — .NET Framework Developer Packs](https://learn.microsoft.com/en-us/dotnet/framework/install/guide-for-developers)
- [Visual Studio downloads](https://visualstudio.microsoft.com/downloads/)
- [NuGet](https://learn.microsoft.com/en-us/nuget/)
- [Cloudinary Free](https://cloudinary.com/pricing)
- [Aiven Documentation](https://aiven.io/docs)
- [DBeaver](https://dbeaver.io/download/)
- [LM Studio](https://lmstudio.ai/)

---

**Ghi chú bảo trì:** Dependency list được trích đúng từ các file cấu hình của source đã cung cấp, không phải inventory của toàn bộ runtime trên máy tác giả. Sau khi nhóm commit thêm/bớt NuGet hoặc nâng cấp target framework, cập nhật tài liệu này cùng pull request. Không có yêu cầu bắt buộc dùng PostgreSQL local, Python hay Cloudflare R2/D1 trong cấu hình Cloudinary hiện tại.
