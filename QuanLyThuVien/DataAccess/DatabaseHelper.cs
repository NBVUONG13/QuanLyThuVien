using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using Npgsql;

namespace QuanLyThuVien.DataAccess
{
    public static class DatabaseHelper
    {
        private static string ConnectionString => ConfigurationManager.ConnectionStrings["AivenDb"].ConnectionString;

        public static NpgsqlConnection GetConnection()
        {
            var conn = new NpgsqlConnection(ConnectionString);
            conn.Open();
            return conn;
        }
    }
}
