using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;


namespace LocalDeviceMonitor.App
{

    public class DatabaseSettings
    {
        public string Provider { get; set; } = "SQLite";
        public string ConnectionString { get; set; } = "";
    }

    //internal class AppConfig
    public static class AppConfig
    {
        public static DatabaseSettings LoadDatabaseSettings()
        {
            return new DatabaseSettings
            {
                Provider = ConfigurationManager.AppSettings["DatabaseProvider"] ?? "SQLite",
                ConnectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"]?.ConnectionString ?? ""
            };
        }
    }

}
