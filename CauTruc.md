# TÀI LIỆU CẤU TRÚC HỆ THỐNG SMART LIBRARY SYSTEM (WPF MVVM)

Hệ thống được xây dựng theo kiến trúc Client-Server hiện đại bằng C# WPF (.NET 8), tuân thủ nghiêm ngặt mô hình MVVM (Model-View-ViewModel). Dưới đây là cấu trúc chi tiết của toàn bộ dự án, chức năng từng file, các hàm cốt lõi và cơ chế tương tác.

---

## 1. SƠ ĐỒ CẤU TRÚC THƯ MỤC VÀ FILE

```text
SmartLibraryApp/
│
├── App.config
├── App.xaml
├── App.xaml.cs
│
├── Models/
│   ├── UserModel.cs
│   ├── BookModel.cs
│   ├── AuthorModel.cs
│   ├── CategoryModel.cs
│   ├── PublisherModel.cs
│   ├── BorrowRecordModel.cs
│   ├── FineModel.cs
│   ├── TimesheetModel.cs
│   ├── SalaryModel.cs
│   └── BookMetadataModel.cs
│
├── DataAccess/
│   └── DatabaseHelper.cs
│
├── Services/
│   ├── AuthService.cs
│   ├── AIService.cs
│   ├── CloudStorageService.cs
│   ├── NotificationService.cs
│   └── DataBackupService.cs
│
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── LoginViewModel.cs
│   ├── DashboardViewModel.cs
│   ├── BookManagementViewModel.cs
│   ├── ReaderManagementViewModel.cs
│   ├── BorrowReturnViewModel.cs
│   ├── FineManagementViewModel.cs
│   ├── SearchViewModel.cs
│   ├── AIChatViewModel.cs
│   ├── HRManagementViewModel.cs
│   └── DataManagementViewModel.cs
│
└── Views/
    ├── LoginWindow.xaml
    ├── MainWindow.xaml
    ├── DashboardView.xaml
    ├── BookManagementView.xaml
    ├── ReaderManagementView.xaml
    ├── BorrowReturnView.xaml
    ├── FineManagementView.xaml
    ├── SearchView.xaml
    ├── AIChatView.xaml
    ├── HRManagementView.xaml
    └── DataManagementView.xaml
```

---

## 2. CHỨC NĂNG TỪNG FILE VÀ CÁC HÀM DỰ KIẾN

### 2.1. Tập tin cấu hình hệ thống (Root)
*   **`App.config`**: Lưu trữ các thông số môi trường như Connection String tới Aiven PostgreSQL, Port của LM Studio, API Keys của Cloudflare R2.
*   **`App.xaml / App.xaml.cs`**: Điểm neo khởi chạy ứng dụng. 
    *   *Hàm chính*: `OnStartup()`. Định nghĩa `LoginWindow` là màn hình khởi động. Chứa các Resource Dictionary (Màu sắc, Styles) dùng chung cho toàn bộ giao diện.

### 2.2. Tầng Models (Dữ liệu)
Chứa các class POCO (Plain Old CLR Object) ánh xạ 1-1 với các bảng trong PostgreSQL. Tham gia vào việc luân chuyển dữ liệu giữa Database và ViewModel.
*   **`UserModel.cs`**: Lưu `UserId`, `Username`, `PasswordHash`, `Role`, `HourlyRate`.
*   **`BookModel.cs`**: Lưu `BookId`, `Title`, `ISBN`, `CoverImageUrl` (link R2), `AvailableQuantity`.
*   **`AuthorModel.cs`**: Lưu `AuthorId`, `AuthorName`, `Biography`.
*   **`CategoryModel.cs`**: Lưu `CategoryId`, `CategoryName`, `ParentId` (cây phân cấp).
*   **`PublisherModel.cs`**: Lưu `PublisherId`, `PublisherName`, `ContactInfo`.
*   **`BorrowRecordModel.cs`**: Lưu `BorrowId`, `UserId`, `BookId`, `BorrowDate`, `DueDate`, `ReturnDate`, `Status`.
*   **`FineModel.cs`**: Lưu `FineId`, `UserId`, `BorrowId`, `Amount`, `Reason`, `IsPaid`.
*   **`TimesheetModel.cs`**: Lưu `TimesheetId`, `UserId`, `CheckInTime`, `CheckOutTime`, `TotalHours`.
*   **`SalaryModel.cs`**: Lưu `SalaryId`, `UserId`, `MonthYear`, `TotalAmount`.
*   **`BookMetadataModel.cs`**: Lưu `MetadataId`, `BookId`, `Summary`, và đặc biệt là mảng `float[] Embedding` (768 chiều cho AI).

