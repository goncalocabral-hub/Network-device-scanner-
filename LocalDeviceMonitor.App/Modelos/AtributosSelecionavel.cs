using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LocalDeviceMonitor.App.Modelos
{
    public class AtributoSelecionavel
    {
        public int Id { get; set; }
        public string Nome { get; set; } = "";
        public bool Selecionado { get; set; }
    }
}
