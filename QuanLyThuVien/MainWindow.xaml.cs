using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Npgsql;
using System.Configuration;

namespace QuanLyThuVien
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void btnTestAiven_Click(object sender, RoutedEventArgs e)
        {
            string connectString = ConfigurationManager.ConnectionStrings["AivenDb"].ConnectionString;

            try
            {
                using (var conn = new NpgsqlConnection(connectString))
                {
                    conn.Open();

                    string sqlEnableVector = "CREATE EXTENSION IF NOT EXISTS vector;";
                    using (var cmd = new NpgsqlCommand(sqlEnableVector, conn))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    string sqlVersion = "SELECT version();";
                    using (var cmd = new NpgsqlCommand(sqlVersion, conn))
                    {
                        string versionInfo = cmd.ExecuteScalar().ToString();

                        MessageBox.Show($"Ket noi thanh cong phien ban: {versionInfo}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi cơ sở dữ liệu: " + ex.Message);
            }
        }
    }
}