### 2.3. Tầng DataAccess (Kết nối CSDL)
*   **`DatabaseHelper.cs`**: Chịu trách nhiệm giao tiếp vật lý với Aiven PostgreSQL.
    *   `GetConnection()`: Trả về đối tượng `NpgsqlConnection`.
    *   `ExecuteQueryAsync(string sql, parameters)`: Thực thi lệnh SELECT, trả về `DataTable`.
    *   `ExecuteNonQueryAsync(string sql, parameters)`: Thực thi INSERT, UPDATE, DELETE.
    *   `ExecuteScalarAsync(string sql, parameters)`: Trả về 1 giá trị (VD: COUNT).

### 2.4. Tầng Services (Xử lý API & Dịch vụ)
Cung cấp các tác vụ xử lý nền, độc lập với giao diện.
*   **`AuthService.cs`**: 
    *   `LoginAsync(username, password)`: Hash pass (BCrypt) và so sánh, trả về `UserModel` nếu đúng.
    *   `GetCurrentUserRole()`: Lấy quyền của phiên đăng nhập hiện tại.
*   **`AIService.cs`**: 
    *   `SendChatRequestAsync(prompt, tools)`: Gọi HTTP POST đến LM Studio port 1234, xử lý JSON trả về (Function Call hoặc Text).
    *   `GetEmbeddingAsync(text)`: Gọi model `nomic-embed-text-v1.5`, lấy mảng vector phục vụ semantic search.
*   **`CloudStorageService.cs`**: 
    *   `UploadImageAsync(filePath)`: Dùng AWSSDK.S3 đẩy ảnh lên Cloudflare R2, trả về Public URL.
*   **`NotificationService.cs`**: 
    *   `CheckOverdueBooks()`: Quét DB và tạo cảnh báo nợ/phạt.
*   **`DataBackupService.cs`**: 
    *   `CreateBackupAsync()`: Gọi tool pg_dump để xuất file `.sql`.
    *   `RestoreBackupAsync(filePath)`: Gọi psql để phục hồi dữ liệu.

### 2.5. Tầng ViewModels (Não bộ xử lý logic)
Áp dụng interface `INotifyPropertyChanged` và sử dụng `ICommand` để gắn sự kiện. Dữ liệu danh sách luôn dùng `ObservableCollection<T>`.
*   **`MainViewModel.cs`**:
    *   *Thuộc tính*: Các biến `Visibility` (vd: `IsAdminVisible`, `IsStaffVisible`) để ẩn/hiện menu phụ thuộc Role.
    *   *Hàm*: `NavigateCommand` (Đổi View hiện tại).
*   **`LoginViewModel.cs`**:
    *   *Hàm*: `LoginCommand` -> Gọi `AuthService.LoginAsync()`. Thành công thì mở `MainWindow` và đóng `LoginWindow`.
*   **`DashboardViewModel.cs`**:
    *   *Hàm*: `LoadStatistics()` -> Gọi DB đếm tổng sách, tổng tiền phạt, vẽ biểu đồ.
*   **`BookManagementViewModel.cs`**:
    *   *Thuộc tính*: `ObservableCollection<BookModel> Books`.
    *   *Hàm*: `AddBookCommand`, `UpdateBookCommand`, `UploadCoverCommand` (gọi `CloudStorageService`).
*   **`ReaderManagementViewModel.cs`**:
    *   *Hàm*: `LoadReadersCommand`, `ViewBorrowHistoryCommand`.
*   **`BorrowReturnViewModel.cs`**:
    *   *Hàm*: `CreateBorrowRecordCommand` (trừ AvailableQuantity), `ReturnBookCommand` (cộng AvailableQuantity, check trễ hạn -> tạo Fine).
