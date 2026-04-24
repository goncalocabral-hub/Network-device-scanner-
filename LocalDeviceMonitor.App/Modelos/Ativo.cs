using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LocalDeviceMonitor.App.Modelos
{
    public class Ativo
    {
        public int Id { get; set; }
        public string NomeAtivo { get; set; } = "";
        public string? Origem { get; set; }
        public string Device { get; set; } = "";

        public List<Atributo> Atributos { get; set; } = new();
    }
}
