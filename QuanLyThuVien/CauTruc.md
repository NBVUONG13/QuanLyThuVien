# KIẾN TRÚC HỆ THỐNG: QUẢN LÝ THƯ VIỆN & NHÂN SỰ TÍCH HỢP AI
**(SMART LIBRARY SYSTEM - WPF MVVM)**

Dự án tuân thủ nghiêm ngặt mô hình thiết kế Client-Server với cấu trúc MVVM (Model-View-ViewModel) trên nền tảng .NET 8, tách biệt hoàn toàn giao diện, logic nghiệp vụ, lưu trữ dữ liệu và xử lý AI.

## 1. SƠ ĐỒ CẤU TRÚC THƯ MỤC & FILE
```text
SmartLibraryApp/
├── App.config                     <-- Lưu Connection String (Aiven PostgreSQL) và Access Keys.
├── App.xaml / App.xaml.cs         <-- Cấu hình Resource dùng chung, thiết lập LoginWindow khởi chạy đầu tiên.
│
├── Models/                        <-- (Tầng Dữ liệu - Ánh xạ DB)
│   ├── UserModel.cs               <-- Thuộc tính: UserId, Username, Role, HourlyRate.
│   ├── BookModel.cs               <-- Thuộc tính: BookId, Title, ISBN, CoverImageUrl, AvailableQuantity.
│   ├── TimesheetModel.cs          <-- Thuộc tính: Giờ Check-in/Check-out, TotalHours.
│   ├── SalaryModel.cs             <-- Thuộc tính: Lương tháng tính từ TotalHours * HourlyRate.
│   └── BookMetadataModel.cs       <-- Thuộc tính: Summary, Keywords, Embedding (Vector 768 chiều).
│
├── DataAccess/                    <-- (Tầng Kết nối CSDL)
│   └── DatabaseHelper.cs          <-- Quản lý kết nối Npgsql tới PostgreSQL.
│
├── Services/                      <-- (Tầng Dịch vụ & Giao tiếp API)
│   ├── AuthService.cs             <-- Mã hóa BCrypt, xác thực User, lưu Session.CurrentUser.
│   ├── CloudStorageService.cs     <-- Dùng AWSSDK.S3 tải ảnh bìa lên Cloudflare R2, trả về public URL.
│   └── AIService.cs               <-- Gọi HTTP/RestSharp tới port 1234 (LM Studio) cho Chat & Embedding.
│
├── ViewModels/                    <-- (Tầng Logic / Não bộ - MVVM)
│   ├── BaseViewModel.cs           <-- Kế thừa INotifyPropertyChanged.
│   ├── MainViewModel.cs           <-- Điều hướng menu, bind Visibility dựa theo Role của Session.
│   ├── BookManagementViewModel.cs <-- Bind ObservableCollection<BookModel>, xử lý Thêm/Sửa/Xóa.
│   ├── HRManagementViewModel.cs   <-- Xử lý Check-in/out, tải bảng lương từ DB.
│   └── AIChatViewModel.cs         <-- Gửi tin nhắn, bóc tách Function Calling, xử lý RAG search.
│
└── Views/                         <-- (Tầng Giao diện UI - Chỉ chứa XAML)
    ├── LoginWindow.xaml           <-- Bind với LoginViewModel.
    ├── MainWindow.xaml            <-- Bind với MainViewModel.
    ├── BookManagementView.xaml    <-- DataGrid bind danh sách sách, Image bind với CoverImageUrl.
    ├── HRManagementView.xaml      <-- Giao diện chấm công (Staff) và xem lương (Admin).
    └── AIChatView.xaml            <-- Khung chat AI, listbox hiển thị kết quả gợi ý.
```

## 2. CHỨC NĂNG CÁC HÀM CỐT LÕI VÀ TƯƠNG TÁC

### 2.1. Tầng DataAccess & Services (Xử lý nền)
*   **`DatabaseHelper.cs`**:
    *   `GetConnection()`: Mở kết nối đến Aiven PostgreSQL.
    *   `ExecuteQueryAsync(string sql, parameters)`: Chạy lệnh SELECT trả về DataTable/List.
    *   `ExecuteNonQueryAsync(string sql, parameters)`: Chạy INSERT, UPDATE, DELETE.
