using System.Linq;
using System.Windows;
using LocalDeviceMonitor.App.Modelos;

namespace LocalDeviceMonitor.App
{
    public partial class EditDeviceWindow : Window
    {
        private readonly DeviceInfo _device;
        private readonly AssetRepository _repo;

        public EditDeviceWindow(DeviceInfo device, AssetRepository repo)
        {
            InitializeComponent();
            _device = device;
            _repo = repo;

            // 1. Carregar a lista de atributos da Base de Dados
            var lista = _repo.GetAtributos();
            lista.Insert(0, new Atributo { Id = 0, Nome = "Nenhum" });
            AtributoCombo.ItemsSource = lista;
            AtributoCombo.SelectedItem = lista.FirstOrDefault(a => a.Nome == _device.AtributoAtual) ?? lista.First();

            // 2. Preencher os campos com a informação que já existe
            NameTextBox.Text = _device.Name;
            OriginTextBox.Text = _device.Origin;
            DeviceTypeTextBox.Text = _device.DeviceType;
            ManufacturerTextBox.Text = _device.Manufacturer;
            IpTextBox.Text = _device.IpAddress;
            MacTextBox.Text = _device.MacAddress;
            PortsTextBox.Text = _device.OpenPorts;
            NotesTextBox.Text = _device.CustomNotes;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Recolher a informação TODA que escreveste nas caixas
                _device.Name = NameTextBox.Text;
                _device.Origin = OriginTextBox.Text;
                _device.DeviceType = DeviceTypeTextBox.Text;
                _device.Manufacturer = ManufacturerTextBox.Text;
                _device.IpAddress = IpTextBox.Text;
                _device.MacAddress = MacTextBox.Text;
                _device.OpenPorts = PortsTextBox.Text;
                _device.CustomNotes = NotesTextBox.Text;

                if (AtributoCombo.SelectedItem is Atributo attr && attr.Id != 0)
                    _device.AtributoAtual = attr.Nome;
                else
                    _device.AtributoAtual = "Nenhum";

                // Tenta gravar na Base de Dados
                _repo.SaveOrUpdateDevice(_device);

                // Fecha a janela e indica que correu tudo bem
                this.DialogResult = true;
                this.Close();
            }
            catch (System.Exception ex)
            {
                // SE ELE NÃO GUARDOU, VAI AVISAR AQUI:
                MessageBox.Show($"Não foi possível gravar!\nIsto acontece se não apagaste a base de dados antiga.\n\nVai à pasta bin/Debug e apaga o ficheiro 'assets.db' ou 'devices_network.db' e tenta outra vez.\n\nErro técnico: {ex.Message}",
                                "Erro na Base de Dados",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
