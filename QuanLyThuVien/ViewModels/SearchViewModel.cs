namespace QuanLyThuVien.ViewModels
{
    // Reuses BookManagementViewModel and the same book catalog UI; no copy/paste business logic.
    public sealed class SearchViewModel : BookManagementViewModel
    {
        public SearchViewModel(string token, string role) : base(token, role) { }
    }
}
