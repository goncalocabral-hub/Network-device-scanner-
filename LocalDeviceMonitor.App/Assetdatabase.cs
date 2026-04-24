using System.Data.Common;
using Microsoft.Data.Sqlite;
using System.IO;

using LocalDeviceMonitor.App;

namespace LocalDeviceMonitor.App
{

    public class AssetDatabase
    {
        private readonly DatabaseSettings _settings;

        public AssetDatabase()
        {
            _settings = AppConfig.LoadDatabaseSettings();
        }
        public void EnsureDatabaseCreated()
        {
            var dbPath = GetSqliteDatabasePath(_settings.ConnectionString);

            if (!File.Exists(dbPath))
            {
                // Cria a estrutura da base de dados
                RunMigrations();
                // Insere os atributos gerais
                RunDefaults();
            }
        }
        private string GetSqliteDatabasePath(string connectionString)
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);

            var dataSource = builder.DataSource;

            if (string.IsNullOrWhiteSpace(dataSource))
                throw new Exception("Data Source não definido na connection string.");

            if (!Path.IsPathRooted(dataSource))
            {
                dataSource = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dataSource);
            }

            return dataSource;
        }

        private DbConnection CreateConnection()
        {
            if (_settings.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
            {
                return new SqliteConnection(_settings.ConnectionString);
            }

            throw new NotSupportedException($"Provider não suportado: {_settings.Provider}");
        }

        public void RunMigrations()
        {
            using var conn = CreateConnection();
            conn.Open();

            var sql = """
        CREATE TABLE IF NOT EXISTS Ativos (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ativo TEXT NOT NULL,
            origem TEXT NULL,
            device TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS Atributos (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            atributo TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS AtivosAtributos (
            id_ativo INTEGER NOT NULL,
            id_atributo INTEGER NOT NULL,
            PRIMARY KEY (id_ativo, id_atributo),
            FOREIGN KEY (id_ativo) REFERENCES Ativos(id) ON DELETE CASCADE,
            FOREIGN KEY (id_atributo) REFERENCES Atributos(id) ON DELETE CASCADE
        );
        """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void RunDefaults()
        {
            using var conn = CreateConnection();
            conn.Open();

            var sql = """
        INSERT INTO Atributos (atributo) VALUES ('Interno');
        INSERT INTO Atributos (atributo) VALUES ('Externo');
        """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }


        public int UpsertAtributo(string atributo)
        {
            using var conn = CreateConnection();
            conn.Open();

            var sql = """
            INSERT INTO Atributos (atributo)
            VALUES (@atributo);
            SELECT id FROM Atributos WHERE atributo = @atributo;
            """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            AddParam(cmd, "@atributo", atributo);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        private static void AddParam(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

    }
}
