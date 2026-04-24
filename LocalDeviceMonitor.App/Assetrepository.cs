using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using System.Security.RightsManagement;

using LocalDeviceMonitor.App.Modelos;

namespace LocalDeviceMonitor.App
{
    internal class AssetRepository
    {
        private readonly DatabaseSettings _settings;

        public AssetRepository()
        {
            _settings = AppConfig.LoadDatabaseSettings();
        }

        private DbConnection CreateConnection()
        {
            return new SqliteConnection(_settings.ConnectionString);
        }

        public int UpsertAtivo(string ativo, string? origem, string device)
        {
            using var conn = CreateConnection();
            conn.Open();

            var sql = """
            INSERT INTO Ativos (ativo, origem, device)
            VALUES (@ativo, @origem, @device)
            ON CONFLICT(device) DO UPDATE SET
                ativo = excluded.ativo,
                origem = excluded.origem;

            SELECT id FROM Ativos WHERE device = @device;
            """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            AddParam(cmd, "@ativo", ativo);
            AddParam(cmd, "@origem", origem);
            AddParam(cmd, "@device", device);

            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public void UpdateTituloAtivo(int ativoId, string novoTitulo)
        {
            using var conn = CreateConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            UPDATE Ativos
            SET ativo = @ativo
            WHERE id = @id;
            """;

            AddParam(cmd, "@ativo", novoTitulo);
            AddParam(cmd, "@id", ativoId);

            cmd.ExecuteNonQuery();
        }

        public List<Atributo> GetAtributos()
        {
            var result = new List<Atributo>();

            using var conn = CreateConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, atributo FROM Atributos ORDER BY atributo;";

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                result.Add(new Atributo
                {
                    Id = reader.GetInt32(0),
                    Nome = reader.GetString(1)
                });
            }

            return result;
        }

        public void SetAtributoAtivo(int ativoId, int atributoId, bool selected)
        {
            using var conn = CreateConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();

            if (selected)
            {
                cmd.CommandText = """
                INSERT OR IGNORE INTO AtivosAtributos (id_ativo, id_atributo)
                VALUES (@id_ativo, @id_atributo);
                """;
            }
            else
            {
                cmd.CommandText = """
                DELETE FROM AtivosAtributos
                WHERE id_ativo = @id_ativo
                  AND id_atributo = @id_atributo;
                """;
            }

            AddParam(cmd, "@id_ativo", ativoId);
            AddParam(cmd, "@id_atributo", atributoId);

            cmd.ExecuteNonQuery();
        }

        public List<int> GetAtributosDoAtivo(int ativoId)
        {
            var result = new List<int>();

            using var conn = CreateConnection();
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            SELECT id_atributo
            FROM AtivosAtributos
            WHERE id_ativo = @id_ativo;
            """;

            AddParam(cmd, "@id_ativo", ativoId);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                result.Add(reader.GetInt32(0));
            }

            return result;
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