*   **`FineManagementViewModel.cs`**:
    *   *Hàm*: `CalculateFineCommand`, `PayFineCommand`.
*   **`SearchViewModel.cs`**:
    *   *Hàm*: `ExecuteSearchCommand` (Tạo câu SQL động WHERE theo các filter).
*   **`AIChatViewModel.cs`**:
    *   *Thuộc tính*: `ChatHistory`, `RecommendedBooks`.
    *   *Hàm*: `SendMessageCommand` -> Gọi `AIService`. Nếu AI trigger `semantic_search()`, lấy embedding rồi query pgvector (`<=>`) qua `DatabaseHelper`, nạp kết quả vào `RecommendedBooks`.
*   **`HRManagementViewModel.cs`**:
    *   *Hàm*: `CheckInCommand`, `CheckOutCommand`, `CalculateSalaryCommand` (Tổng giờ x HourlyRate).
*   **`DataManagementViewModel.cs`**:
    *   *Hàm*: `ExecuteBackupCommand`, `ExecuteRestoreCommand` (gọi `DataBackupService`).

### 2.6. Tầng Views (Giao diện UI)
Chỉ chứa mã XAML, không chứa logic trong Code-behind (`.xaml.cs`), sử dụng cơ chế `DataContext = ViewModel`.
*   **`LoginWindow.xaml`**: Form điền Username/Password.
*   **`MainWindow.xaml`**: Chứa Sidebar Navigation và phần ContentControl (để load các View con).
*   **`DashboardView.xaml`**: Biểu đồ thống kê.
*   **`BookManagementView.xaml`**: Có DataGrid liệt kê sách, Button "Upload Ảnh", Image tag bind tới `CoverImageUrl`.
*   **`ReaderManagementView.xaml`**: Form đăng ký độc giả.
*   **`BorrowReturnView.xaml`**: Nhập mã sách, mã thẻ để mượn/trả.
*   **`FineManagementView.xaml`**: Danh sách hóa đơn phạt.
*   **`SearchView.xaml`**: Textbox và ComboBox lọc tìm kiếm.
*   **`AIChatView.xaml`**: Khung chat (ListBox) và vùng hiển thị sách AI gợi ý.
*   **`HRManagementView.xaml`**: Nút bấm Check-in/out to, bảng chấm công DataGrid.
*   **`DataManagementView.xaml`**: Các nút Backup, Restore.

---

## 3. CƠ CHẾ TƯƠNG TÁC GIỮA CÁC TẦNG (WORKFLOW)

Để hiểu rõ cách các file liên kết, ta xem xét luồng **Quản lý phiếu mượn (Borrow Book)**:

1.  **View (`BorrowReturnView.xaml`)**: Thủ thư nhập Mã sách và Mã độc giả, bấm nút "Tạo Phiếu". Nút này được Data Binding với `CreateBorrowRecordCommand` trong ViewModel.
2.  **ViewModel (`BorrowReturnViewModel.cs`)**:
    *   Nhận lệnh `CreateBorrowRecordCommand`.
    *   Kiểm tra tính hợp lệ (độc giả có bị khóa thẻ không, sách còn `AvailableQuantity > 0` không bằng cách gọi `DatabaseHelper.ExecuteQueryAsync`).
    *   Tạo đối tượng `BorrowRecordModel` với `BorrowDate = DateTime.Now`.
    *   Gọi `DatabaseHelper.ExecuteNonQueryAsync` 2 lệnh trong 1 Transaction:
        *   `INSERT INTO Borrows...`
        *   `UPDATE Books SET AvailableQuantity = AvailableQuantity - 1 WHERE BookId = ...`
    *   Cập nhật lại danh sách trên UI bằng cách thêm phiếu mượn mới vào `ObservableCollection`.
3.  **Database (Aiven PostgreSQL)**: Thực thi lưu trữ vĩnh viễn.

*Cơ chế tương tự được áp dụng cho toàn bộ các chức năng khác (AI, Upload ảnh, HR), đảm bảo UI luôn không bị đứng (freeze) bằng cách sử dụng các hàm bất đồng bộ `async/await` ở tầng Service và DatabaseHelper.*