*   **`CloudStorageService.cs`**:
    *   `UploadCoverAsync(string localFilePath)`: Đọc file ảnh, gửi lên bucket `library-images` qua S3 API, trả về chuỗi `https://pub-xxx.r2.dev/book.jpg`.
*   **`AIService.cs`**:
    *   `GetEmbeddingAsync(string text)`: Gọi model `nomic-embed-text-v1.5` lấy mảng float[768].
    *   `SendChatRequestAsync(string prompt, list tools)`: Gửi hội thoại và danh sách hàm, nhận về câu trả lời hoặc cờ báo hiệu Function Call từ model `Llama-3.1`.

### 2.2. Tầng ViewModels (Logic & Data Binding)
Sử dụng `ObservableCollection` để chứa danh sách dữ liệu. Mọi thay đổi trên collection này tự động ánh xạ lên View. Các hành động của người dùng (Click) được gắn vào các `ICommand`.

*   **`BookManagementViewModel`**:
    *   `ObservableCollection<BookModel> BooksList`: Chứa danh sách sách để bind lên DataGrid.
    *   `UploadCoverCommand`: Gọi `CloudStorageService` lấy URL ảnh.
    *   `SaveBookCommand`: Cập nhật DB qua `DatabaseHelper`, sau đó reload `BooksList`.
*   **`MainViewModel`**:
    *   `Visibility StaffTabVisibility`: Nếu `Role == Admin`, trả về `Visible`, ngược lại `Collapsed`. Tính năng này áp dụng tương tự cho các tab phân quyền khác.

## 3. LUỒNG THỰC THI (WORKFLOWS) ĐIỂN HÌNH

### Luồng 1: Thêm Sách mới & Upload Ảnh bìa (Cloudflare R2)
1.  **View**: Admin điền Form trên `BookManagementView.xaml` và bấm "Chọn ảnh".
2.  **ViewModel**: Trigger `UploadCoverCommand`. Hiển thị `OpenFileDialog` lấy đường dẫn file.
3.  **Service**: Truyền file cho `CloudStorageService.UploadCoverAsync()`. Service đẩy lên R2, trả URL về ViewModel.
4.  **ViewModel**: Bind URL này vào đối tượng `BookModel` đang thêm mới (UI tự load ảnh preview qua HTTP).
5.  **View**: Admin bấm "Lưu".
6.  **ViewModel**: Trigger `SaveBookCommand`. Gọi `DatabaseHelper.ExecuteNonQueryAsync()` ghi thông tin (chứa URL ảnh) vào bảng `Books`.
7.  **ViewModel**: Thêm sách vào `BooksList`, UI (DataGrid) tự động cập nhật dòng mới.

### Luồng 2: Tìm kiếm ngữ nghĩa bằng AI (RAG Workflow)
1.  **View**: Độc giả gõ *"Tìm cho tôi sách về phép thuật"* vào `AIChatView.xaml` và bấm Gửi.
2.  **ViewModel**: Gửi chuỗi này vào `AIService.SendChatRequestAsync()`.
3.  **Service (LLM)**: LLM nhận diện cần dùng tool, trả về json yêu cầu gọi hàm `semantic_search("phép thuật")`.
4.  **ViewModel (Function Calling)**: Parse JSON, trích xuất từ khóa.
5.  **Service (Embedding)**: Chuyển từ khóa vào `AIService.GetEmbeddingAsync()` để nhận mảng `vector[768]`.
6.  **Database**: Gọi `DatabaseHelper` với lệnh:
    `SELECT Title, Summary FROM BookMetadata ORDER BY Embedding <=> @vector LIMIT 5;`
7.  **ViewModel**: Nhận Top 5 kết quả, đóng gói lại thành prompt gửi lần 2 cho LLM tổng hợp.
8.  **View**: Hiển thị câu trả lời tự nhiên của AI kèm danh sách 5 cuốn sách gợi ý lên màn hình.