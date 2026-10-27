using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class HRManagementView : UserControl
    {
        public HRManagementView() { InitializeComponent(); }
        // PasswordBox intentionally not bound; business logic and HTTP stay in HRManagementViewModel.
        private void CreateStaff_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as HRManagementViewModel;
            if (vm == null) return;
            var password = StaffPassword.Password;
            if (vm.CreateCommand.CanExecute(password))
            {
                StaffPassword.Clear();
                vm.CreateCommand.Execute(password);
            }
        }
    }
}
