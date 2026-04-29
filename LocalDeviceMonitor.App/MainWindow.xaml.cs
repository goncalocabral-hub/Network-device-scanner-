using NModbus;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using Windows.Devices.Bluetooth.Advertisement;
using LocalDeviceMonitor.App.Modelos;



namespace LocalDeviceMonitor.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceInfo> _allDevices = new();
    private readonly ObservableCollection<DeviceInfo> _shadowDevices = new();
    private readonly HashSet<string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);

    private int _baselineScanCount = 0;
    private const int BaselineScansRequired = 3;
    private bool _baselineEstablished = false;
    private int _newDevicesThisScan = 0;


    private bool _isDarkMode = false;
    private readonly DispatcherTimer _scanTimer = new();
    private bool _isContinuousScanEnabled = false;
    private bool _isScanRunning = false;

    private readonly AssetRepository _repo = new();

    private static readonly int[] CommonPorts =
    {
        80, 443, 22, 554, 8000, 8080, 47808, 502, 3702
    };

    private static readonly Dictionary<ushort, string> BluetoothCompanies = new()
    {
        { 6, "Microsoft / Kontakt.io" },
        { 10, "CSR (Qualcomm)" },
        { 13, "Texas Instruments" },
        { 15, "Broadcom" },
        { 76, "Apple" },
        { 89, "Nordic Semiconductor" },
        { 117, "Samsung" },
        { 224, "Google" },
        { 352, "Apple" },
        { 0, "Ericsson AB" },
        { 101, " HP, Inc." },
        { 2409, "Woan Technology (Shenzhen) Co., Ltd." },
        { 147, "Universal Electronics, Inc." },
        { 1704, "GD Midea Air-Conditioning Equipment Co., Ltd."},
        { 34818, "Ningbo Joyson Electronic Corp." },
        { 20 , "Mitsubishi Electric Corporation" },
        { 41488, "Xiaomi Inc. " }
    };

    private static readonly Dictionary<string, string> OuiVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "FCFBFB", "Apple" },
        { "F0D1A9", "Apple" },
        { "3C2EFF", "Apple" },
        { "8C8590", "Apple" },
        { "A4C361", "Apple" },
        { "B8E856", "Apple" },
        { "BC679C", "Apple" },
        { "DCF756", "Apple" },

        { "E8E5D6", "Samsung" },
        { "3089D3", "Samsung" },
        { "5CF370", "Samsung" },
        { "FCA13E", "Samsung" },
        { "7C61D9", "Samsung" },

        { "488D36", "Hikvision" },
        { "D4E853", "Hikvision" },
        { "C4EA1D", "Hikvision" },

        { "F4F26D", "TP-Link" },
        { "50C7BF", "TP-Link" },
        { "B0487A", "TP-Link" },
        { "60E327", "TP-Link" },

        { "D8EC5E", "Intel" },
        { "3C6AA7", "Intel" },
        { "5C80B6", "Intel" },

        { "FC3497", "Xiaomi" },
        { "64CC2E", "Xiaomi" },
        { "28E347", "Xiaomi" },

        { "2CF05D", "Cisco" },
        { "F8A2D6", "Cisco" },
        { "001B54", "Cisco" },

        { "4CC9E4", "Ubiquiti" },
        { "24A43C", "Ubiquiti" },
        { "80DA13", "Ubiquiti" },

        { "CC2D21", "MikroTik" },
        { "08B4B1", "MikroTik" },

        { "A44CC8", "Dell" },
        { "F04DA2", "HP" },
        { "D017C2", "Lenovo" }
    };

    public MainWindow()
    {
        InitializeComponent();

        DevicesGrid.ItemsSource = _allDevices;
        ShadowGrid.ItemsSource = _shadowDevices;

        DevicesGrid.MouseDoubleClick += DataGrid_MouseDoubleClick;
        ShadowGrid.MouseDoubleClick += DataGrid_MouseDoubleClick;

        OriginComboBox.SelectedIndex = 0;
        StatusTextBlock.Text = "Pronto.";
        UpdateSummaryCards(_allDevices);
        UpdateShadowCount();
        ApplyLightTheme();


        var db = new AssetDatabase();
        db.EnsureDatabaseCreated();

        LoadFiltroAtributos();

        _scanTimer.Interval = TimeSpan.FromSeconds(12);
        _scanTimer.Tick += ScanTimer_Tick;
    }

    private void LoadFiltroAtributos()
    {
        var atributos = _repo.GetAtributos();

        atributos.Insert(0, new Atributo
        {
            Id = 0,
            Nome = "Todos"
        });

        AtributoComboBox.ItemsSource = atributos;
        AtributoComboBox.SelectedIndex = 0;
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 1. Tenta descobrir qual foi o elemento visual exato onde clicaste
        DependencyObject dep = (DependencyObject)e.OriginalSource;

        // 2. Sobe na "árvore" de elementos do WPF até encontrar a Célula da grelha
        while (dep != null && !(dep is DataGridCell))
        {
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }

        // 3. Se encontrou a célula, extrai o texto e copia para o Clipboard
        if (dep is DataGridCell cell && cell.Content is TextBlock textBlock)
        {
            string valorCopiado = textBlock.Text;

            if (!string.IsNullOrWhiteSpace(valorCopiado))
            {
                Clipboard.SetText(valorCopiado);

                // Reutiliza o Toast para avisar que copiou! (Dura 4 segundos)
                ShowToast("Copiado!", valorCopiado, 4);
            }
        }
    }


    private async void PingDevice_Click(object sender, RoutedEventArgs e)
    {
        // Verifica se há um dispositivo selecionado e se ele tem IP
        if (DevicesGrid.SelectedItem is DeviceInfo device && !string.IsNullOrWhiteSpace(device.IpAddress))
        {
            ShowToast("A testar ligação...", $"A enviar ping para {device.IpAddress}", 2);

            try
            {
                using var ping = new Ping();
                // Usa SendPingAsync para não congelar a interface enquanto espera!
                var reply = await ping.SendPingAsync(device.IpAddress, 2000); // Timeout de 2 segundos

                if (reply.Status == IPStatus.Success)
                {
                    ShowToast("✅ Ping Sucesso!", $"{device.IpAddress} respondeu em {reply.RoundtripTime}ms", 4);
                }
                else
                {
                    ShowToast("❌ Ping Falhou", $"O dispositivo não respondeu ({reply.Status})", 4);
                }
            }
            catch (Exception ex)
            {
                ShowToast("⚠️ Erro no Ping", ex.Message, 4);
            }
        }
        else
        {
            ShowToast("Aviso", "O dispositivo selecionado não tem um endereço IP válido.", 3);
        }
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        // Verifica se há um dispositivo selecionado e se ele tem IP
        if (DevicesGrid.SelectedItem is DeviceInfo device && !string.IsNullOrWhiteSpace(device.IpAddress))
        {
            try
            {
                // Constrói o URL (assume HTTP, o próprio browser faz o upgrade para HTTPS se necessário)
                string url = $"http://{device.IpAddress}";

                // Lança o processo predefinido do Windows para abrir links (o teu browser padrão)
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                ShowToast("A abrir Browser...", $"A redirecionar para {url}", 2);
            }
            catch (Exception ex)
            {
                ShowToast("⚠️ Erro", $"Não foi possível abrir o browser: {ex.Message}", 4);
            }
        }
        else
        {
            ShowToast("Aviso", "O dispositivo selecionado não tem um endereço IP válido.", 3);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!_isContinuousScanEnabled)
        {
            _isContinuousScanEnabled = true;
            _scanTimer.Start();
            ScanButton.Content = "Parar Scan Contínuo";
            StatusTextBlock.Text = "Scan contínuo iniciado.";

            await RunFullScanAsync();
        }
        else
        {
            _isContinuousScanEnabled = false;
            _scanTimer.Stop();
            ScanButton.Content = "Iniciar Scan Contínuo";
            StatusTextBlock.Text = "Scan contínuo parado.";
        }
    }

    private async void ScanTimer_Tick(object? sender, EventArgs e)
    {
        await RunFullScanAsync();
    }

    private async Task RunFullScanAsync()
    {
        if (_isScanRunning)
            return;

        _isScanRunning = true;
        _newDevicesThisScan = 0;

        try
        {
            if (!_baselineEstablished)
                StatusTextBlock.Text = $"A criar baseline... scan {_baselineScanCount + 1}/{BaselineScansRequired}";
            else
                StatusTextBlock.Text = "A executar scan contínuo...";

            var bleTask = ScanBleAsync();
            var wifiTask = ScanWifiAsync();
            var lanTask = ScanLanAsync();
            var bacnetTask = ScanBacnetAsync();
            var modbusTask = ScanModbusAsync();
            var onvifTask = ScanOnvifAsync();

            await Task.WhenAll(bleTask, wifiTask, lanTask, bacnetTask, modbusTask, onvifTask);

            var currentScanDevices = new List<DeviceInfo>();
            currentScanDevices.AddRange(bleTask.Result);
            currentScanDevices.AddRange(wifiTask.Result);
            currentScanDevices.AddRange(lanTask.Result);
            currentScanDevices.AddRange(bacnetTask.Result);
            currentScanDevices.AddRange(modbusTask.Result);
            currentScanDevices.AddRange(onvifTask.Result);

            ProcessDiscoveredDevices(currentScanDevices);

            MarkOfflineDevices();
            ApplyFilters();
            UpdateShadowCount();

            if (!_baselineEstablished)
            {
                _baselineScanCount++;

                if (_baselineScanCount >= BaselineScansRequired)
                {
                    _baselineEstablished = true;
                    StatusTextBlock.Text =
                        $"Baseline concluída. {_allDevices.Count} dispositivo(s) conhecidos.";
                }
                else
                {
                    StatusTextBlock.Text =
                        $"Baseline em curso... scan {_baselineScanCount}/{BaselineScansRequired}. {_allDevices.Count} dispositivo(s) aprendidos.";
                }
            }
            else
            {
                StatusTextBlock.Text =
                    $"Scan atualizado. {_allDevices.Count} registo(s), {_allDevices.Count(d => d.Status == "Online")} online.";

                if (_newDevicesThisScan > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowToast("Shadow IT Detection", $"{_newDevicesThisScan} novos dispositivos detetados", 6);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Erro no scan.";
            MessageBox.Show(
                $"Erro ao executar scan: {ex.Message}",
                "Erro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isScanRunning = false;
        }
    }

    private async void ShowToast(string title, string message, int durationSeconds = 3)
    {
        // Cria uma janela invisível e flutuante
        var toastWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowActivated = false, // Não rouba o foco
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false // Impede que bloqueie cliques noutras coisas
        };

        // Estiliza o "card" do Toast usando as tuas cores do tema
        var border = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("CardBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushTheme"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(15),
            Padding = new Thickness(20, 15, 20, 15),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 15,
                ShadowDepth = 4,
                Opacity = 0.2
            }
        };

        var stack = new StackPanel();

        // Título
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryTextBrush"),
            Margin = new Thickness(0, 0, 0, 5)
        });

        // Mensagem
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush")
        });

        border.Child = stack;
        toastWindow.Content = border;

        // Posiciona o Toast no canto inferior direito da tua MainWindow
        toastWindow.Loaded += (s, e) =>
        {
            toastWindow.Left = this.Left + this.ActualWidth - toastWindow.ActualWidth - 20;
            toastWindow.Top = this.Top + this.ActualHeight - toastWindow.ActualHeight - 20;
        };

        // Mostra o Toast
        toastWindow.Show();

        // Espera os segundos definidos
        await Task.Delay(durationSeconds * 1000);

        // Animação de fade-out rápida
        while (toastWindow.Opacity > 0)
        {
            toastWindow.Opacity -= 0.1;
            await Task.Delay(20);
        }

        toastWindow.Close();
    }


    private async Task<List<DeviceInfo>> DiscoverDevicesAsync()
    {
        var bleTask = ScanBleAsync();
        var wifiTask = ScanWifiAsync();
        var lanTask = ScanLanAsync();
        var bacnetTask = ScanBacnetAsync();
        var modbusTask = ScanModbusAsync();
        var onvifTask = ScanOnvifAsync();

        await Task.WhenAll(bleTask, wifiTask, lanTask, bacnetTask, modbusTask, onvifTask);

        var discoveredDevices = new List<DeviceInfo>();
        discoveredDevices.AddRange(bleTask.Result);
        discoveredDevices.AddRange(wifiTask.Result);
        discoveredDevices.AddRange(lanTask.Result);
        discoveredDevices.AddRange(bacnetTask.Result);
        discoveredDevices.AddRange(modbusTask.Result);
        discoveredDevices.AddRange(onvifTask.Result);

        return discoveredDevices;
    }

    private void ProcessDiscoveredDevices(IEnumerable<DeviceInfo> discoveredDevices)
    {
        foreach (var device in discoveredDevices)
        {
            EnrichDevice(device);
            RegisterShadowDevice(device);
            UpsertDevice(device);
        }
    }

    private void EnrichDevice(DeviceInfo device)
    {
        device.LastSeen = DateTime.Now;
        device.Status = "Online";

        EnrichManufacturerFromMac(device);
        ClassifyDevice(device);
    }

    private void RegisterShadowDevice(DeviceInfo device)
    {
        var matchedKnown = FindMatchingKnownDevice(device);

        if (matchedKnown != null)
            return;

        _knownDevices.Add(BuildSoftDeviceKey(device));

        // durante a baseline só aprende
        if (!_baselineEstablished)
            return;

        bool alreadyInShadow = _shadowDevices.Any(d => IsSameLogicalDevice(d, device));
        if (!alreadyInShadow)
        {
            _shadowDevices.Add(device);
            _newDevicesThisScan++;
        }
    }

    private DeviceInfo? FindMatchingKnownDevice(DeviceInfo scannedDevice)
    {
        foreach (var known in _allDevices)
        {
            if (IsSameLogicalDevice(known, scannedDevice))
                return known;
        }

        string softKey = BuildSoftDeviceKey(scannedDevice);

        if (_knownDevices.Contains(softKey))
            return scannedDevice;

        return null;
    }

    private bool IsSameLogicalDevice(DeviceInfo a, DeviceInfo b)
    {
        // 1. MAC igual
        if (!string.IsNullOrWhiteSpace(a.MacAddress) &&
            !string.IsNullOrWhiteSpace(b.MacAddress) &&
            string.Equals(NormalizeValue(a.MacAddress), NormalizeValue(b.MacAddress), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. ID igual
        if (!string.IsNullOrWhiteSpace(a.Id) &&
            !string.IsNullOrWhiteSpace(b.Id) &&
            string.Equals(NormalizeValue(a.Id), NormalizeValue(b.Id), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Nome + fabricante + origem
        if (!string.IsNullOrWhiteSpace(a.Name) &&
            !string.IsNullOrWhiteSpace(b.Name) &&
            !string.IsNullOrWhiteSpace(a.Manufacturer) &&
            !string.IsNullOrWhiteSpace(b.Manufacturer) &&
            string.Equals(NormalizeValue(a.Name), NormalizeValue(b.Name), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeValue(a.Manufacturer), NormalizeValue(b.Manufacturer), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeValue(a.Origin), NormalizeValue(b.Origin), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. IP + nome + origem
        if (!string.IsNullOrWhiteSpace(a.IpAddress) &&
            !string.IsNullOrWhiteSpace(b.IpAddress) &&
            string.Equals(NormalizeValue(a.IpAddress), NormalizeValue(b.IpAddress), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeValue(a.Origin), NormalizeValue(b.Origin), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(a.Name) &&
            !string.IsNullOrWhiteSpace(b.Name) &&
            string.Equals(NormalizeValue(a.Name), NormalizeValue(b.Name), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private string BuildSoftDeviceKey(DeviceInfo device)
    {
        string origin = NormalizeValue(device.Origin);
        string name = NormalizeValue(device.Name);
        string manufacturer = NormalizeValue(device.Manufacturer);

        if (!string.IsNullOrWhiteSpace(device.MacAddress))
            return $"mac:{NormalizeValue(device.MacAddress)}";

        if (!string.IsNullOrWhiteSpace(device.Id))
            return $"id:{NormalizeValue(device.Id)}";

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(manufacturer))
            return $"nm:{origin}|{name}|{manufacturer}";

        if (!string.IsNullOrWhiteSpace(device.IpAddress) && !string.IsNullOrWhiteSpace(name))
            return $"ipn:{origin}|{NormalizeValue(device.IpAddress)}|{name}";

        return $"fallback:{origin}|{name}";
    }

    private static string NormalizeValue(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant();
    }

    private void UpsertDevice(DeviceInfo scannedDevice)
    {
        var existing = _allDevices.FirstOrDefault(d =>
            string.Equals(d.Origin, scannedDevice.Origin, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Id, scannedDevice.Id, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            scannedDevice.Status = "Online";
            scannedDevice.LastSeen = DateTime.Now;
            _allDevices.Add(scannedDevice);
            return;
        }

        existing.Icon = scannedDevice.Icon;
        existing.DeviceType = scannedDevice.DeviceType;
        existing.Name = scannedDevice.Name;
        existing.Manufacturer = scannedDevice.Manufacturer;
        existing.MacAddress = scannedDevice.MacAddress;
        existing.IpAddress = scannedDevice.IpAddress;

        existing.Status = "Online";
        existing.LastSeen = DateTime.Now;

        existing.Protocol = scannedDevice.Protocol;
        existing.Rssi = scannedDevice.Rssi;
        existing.EstimatedDistanceMeters = scannedDevice.EstimatedDistanceMeters;
        existing.OpenPorts = scannedDevice.OpenPorts;
        existing.DetectedServices = scannedDevice.DetectedServices;

        existing.BacnetDeviceId = scannedDevice.BacnetDeviceId;
        existing.BacnetVendorName = scannedDevice.BacnetVendorName;
        existing.BacnetModelName = scannedDevice.BacnetModelName;
        existing.BacnetFirmware = scannedDevice.BacnetFirmware;
        existing.BacnetObjectSummary = scannedDevice.BacnetObjectSummary;

        existing.ModbusUnitId = scannedDevice.ModbusUnitId;
        existing.ModbusRegisterSummary = scannedDevice.ModbusRegisterSummary;

        existing.OnvifXAddr = scannedDevice.OnvifXAddr;
        existing.OnvifScopes = scannedDevice.OnvifScopes;
        existing.OnvifEndpointAddress = scannedDevice.OnvifEndpointAddress;
    }

    private void MarkOfflineDevices()
    {
        var now = DateTime.Now;
        var offlineThreshold = TimeSpan.FromSeconds(45);

        foreach (var device in _allDevices)
        {
            if (device.LastSeen == DateTime.MinValue)
            {
                device.Status = "Offline";
                continue;
            }

            device.Status = (now - device.LastSeen) <= offlineThreshold
                ? "Online"
                : "Offline";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        OriginComboBox.SelectedIndex = 0;
        NameFilterTextBox.Text = string.Empty;
        ManufacturerFilterTextBox.Text = string.Empty;

        ApplyFilters();
        StatusTextBlock.Text = $"Filtros limpos. {_allDevices.Count} registo(s) disponíveis.";
    }
    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded)
            return;

        ApplyFilters();
    }

    private async Task<List<DeviceInfo>> ScanBleAsync()
    {
        var result = new Dictionary<ulong, DeviceInfo>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string mac = FormatBluetoothAddress(args.BluetoothAddress);
            string name = args.Advertisement.LocalName;
            int rssi = args.RawSignalStrengthInDBm;

            string manufacturer = "Desconhecido";
            if (args.Advertisement.ManufacturerData.Count > 0)
            {
                var companyId = args.Advertisement.ManufacturerData[0].CompanyId;
                manufacturer = GetBluetoothCompanyName(companyId);

                if (companyId == 6)
                    manufacturer = "Kontakt.io / Microsoft";
            }

            var device = new DeviceInfo
            {
                Id = mac,
                Origin = "Bluetooth",
                Name = string.IsNullOrWhiteSpace(name) ? "BLE Device" : name,
                Manufacturer = manufacturer,
                MacAddress = mac,
                Rssi = rssi,
                EstimatedDistanceMeters = EstimateDistance(rssi, -59, 2.2),
                Status = "Online",
                LastSeen = DateTime.Now
            };

            result[args.BluetoothAddress] = device;
        }

        watcher.Received += OnReceived;
        watcher.Start();
        await Task.Delay(5000);
        watcher.Stop();
        watcher.Received -= OnReceived;

        return result.Values
            .OrderByDescending(d => d.Rssi ?? int.MinValue)
            .ToList();
    }

    private async Task<List<DeviceInfo>> ScanWifiAsync()
    {
        var list = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var output = await ExecuteCommandAsync("netsh", "wlan show networks mode=bssid");

        string? currentSsid = null;
        string? currentBssid = null;
        int? currentSignalPercent = null;

        foreach (var raw in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var line = raw.Trim();

            if (line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                currentSsid = idx >= 0 ? line[(idx + 1)..].Trim() : "";
                currentBssid = null;
                currentSignalPercent = null;
            }
            else if (line.StartsWith("BSSID ", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                currentBssid = idx >= 0 ? line[(idx + 1)..].Trim() : "";
            }
            else if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                var txt = idx >= 0 ? line[(idx + 1)..].Trim().Replace("%", "") : "";
                if (int.TryParse(txt, out var p))
                    currentSignalPercent = p;
            }
            else if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase))
            {
                int rssi = currentSignalPercent.HasValue ? ConvertPercentToRssi(currentSignalPercent.Value) : 0;
                string name = string.IsNullOrWhiteSpace(currentSsid) ? "WiFi Network" : currentSsid!;
                string bssid = currentBssid ?? "";

                var vendor = GetVendorFromMac(bssid);

                var device = new DeviceInfo
                {
                    Id = string.IsNullOrWhiteSpace(bssid) ? Guid.NewGuid().ToString("N") : $"WIFI-AP-{bssid}",
                    Origin = "WIFI",
                    Name = name,
                    Manufacturer = !string.IsNullOrWhiteSpace(vendor) ? vendor : "WiFi Access Point",
                    MacAddress = bssid,
                    Rssi = rssi,
                    EstimatedDistanceMeters = currentSignalPercent.HasValue ? EstimateDistance(rssi, -50, 2.6) : null,
                    Status = "Online",
                    LastSeen = DateTime.Now
                };

                if (seen.Add(device.Id))
                    list.Add(device);
            }
        }

        var arpOutput = await ExecuteCommandAsync("arp", "-a");
        var arpRegex = new Regex(@"(?<ip>\d+\.\d+\.\d+\.\d+)\s+(?<mac>([0-9a-f]{2}-){5}[0-9a-f]{2})", RegexOptions.IgnoreCase);

        var arpMap = arpRegex.Matches(arpOutput)
            .Cast<Match>()
            .GroupBy(m => m.Groups["ip"].Value)
            .ToDictionary(
                g => g.Key,
                g => g.First().Groups["mac"].Value,
                StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            var ipProps = ni.GetIPProperties();
            var uni = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null);

            if (uni == null || uni.IPv4Mask == null)
                continue;

            var localIp = uni.Address.ToString();
            var hosts = EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254).ToList();

            var semaphore = new SemaphoreSlim(35);

            var pingTasks = hosts.Select(async ip =>
            {
                await semaphore.WaitAsync();

                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, 150);
                    if (reply.Status != IPStatus.Success)
                        return null;

                    string ipText = ip.ToString();
                    if (string.Equals(ipText, localIp, StringComparison.OrdinalIgnoreCase))
                        return null;

                    string hostName;
                    try
                    {
                        hostName = (await Dns.GetHostEntryAsync(ip)).HostName;
                    }
                    catch
                    {
                        hostName = $"WiFi Client {ipText}";
                    }

                    arpMap.TryGetValue(ipText, out string? mac);
                    var vendor = GetVendorFromMac(mac);

                    var portInfo = await DetectCommonPortsAsync(ipText);

                    var device = new DeviceInfo
                    {
                        Id = $"WIFI-CLIENT-{ipText}",
                        Origin = "WIFI",
                        Name = hostName,
                        Manufacturer = !string.IsNullOrWhiteSpace(vendor) ? vendor : "WiFi Client",
                        IpAddress = ipText,
                        MacAddress = mac ?? "",
                        OpenPorts = portInfo.openPorts,
                        DetectedServices = string.IsNullOrWhiteSpace(portInfo.services) ? "Cliente Wi-Fi" : portInfo.services,
                        Status = "Online",
                        LastSeen = DateTime.Now
                    };

                    return device;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var pingResults = await Task.WhenAll(pingTasks);

            foreach (var item in pingResults.Where(x => x != null))
            {
                if (seen.Add(item!.Id))
                    list.Add(item!);
            }
        }

        return list.OrderBy(d => d.Name).ToList();
    }

    private async Task<List<DeviceInfo>> ScanLanAsync()
    {
        var list = new List<DeviceInfo>();
        var ipStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                continue;

            var ipProps = ni.GetIPProperties();
            var uni = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null);

            if (uni == null || uni.IPv4Mask == null)
                continue;

            var hosts = EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254).ToList();
            var semaphore = new SemaphoreSlim(35);

            var pingTasks = hosts.Select(async ip =>
            {
                await semaphore.WaitAsync();

                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, 180);
                    if (reply.Status != IPStatus.Success)
                        return null;

                    string hostName;
                    try
                    {
                        hostName = (await Dns.GetHostEntryAsync(ip)).HostName;
                    }
                    catch
                    {
                        hostName = ip.ToString();
                    }

                    var portInfo = await DetectCommonPortsAsync(ip.ToString());

                    var device = new DeviceInfo
                    {
                        Id = ip.ToString(),
                        Origin = "LAN",
                        Name = hostName,
                        Manufacturer = "LAN Host",
                        IpAddress = ip.ToString(),
                        OpenPorts = portInfo.openPorts,
                        DetectedServices = portInfo.services,
                        Status = "Online",
                        LastSeen = DateTime.Now
                    };

                    return device;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var pingResults = await Task.WhenAll(pingTasks);

            foreach (var item in pingResults.Where(x => x != null))
            {
                if (ipStrings.Add(item!.IpAddress))
                    list.Add(item!);
            }
        }

        var arpOutput = await ExecuteCommandAsync("arp", "-a");
        var arpRegex = new Regex(@"(?<ip>\d+\.\d+\.\d+\.\d+)\s+(?<mac>([0-9a-f]{2}-){5}[0-9a-f]{2})", RegexOptions.IgnoreCase);

        foreach (Match m in arpRegex.Matches(arpOutput))
        {
            var ip = m.Groups["ip"].Value;
            var mac = m.Groups["mac"].Value;

            var existing = list.FirstOrDefault(x => string.Equals(x.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.MacAddress = mac;
                existing.LastSeen = DateTime.Now;
            }
            else if (ipStrings.Add(ip))
            {
                var portInfo = await DetectCommonPortsAsync(ip);
                var vendor = GetVendorFromMac(mac);

                var device = new DeviceInfo
                {
                    Id = ip,
                    Origin = "LAN",
                    Name = ip,
                    Manufacturer = !string.IsNullOrWhiteSpace(vendor) ? vendor : "LAN Host",
                    IpAddress = ip,
                    MacAddress = mac,
                    OpenPorts = portInfo.openPorts,
                    DetectedServices = portInfo.services,
                    Status = "Online",
                    LastSeen = DateTime.Now
                };

                list.Add(device);
            }
        }

        return list;
    }

    private async Task<List<DeviceInfo>> ScanBacnetAsync()
    {
        var list = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lockObj = new object();

        await Task.Run(async () =>
        {
            BacnetClient? client = null;

            try
            {
                client = new BacnetClient(new BacnetIpUdpProtocolTransport(0xBAC0, false));

                client.OnIam += (BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu, BacnetSegmentations segmentation, ushort vendorId) =>
                {
                    try
                    {
                        string ip = ExtractBacnetIp(adr);
                        string uniqueKey = $"IAM-{deviceId}-{ip}";

                        lock (lockObj)
                        {
                            if (!seen.Add(uniqueKey))
                                return;
                        }

                        var deviceObjectId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceId);

                        string vendorName = "";
                        string modelName = "";
                        string firmware = "";
                        string objectSummary = "";

                        try
                        {
                            vendorName = ReadBacnetStringProperty(sender, adr, deviceObjectId, BacnetPropertyIds.PROP_VENDOR_NAME);
                            modelName = ReadBacnetStringProperty(sender, adr, deviceObjectId, BacnetPropertyIds.PROP_MODEL_NAME);
                            firmware = ReadBacnetStringProperty(sender, adr, deviceObjectId, BacnetPropertyIds.PROP_FIRMWARE_REVISION);
                            objectSummary = ReadBacnetObjectSummary(sender, adr, deviceObjectId, 6);
                        }
                        catch
                        {
                        }

                        var device = new DeviceInfo
                        {
                            Id = $"BACNET-{deviceId}",
                            Origin = "BACNET",
                            Name = !string.IsNullOrWhiteSpace(modelName) ? modelName : $"BACnet Device {deviceId}",
                            Manufacturer = !string.IsNullOrWhiteSpace(vendorName) ? vendorName : (vendorId > 0 ? $"Vendor {vendorId}" : "BACnet"),
                            IpAddress = ip,
                            OpenPorts = "47808",
                            DetectedServices = "BACnet | I-Am recebido",
                            BacnetDeviceId = deviceId,
                            BacnetVendorName = vendorName,
                            BacnetModelName = modelName,
                            BacnetFirmware = firmware,
                            BacnetObjectSummary = objectSummary,
                            Status = "Online",
                            Protocol = "BACnet/IP",
                            LastSeen = DateTime.Now
                        };

                        lock (lockObj)
                        {
                            list.Add(device);
                        }
                    }
                    catch
                    {
                    }
                };

                client.Start();
                client.WhoIs();

                await Task.Delay(6000);
            }
            catch
            {
            }
            finally
            {
                if (client != null)
                {
                    try { client.Dispose(); } catch { }
                }
            }
        });

        var candidateIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            var ipProps = ni.GetIPProperties();
            var uni = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null);

            if (uni == null || uni.IPv4Mask == null)
                continue;

            foreach (var ip in EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254))
                candidateIps.Add(ip.ToString());
        }

        var semaphore = new SemaphoreSlim(45);

        var portChecks = candidateIps.Select(async ip =>
        {
            await semaphore.WaitAsync();

            try
            {
                bool open = await IsUdpLikelyBacnetAsync(ip, 47808);
                if (!open)
                    return null;

                string uniqueKey = $"PORT-{ip}";

                lock (lockObj)
                {
                    if (!seen.Add(uniqueKey))
                        return null;
                }

                return new DeviceInfo
                {
                    Id = $"BACNET-PORT-{ip}",
                    Origin = "BACNET",
                    Name = $"Possível dispositivo BACnet ({ip})",
                    Manufacturer = "BACnet/IP",
                    IpAddress = ip,
                    OpenPorts = "47808",
                    DetectedServices = "BACnet | Porta 47808 detetada",
                    Status = "Online",
                    Protocol = "BACnet/IP",
                    LastSeen = DateTime.Now
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var portResults = await Task.WhenAll(portChecks);

        foreach (var item in portResults.Where(x => x != null))
            list.Add(item!);

        return list
            .GroupBy(d => $"{d.Id}|{d.IpAddress}")
            .Select(g => g.First())
            .OrderBy(d => d.Name)
            .ToList();
    }

    private async Task<bool> IsUdpLikelyBacnetAsync(string ipAddress, int port)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 500;
            udp.Connect(ipAddress, port);

            var data = new byte[] { 0x81, 0x0b, 0x00, 0x0c, 0x01, 0x20, 0xff, 0xff, 0x00, 0xff, 0x10, 0x08 };
            await udp.SendAsync(data, data.Length);

            var receiveTask = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(700));

            return completed == receiveTask;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<DeviceInfo>> ScanModbusAsync()
    {
        var list = new List<DeviceInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            var ipProps = ni.GetIPProperties();
            var uni = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null);

            if (uni == null || uni.IPv4Mask == null)
                continue;

            foreach (var ip in EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254))
                candidateIps.Add(ip.ToString());
        }

        var semaphore = new SemaphoreSlim(25);

        var tasks = candidateIps.Select(async ip =>
        {
            await semaphore.WaitAsync();

            try
            {
                if (!await IsTcpPortOpenAsync(ip, 502, 500))
                    return new List<DeviceInfo>();

                var devices = await TryReadModbusDevicesAsync(ip);

                if (devices.Count == 0)
                {
                    return new List<DeviceInfo>
                    {
                        new DeviceInfo
                        {
                            Id = $"MODBUS-PORT-{ip}",
                            Origin = "MODBUS",
                            Name = $"Possível dispositivo Modbus ({ip})",
                            Manufacturer = "Modbus TCP",
                            IpAddress = ip,
                            OpenPorts = "502",
                            DetectedServices = "Modbus | Porta 502 detetada, sem leitura confirmada",
                            Status = "Online",
                            Protocol = "Modbus TCP",
                            LastSeen = DateTime.Now
                        }
                    };
                }

                return devices;
            }
            catch
            {
                return new List<DeviceInfo>();
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var deviceList in results)
        {
            foreach (var device in deviceList)
            {
                if (seenIds.Add(device.Id))
                    list.Add(device);
            }
        }

        return list.OrderBy(d => d.Name).ToList();
    }

    private async Task<List<DeviceInfo>> TryReadModbusDevicesAsync(string ip)
    {
        var devices = new List<DeviceInfo>();

        try
        {
            using var tcpClient = new TcpClient();

            var connectTask = tcpClient.ConnectAsync(ip, 502);
            var completed = await Task.WhenAny(connectTask, Task.Delay(1200));

            if (completed != connectTask || !tcpClient.Connected)
                return devices;

            var factory = new ModbusFactory();
            using var master = factory.CreateMaster(tcpClient);
            master.Transport.ReadTimeout = 1000;
            master.Transport.WriteTimeout = 1000;
            master.Transport.Retries = 0;

            for (byte unitId = 1; unitId <= 20; unitId++)
            {
                var summaries = new List<string>();
                bool found = false;

                ushort[] candidateAddresses = { 0, 1, 10, 20, 100, 300, 400, 1000 };

                foreach (var address in candidateAddresses)
                {
                    try
                    {
                        var regs = master.ReadHoldingRegisters(unitId, address, 6);
                        if (regs != null && regs.Length > 0)
                        {
                            found = true;
                            summaries.Add($"Holding@{address}: " +
                                          string.Join(", ", regs.Select((v, i) => $"R{address + i}={v}")));

                            if (summaries.Count >= 2)
                                break;
                        }
                    }
                    catch
                    {
                    }
                }

                if (summaries.Count < 2)
                {
                    foreach (var address in candidateAddresses)
                    {
                        try
                        {
                            var regs = master.ReadInputRegisters(unitId, address, 6);
                            if (regs != null && regs.Length > 0)
                            {
                                found = true;
                                summaries.Add($"Input@{address}: " +
                                              string.Join(", ", regs.Select((v, i) => $"R{address + i}={v}")));

                                if (summaries.Count >= 2)
                                    break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (!found)
                    continue;

                string registerSummary = string.Join(" | ", summaries);

                devices.Add(new DeviceInfo
                {
                    Id = $"MODBUS-{ip}-U{unitId}",
                    Origin = "MODBUS",
                    Name = $"Modbus Device {ip} (Unit {unitId})",
                    Manufacturer = "Modbus TCP",
                    IpAddress = ip,
                    OpenPorts = "502",
                    DetectedServices = $"Modbus | Unit ID: {unitId} | {registerSummary}",
                    ModbusUnitId = unitId,
                    ModbusRegisterSummary = registerSummary,
                    Status = "Online",
                    Protocol = "Modbus TCP",
                    LastSeen = DateTime.Now
                });
            }
        }
        catch
        {
        }

        return devices;
    }

    private async Task<List<DeviceInfo>> ScanOnvifAsync()
    {
        var list = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(async () =>
        {
            UdpClient? udp = null;

            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.EnableBroadcast = true;
                udp.MulticastLoopback = false;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

                var probeXml = BuildOnvifProbeMessage();
                var probeBytes = Encoding.UTF8.GetBytes(probeXml);

                var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
                await udp.SendAsync(probeBytes, probeBytes.Length, multicastEndpoint);

                await Task.Delay(150);

                var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, 3702);
                await udp.SendAsync(probeBytes, probeBytes.Length, broadcastEndpoint);

                foreach (var bcast in GetBroadcastAddresses())
                {
                    try
                    {
                        var subnetBroadcastEndpoint = new IPEndPoint(bcast, 3702);
                        await udp.SendAsync(probeBytes, probeBytes.Length, subnetBroadcastEndpoint);
                    }
                    catch
                    {
                    }
                }

                var stopAt = DateTime.UtcNow.AddSeconds(6);

                while (DateTime.UtcNow < stopAt)
                {
                    var receiveTask = udp.ReceiveAsync();
                    var completed = await Task.WhenAny(receiveTask, Task.Delay(500));

                    if (completed != receiveTask)
                        continue;

                    UdpReceiveResult result;
                    try
                    {
                        result = receiveTask.Result;
                    }
                    catch
                    {
                        continue;
                    }

                    string xml;
                    try
                    {
                        xml = Encoding.UTF8.GetString(result.Buffer);
                    }
                    catch
                    {
                        continue;
                    }

                    var parsed = ParseOnvifProbeMatch(xml, result.RemoteEndPoint.Address.ToString());
                    if (parsed == null)
                        continue;

                    string key = $"{parsed.Value.Ip}|{parsed.Value.XAddr}|{parsed.Value.EndpointAddress}";
                    if (!seen.Add(key))
                        continue;

                    var portInfo = await DetectOnvifServicePortsAsync(parsed.Value.Ip);

                    string manufacturer = BuildOnvifManufacturer(parsed.Value);
                    string displayName = BuildOnvifDisplayName(parsed.Value);

                    var serviceParts = new List<string> { "ONVIF" };

                    if (!string.IsNullOrWhiteSpace(parsed.Value.XAddr))
                        serviceParts.Add($"XAddr: {parsed.Value.XAddr}");

                    if (!string.IsNullOrWhiteSpace(parsed.Value.Scopes))
                        serviceParts.Add($"Scopes: {TrimText(parsed.Value.Scopes, 120)}");

                    if (!string.IsNullOrWhiteSpace(portInfo.services))
                        serviceParts.Add(portInfo.services);

                    var openPorts = new List<string> { "3702" };
                    if (!string.IsNullOrWhiteSpace(portInfo.bestPort) && !openPorts.Contains(portInfo.bestPort))
                        openPorts.Add(portInfo.bestPort);

                    list.Add(new DeviceInfo
                    {
                        Id = $"ONVIF-{parsed.Value.Ip}",
                        Origin = "ONVIF",
                        Name = displayName,
                        Manufacturer = manufacturer,
                        IpAddress = parsed.Value.Ip,
                        OpenPorts = string.Join(", ", openPorts),
                        DetectedServices = string.Join(" | ", serviceParts),
                        Status = "Online",
                        Protocol = "ONVIF",
                        OnvifXAddr = parsed.Value.XAddr,
                        OnvifScopes = parsed.Value.Scopes,
                        OnvifEndpointAddress = parsed.Value.EndpointAddress,
                        LastSeen = DateTime.Now
                    });
                }
            }
            catch
            {
            }
            finally
            {
                udp?.Dispose();
            }
        });

        var candidateIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            var ipProps = ni.GetIPProperties();
            var uni = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null);

            if (uni == null || uni.IPv4Mask == null)
                continue;

            foreach (var ip in EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254))
                candidateIps.Add(ip.ToString());
        }

        var semaphore = new SemaphoreSlim(35);

        var fallbackTasks = candidateIps.Select(async ip =>
        {
            await semaphore.WaitAsync();

            try
            {
                bool responds = await IsUdpLikelyOnvifAsync(ip, 3702);
                if (!responds)
                    return null;

                string key = $"ONVIF-FALLBACK-{ip}";
                if (!seen.Add(key))
                    return null;

                var portInfo = await DetectOnvifServicePortsAsync(ip);

                var ports = new List<string> { "3702" };
                if (!string.IsNullOrWhiteSpace(portInfo.bestPort) && !ports.Contains(portInfo.bestPort))
                    ports.Add(portInfo.bestPort);

                return new DeviceInfo
                {
                    Id = $"ONVIF-FALLBACK-{ip}",
                    Origin = "ONVIF",
                    Name = $"Possível dispositivo ONVIF ({ip})",
                    Manufacturer = "ONVIF / WS-Discovery",
                    IpAddress = ip,
                    OpenPorts = string.Join(", ", ports),
                    DetectedServices = string.IsNullOrWhiteSpace(portInfo.services)
                        ? "ONVIF | Porta 3702 / WS-Discovery detetado"
                        : $"ONVIF | Porta 3702 / WS-Discovery detetado | {portInfo.services}",
                    Status = "Online",
                    Protocol = "ONVIF",
                    LastSeen = DateTime.Now
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var fallbackResults = await Task.WhenAll(fallbackTasks);

        foreach (var item in fallbackResults.Where(x => x != null))
            list.Add(item!);

        return list
            .GroupBy(d => $"{d.IpAddress}|{d.OnvifXAddr}|{d.Id}")
            .Select(g => g.First())
            .OrderBy(d => d.Name)
            .ToList();
    }

    private async Task<bool> IsUdpLikelyOnvifAsync(string ipAddress, int port)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.ReceiveTimeout = 700;
            udp.Connect(ipAddress, port);

            var probeXml = BuildOnvifProbeMessage();
            var data = Encoding.UTF8.GetBytes(probeXml);

            await udp.SendAsync(data, data.Length);

            var receiveTask = udp.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(800));

            if (completed != receiveTask)
                return false;

            var result = receiveTask.Result;
            if (result.Buffer == null || result.Buffer.Length == 0)
                return false;

            string xml = Encoding.UTF8.GetString(result.Buffer);

            return xml.Contains("ProbeMatches", StringComparison.OrdinalIgnoreCase) ||
                   xml.Contains("ProbeMatch", StringComparison.OrdinalIgnoreCase) ||
                   xml.Contains("onvif", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<(string bestPort, string services)> DetectOnvifServicePortsAsync(string ip)
    {
        var open = new List<int>();
        int[] onvifPorts = { 80, 443, 554, 8080, 8000, 8899 };

        foreach (var port in onvifPorts)
        {
            if (await IsTcpPortOpenAsync(ip, port, 250))
                open.Add(port);
        }

        string bestPort = open.Count > 0 ? open.First().ToString() : "";
        string services = string.Join(", ", open.Select(GetServiceName));

        return (bestPort, services);
    }

    private static string BuildOnvifProbeMessage()
    {
        string messageId = $"uuid:{Guid.NewGuid()}";

        return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:tds=""http://www.onvif.org/ver10/device/wsdl"">
    <e:Header>
        <w:MessageID>{messageId}</w:MessageID>
        <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
        <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
    </e:Header>
    <e:Body>
        <d:Probe>
            <d:Types>tds:Device</d:Types>
        </d:Probe>
    </e:Body>
</e:Envelope>";
    }

    private static (string Ip, string XAddr, string Scopes, string EndpointAddress)? ParseOnvifProbeMatch(string xml, string fallbackIp)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            XNamespace wsd = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            XNamespace wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";

            var probeMatch = doc.Descendants(wsd + "ProbeMatch").FirstOrDefault();
            if (probeMatch == null)
                return null;

            string xAddrs = probeMatch.Element(wsd + "XAddrs")?.Value?.Trim() ?? "";
            string scopes = probeMatch.Element(wsd + "Scopes")?.Value?.Trim() ?? "";
            string endpointAddress = probeMatch
                .Descendants(wsa + "Address")
                .FirstOrDefault()?.Value?.Trim() ?? "";

            string ip = TryExtractIpFromText(xAddrs);
            if (string.IsNullOrWhiteSpace(ip))
                ip = fallbackIp;

            if (string.IsNullOrWhiteSpace(ip))
                return null;

            return (ip, xAddrs, scopes, endpointAddress);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildOnvifDisplayName((string Ip, string XAddr, string Scopes, string EndpointAddress) parsed)
    {
        string scopes = parsed.Scopes ?? "";

        string? name =
            GetScopeValue(scopes, "name") ??
            GetScopeValue(scopes, "hardware") ??
            GetScopeValue(scopes, "location") ??
            GetScopeValue(scopes, "profile");

        return !string.IsNullOrWhiteSpace(name)
            ? $"ONVIF {name}"
            : $"ONVIF Device {parsed.Ip}";
    }

    private static string BuildOnvifManufacturer((string Ip, string XAddr, string Scopes, string EndpointAddress) parsed)
    {
        string scopes = parsed.Scopes ?? "";

        string? manufacturer =
            GetScopeValue(scopes, "manufacturer") ??
            GetScopeValue(scopes, "hardware");

        return !string.IsNullOrWhiteSpace(manufacturer)
            ? manufacturer
            : "ONVIF";
    }

    private static string? GetScopeValue(string scopes, string key)
    {
        if (string.IsNullOrWhiteSpace(scopes))
            return null;

        var parts = scopes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var marker = $"/{key}/";
            int idx = part.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string value = part[(idx + marker.Length)..];
                value = Uri.UnescapeDataString(value);
                return value.Replace('_', ' ').Trim();
            }
        }

        return null;
    }

    private static string TryExtractIpFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        foreach (var token in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri))
            {
                if (IPAddress.TryParse(uri.Host, out _))
                    return uri.Host;
            }
        }

        var match = Regex.Match(text, @"(\d{1,3}\.){3}\d{1,3}");
        return match.Success ? match.Value : "";
    }

    private static string TrimText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= maxLength
            ? value
            : value.Substring(0, maxLength) + "...";
    }

    private static string ReadBacnetStringProperty(
        BacnetClient client,
        BacnetAddress address,
        BacnetObjectId objectId,
        BacnetPropertyIds propertyId)
    {
        try
        {
            if (client.ReadPropertyRequest(address, objectId, propertyId, out IList<BacnetValue> values))
            {
                if (values != null && values.Count > 0 && values[0].Value != null)
                    return values[0].Value.ToString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static string ReadBacnetPresentValue(
        BacnetClient client,
        BacnetAddress address,
        BacnetObjectId objectId)
    {
        try
        {
            if (client.ReadPropertyRequest(address, objectId, BacnetPropertyIds.PROP_PRESENT_VALUE, out IList<BacnetValue> values))
            {
                if (values != null && values.Count > 0 && values[0].Value != null)
                    return values[0].Value.ToString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static string ReadBacnetObjectSummary(
        BacnetClient client,
        BacnetAddress address,
        BacnetObjectId deviceObjectId,
        int maxObjects)
    {
        try
        {
            if (!client.ReadPropertyRequest(address, deviceObjectId, BacnetPropertyIds.PROP_OBJECT_LIST, out IList<BacnetValue> values))
                return "";

            var parts = new List<string>();

            foreach (var item in values)
            {
                if (item.Value is not BacnetObjectId obj)
                    continue;

                if (obj.type == BacnetObjectTypes.OBJECT_DEVICE)
                    continue;

                string objName = ReadBacnetStringProperty(client, address, obj, BacnetPropertyIds.PROP_OBJECT_NAME);
                string presentValue = ReadBacnetPresentValue(client, address, obj);

                string label = !string.IsNullOrWhiteSpace(objName)
                    ? objName
                    : $"{obj.type}:{obj.instance}";

                if (!string.IsNullOrWhiteSpace(presentValue))
                    label += $"={presentValue}";

                parts.Add(label);

                if (parts.Count >= maxObjects)
                    break;
            }

            return string.Join(", ", parts);
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractBacnetIp(BacnetAddress? adr)
    {
        try
        {
            if (adr == null)
                return "";

            if (adr.adr != null && adr.adr.Length >= 4)
                return $"{adr.adr[0]}.{adr.adr[1]}.{adr.adr[2]}.{adr.adr[3]}";

            var txt = adr.ToString() ?? "";
            var match = Regex.Match(txt, @"(\d{1,3}\.){3}\d{1,3}");
            return match.Success ? match.Value : txt;
        }
        catch
        {
            return "";
        }
    }

    private async Task<(string openPorts, string services)> DetectCommonPortsAsync(string ipAddress)
    {
        var openPorts = new List<int>();

        var checks = CommonPorts.Select(async port =>
        {
            bool isOpen = await IsTcpPortOpenAsync(ipAddress, port, 180);
            return new { Port = port, IsOpen = isOpen };
        });

        var results = await Task.WhenAll(checks);

        foreach (var result in results)
        {
            if (result.IsOpen)
                openPorts.Add(result.Port);
        }

        openPorts = openPorts.OrderBy(p => p).ToList();

        string portsText = openPorts.Count > 0
            ? string.Join(", ", openPorts)
            : "Nenhuma";

        string servicesText = openPorts.Count > 0
            ? string.Join(", ", openPorts.Select(GetServiceName))
            : "Nenhum";

        return (portsText, servicesText);
    }

    private async Task<bool> IsTcpPortOpenAsync(string ipAddress, int port, int timeoutMs)
    {
        try
        {
            using var tcpClient = new TcpClient();

            var connectTask = tcpClient.ConnectAsync(ipAddress, port);
            var timeoutTask = Task.Delay(timeoutMs);

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

            if (completedTask == timeoutTask)
                return false;

            await connectTask;
            return tcpClient.Connected;
        }
        catch
        {
            return false;
        }
    }

    private string GetServiceName(int port)
    {
        return port switch
        {
            80 => "HTTP",
            443 => "HTTPS",
            22 => "SSH",
            554 => "RTSP",
            8000 => "Hikvision",
            8080 => "HTTP Alt",
            47808 => "BACnet",
            502 => "Modbus",
            3702 => "WS-Discovery",
            8899 => "ONVIF Alt",
            _ => $"Porta {port}"
        };
    }

    private void ClassifyDevice(DeviceInfo device)
    {
        string manufacturer = (device.Manufacturer ?? "").ToLower();
        string origin = (device.Origin ?? "").ToLower();
        string name = (device.Name ?? "").ToLower();
        string openPorts = (device.OpenPorts ?? "").ToLower();
        string services = (device.DetectedServices ?? "").ToLower();

        if (origin.Contains("bluetooth"))
            device.Protocol = "BLE";
        else if (origin.Contains("wifi"))
            device.Protocol = "Wi-Fi";
        else if (origin.Contains("lan"))
            device.Protocol = "LAN";
        else if (origin.Contains("bacnet") || openPorts.Contains("47808"))
            device.Protocol = "BACnet/IP";
        else if (origin.Contains("modbus") || openPorts.Contains("502"))
            device.Protocol = "Modbus TCP";
        else if (origin.Contains("onvif") || openPorts.Contains("3702"))
            device.Protocol = "ONVIF";
        else
            device.Protocol = "Desconhecido";

        if (openPorts.Contains("502") || services.Contains("modbus"))
        {
            device.DeviceType = "PLC / Modbus Device";
            device.Icon = "⚙️";
            return;
        }

        if (openPorts.Contains("47808") || services.Contains("bacnet"))
        {
            device.DeviceType = "Sistema BACnet";
            device.Icon = "🏢";
            return;
        }

        if (openPorts.Contains("554") || services.Contains("rtsp") ||
            openPorts.Contains("8000") || manufacturer.Contains("hikvision") ||
            openPorts.Contains("3702") || services.Contains("onvif"))
        {
            device.DeviceType = "Câmara IP";
            device.Icon = "🎥";
            return;
        }

        if (manufacturer.Contains("cisco") ||
            manufacturer.Contains("mikrotik") ||
            manufacturer.Contains("tp-link") ||
            manufacturer.Contains("ubiquiti") ||
            name.Contains("router") ||
            name.Contains("gateway"))
        {
            device.DeviceType = "Equipamento de Rede";
            device.Icon = "🌐";
            return;
        }

        if (manufacturer.Contains("intel") ||
            manufacturer.Contains("dell") ||
            manufacturer.Contains("hp") ||
            manufacturer.Contains("lenovo") ||
            name.Contains("desktop") ||
            name.Contains("laptop") ||
            name.Contains("pc"))
        {
            device.DeviceType = "Computador";
            device.Icon = "💻";
            return;
        }

        if (manufacturer.Contains("apple") ||
            manufacturer.Contains("samsung") ||
            manufacturer.Contains("xiaomi") ||
            name.Contains("iphone") ||
            name.Contains("android"))
        {
            device.DeviceType = "Dispositivo Móvel";
            device.Icon = "📱";
            return;
        }

        if (name.Contains("printer") ||
            manufacturer.Contains("epson") ||
            manufacturer.Contains("canon") ||
            manufacturer.Contains("brother"))
        {
            device.DeviceType = "Impressora";
            device.Icon = "🖨️";
            return;
        }

        if (name.Contains("jbl") ||
            name.Contains("speaker") ||
            name.Contains("audio"))
        {
            device.DeviceType = "Dispositivo Áudio";
            device.Icon = "🎧";
            return;
        }

        if (origin.Contains("bluetooth"))
        {
            device.DeviceType = "Dispositivo BLE";
            device.Icon = "📶";
            return;
        }

        if (origin.Contains("wifi"))
        {
            device.DeviceType = "Dispositivo Wi-Fi";
            device.Icon = "📡";
            return;
        }

        device.DeviceType = "Dispositivo Desconhecido";
        device.Icon = "❓";
    }

    private void ApplyFilters()
    {
        /*
        if (ComboFiltroAtributo.SelectedValue is int id && id > 0)
            _atributoFiltroId = id;
        else
            _atributoFiltroId = null;

        _ativosView.Refresh();
        */

        var selectedAtributo = GetSelectedAtributo();


        var selectedOrigin = GetSelectedOrigin();
        var nameFilter = NameFilterTextBox.Text?.Trim() ?? string.Empty;
        var manufacturerFilter = ManufacturerFilterTextBox.Text?.Trim() ?? string.Empty;

        IEnumerable<DeviceInfo> query = _allDevices;

        // if (!string.Equals(selectedAtributo, "Todos", StringComparison.OrdinalIgnoreCase))
            //query = query.Where(d => string.Equals(d.Atributos, selectedOrigin, StringComparison.OrdinalIgnoreCase));


        if (!string.Equals(selectedOrigin, "Todas", StringComparison.OrdinalIgnoreCase))
            query = query.Where(d => string.Equals(d.Origin, selectedOrigin, StringComparison.OrdinalIgnoreCase));
        
        // Filtro do Nome ou IP (Pesquisa nos dois campos ao mesmo tempo)
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            query = query.Where(d => 
                (d.Name ?? "").Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
                (d.IpAddress ?? "").Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
            );
        }

        if (!string.IsNullOrWhiteSpace(manufacturerFilter))
            query = query.Where(d => (d.Manufacturer ?? "").Contains(manufacturerFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.Equals(selectedAtributo, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(d => string.Equals(d.AtributoAtual?.Trim(), selectedAtributo.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = query
            .OrderByDescending(d => d.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Origin)
            .ThenBy(d => d.Name)
            .ToList();

        DevicesGrid.ItemsSource = filteredList;
        UpdateSummaryCards(_allDevices);
    }

    private void UpdateSummaryCards(IEnumerable<DeviceInfo> devices)
    {
        var list = devices.ToList();

        TotalCountText.Text = list.Count.ToString();
        BluetoothCountText.Text = list.Count(d => d.Origin.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)).ToString();
        WifiCountText.Text = list.Count(d => d.Origin.Equals("WIFI", StringComparison.OrdinalIgnoreCase)).ToString();
        LanCountText.Text = list.Count(d => d.Origin.Equals("LAN", StringComparison.OrdinalIgnoreCase)).ToString();
        BacnetCountText.Text = list.Count(d => d.Origin.Equals("BACNET", StringComparison.OrdinalIgnoreCase)).ToString();
        ModbusCountText.Text = list.Count(d => d.Origin.Equals("MODBUS", StringComparison.OrdinalIgnoreCase)).ToString();
        OnvifCountText.Text = list.Count(d => d.Origin.Equals("ONVIF", StringComparison.OrdinalIgnoreCase)).ToString();
        OnlineCountText.Text = list.Count(d => d.Status.Equals("Online", StringComparison.OrdinalIgnoreCase)).ToString();
    }

    private void UpdateShadowCount()
    {
        ShadowCountText.Text = $"{_shadowDevices.Count} novos";
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "devices_export",
                DefaultExt = ".csv",
                Filter = "CSV files (.csv)|*.csv"
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                var exportDevices = GetDevicesToExport();
                var lines = new List<string>();

                lines.Add("ID,Origem,Tipo,Nome,Fabricante,MAC,IP,Estado,Protocolo,PortasAbertas,Servicos,RSSI,DistanciaMetros,UltimaDetecao,BacnetDeviceId,BacnetVendor,BacnetModelo,BacnetFirmware,BacnetObjetos,ModbusUnitId,ModbusRegistos,OnvifXAddr,OnvifScopes,OnvifEndpoint");

                foreach (var device in exportDevices)
                {
                    lines.Add(string.Join(",",
                        EscapeCsv(device.Id),
                        EscapeCsv(device.Origin),
                        EscapeCsv(device.DeviceType),
                        EscapeCsv(device.Name),
                        EscapeCsv(device.Manufacturer),
                        EscapeCsv(device.MacAddress),
                        EscapeCsv(device.IpAddress),
                        EscapeCsv(device.Status),
                        EscapeCsv(device.Protocol),
                        EscapeCsv(device.OpenPorts),
                        EscapeCsv(device.DetectedServices),
                        EscapeCsv(device.Rssi?.ToString() ?? ""),
                        EscapeCsv(device.EstimatedDistanceMeters?.ToString() ?? ""),
                        EscapeCsv(device.LastSeen == DateTime.MinValue ? "" : device.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")),
                        EscapeCsv(device.BacnetDeviceId?.ToString() ?? ""),
                        EscapeCsv(device.BacnetVendorName),
                        EscapeCsv(device.BacnetModelName),
                        EscapeCsv(device.BacnetFirmware),
                        EscapeCsv(device.BacnetObjectSummary),
                        EscapeCsv(device.ModbusUnitId?.ToString() ?? ""),
                        EscapeCsv(device.ModbusRegisterSummary),
                        EscapeCsv(device.OnvifXAddr),
                        EscapeCsv(device.OnvifScopes),
                        EscapeCsv(device.OnvifEndpointAddress)
                    ));
                }

                System.IO.File.WriteAllLines(dialog.FileName, lines, Encoding.UTF8);

                MessageBox.Show(
                    $"Exportação CSV concluída! {exportDevices.Count} dispositivo(s) exportado(s).",
                    "Export CSV",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao exportar CSV: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "devices_export",
                DefaultExt = ".txt",
                Filter = "Text files (.txt)|*.txt"
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                var exportDevices = GetDevicesToExport();
                var lines = new List<string>();

                lines.Add("LOCAL DEVICE MONITOR");
                lines.Add("Exportação de dispositivos");
                lines.Add($"Data: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                lines.Add($"Registos exportados: {exportDevices.Count}");
                lines.Add("--------------------------------------------------");
                lines.Add("");

                int i = 1;

                foreach (var device in exportDevices)
                {
                    lines.Add($"Dispositivo {i}");
                    lines.Add($"ID: {device.Id}");
                    lines.Add($"Origem: {device.Origin}");
                    lines.Add($"Tipo: {device.DeviceType}");
                    lines.Add($"Nome: {device.Name}");
                    lines.Add($"Fabricante: {device.Manufacturer}");
                    lines.Add($"MAC: {device.MacAddress}");
                    lines.Add($"IP: {device.IpAddress}");
                    lines.Add($"Estado: {device.Status}");
                    lines.Add($"Protocolo: {device.Protocol}");
                    lines.Add($"Portas: {device.OpenPorts}");
                    lines.Add($"Serviços: {device.DetectedServices}");
                    lines.Add($"RSSI: {device.Rssi}");
                    lines.Add($"Distância: {device.EstimatedDistanceMeters}");
                    lines.Add($"Última deteção: {device.LastSeen}");

                    if (device.Origin.Equals("BACNET", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"BACnet ID: {device.BacnetDeviceId}");
                        lines.Add($"BACnet Vendor: {device.BacnetVendorName}");
                        lines.Add($"BACnet Modelo: {device.BacnetModelName}");
                        lines.Add($"BACnet Firmware: {device.BacnetFirmware}");
                        lines.Add($"BACnet Objetos: {device.BacnetObjectSummary}");
                    }

                    if (device.Origin.Equals("MODBUS", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"Modbus Unit: {device.ModbusUnitId}");
                        lines.Add($"Registos: {device.ModbusRegisterSummary}");
                    }

                    if (device.Origin.Equals("ONVIF", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"ONVIF XAddr: {device.OnvifXAddr}");
                        lines.Add($"ONVIF Scopes: {device.OnvifScopes}");
                        lines.Add($"ONVIF Endpoint: {device.OnvifEndpointAddress}");
                    }

                    lines.Add("");
                    lines.Add("--------------------------------------------------");
                    lines.Add("");
                    i++;
                }

                System.IO.File.WriteAllLines(dialog.FileName, lines, Encoding.UTF8);

                MessageBox.Show(
                    $"Exportação TXT concluída! {exportDevices.Count} dispositivo(s) exportado(s).",
                    "Export TXT",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao exportar TXT: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<DeviceInfo> GetDevicesToExport()
    {
        if (DevicesGrid.ItemsSource is IEnumerable<DeviceInfo> visibleDevices)
            return visibleDevices.ToList();

        return _allDevices.ToList();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        value = value.Replace("\"", "\"\"");
        return $"\"{value}\"";
    }

    private string GetSelectedOrigin()
    {
        if (OriginComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
            return value;

        return "Todas";
    }
    private string GetSelectedAtributo()
    {
  
        if (AtributoComboBox.SelectedItem is LocalDeviceMonitor.App.Modelos.Atributo attr)
        {
            return attr.Nome;
        }

        return "Todos";
    }

    private static async Task<string> ExecuteCommandAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private string GetBluetoothCompanyName(ushort companyId)
    {
        if (BluetoothCompanies.TryGetValue(companyId, out var name))
            return name;

        return $"CompanyID {companyId}";
    }

    private void EnrichManufacturerFromMac(DeviceInfo device)
    {
        if (device == null)
            return;

        var vendor = GetVendorFromMac(device.MacAddress);
        if (string.IsNullOrWhiteSpace(vendor))
            return;

        if (string.IsNullOrWhiteSpace(device.Manufacturer) ||
            device.Manufacturer.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase) ||
            device.Manufacturer.Equals("LAN Host", StringComparison.OrdinalIgnoreCase) ||
            device.Manufacturer.Equals("WiFi Client", StringComparison.OrdinalIgnoreCase) ||
            device.Manufacturer.Equals("WiFi Access Point", StringComparison.OrdinalIgnoreCase) ||
            device.Manufacturer.StartsWith("CompanyID ", StringComparison.OrdinalIgnoreCase))
        {
            device.Manufacturer = vendor;
        }
    }

    private string GetVendorFromMac(string? macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
            return "";

        var normalized = NormalizeMacForOui(macAddress);
        if (normalized.Length < 6)
            return "";

        var oui = normalized.Substring(0, 6);

        if (OuiVendors.TryGetValue(oui, out var vendor))
            return vendor;

        return "";
    }

    private static string NormalizeMacForOui(string macAddress)
    {
        return Regex.Replace(macAddress ?? "", "[^0-9A-Fa-f]", "")
            .ToUpperInvariant();
    }

    private static int ConvertPercentToRssi(int percent)
    {
        percent = Math.Clamp(percent, 1, 100);
        return (percent / 2) - 100;
    }

    private static double EstimateDistance(int rssi, int txPower, double n)
    {
        return Math.Round(Math.Pow(10d, (txPower - rssi) / (10d * n)), 2);
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static IEnumerable<IPAddress> EnumerateSubnetHosts(IPAddress address, IPAddress mask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        var network = new byte[4];
        var broadcast = new byte[4];

        for (int i = 0; i < 4; i++)
        {
            network[i] = (byte)(ipBytes[i] & maskBytes[i]);
            broadcast[i] = (byte)(network[i] | ~maskBytes[i]);
        }

        uint start = BitConverter.ToUInt32(network.Reverse().ToArray(), 0);
        uint end = BitConverter.ToUInt32(broadcast.Reverse().ToArray(), 0);

        for (uint i = start + 1; i < end; i++)
        {
            var bytes = BitConverter.GetBytes(i).Reverse().ToArray();
            yield return new IPAddress(bytes);
        }
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            var ipProps = ni.GetIPProperties();

            foreach (var uni in ipProps.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork || uni.IPv4Mask == null)
                    continue;

                var ipBytes = uni.Address.GetAddressBytes();
                var maskBytes = uni.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];

                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                yield return new IPAddress(broadcast);
            }
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;

        if (_isDarkMode)
            ApplyDarkTheme();
        else
            ApplyLightTheme();
    }

    private void ApplyLightTheme()
    {
        SetBrush("AppWindowBackgroundBrush", "#EEF3F9");
        SetBrush("PanelBrush", "#FFFFFF");
        SetBrush("CardBrush", "#FFFFFF");
        SetBrush("BorderBrushTheme", "#DCE5F1");
        SetBrush("PrimaryTextBrush", "#0F172A");
        SetBrush("SecondaryTextBrush", "#64748B");
        SetBrush("GridHeaderBrush", "#F7FAFE");
        SetBrush("GridRowBrush", "#FFFFFF");
        SetBrush("GridAltRowBrush", "#FAFCFF");
        SetBrush("GridHoverBrush", "#F2F7FF");
        SetBrush("GridSelectedBrush", "#DBEAFE");
        SetBrush("InputBackgroundBrush", "#F8FAFC");
        SetBrush("InputBorderBrush", "#CBD5E1");

        ThemeToggleButton.Content = "🌙";
    }

    private void ApplyDarkTheme()
    {
        SetBrush("AppWindowBackgroundBrush", "#0B1220");
        SetBrush("PanelBrush", "#111827");
        SetBrush("CardBrush", "#111827");
        SetBrush("BorderBrushTheme", "#334155");
        SetBrush("PrimaryTextBrush", "#E5E7EB");
        SetBrush("SecondaryTextBrush", "#94A3B8");
        SetBrush("GridHeaderBrush", "#1F2937");
        SetBrush("GridRowBrush", "#111827");
        SetBrush("GridAltRowBrush", "#172033");
        SetBrush("GridHoverBrush", "#1E293B");
        SetBrush("GridSelectedBrush", "#1D4ED8");
        SetBrush("InputBackgroundBrush", "#0F172A");
        SetBrush("InputBorderBrush", "#334155");

        ThemeToggleButton.Content = "☀️";
    }


    private void SetBrush(string key, string hexColor)
    {
        Resources[key] = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor));
    }

    private void OpenDetailsWindow_Click(object sender, RoutedEventArgs e)
    {
        var device = DevicesGrid.SelectedItem as DeviceInfo;
        if (device == null)
        {
            MessageBox.Show("Selecione um dispositivo para ver os detalhes.", "Detalhes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Nome: {device.Name}");
        sb.AppendLine($"Origem: {device.Origin}");
        sb.AppendLine($"Tipo: {device.DeviceType}");
        sb.AppendLine($"Fabricante: {device.Manufacturer}");
        sb.AppendLine($"MAC: {device.MacAddress}");
        sb.AppendLine($"IP: {device.IpAddress}");
        sb.AppendLine($"Estado: {device.Status}");
        sb.AppendLine($"Protocolo: {device.Protocol}");
        sb.AppendLine($"Portas: {device.OpenPorts}");
        sb.AppendLine($"Serviços: {device.DetectedServices}");

        if (device.Rssi.HasValue)
            sb.AppendLine($"RSSI: {device.Rssi}");
        if (device.EstimatedDistanceMeters.HasValue)
            sb.AppendLine($"Distância (m): {device.EstimatedDistanceMeters}");

        sb.AppendLine($"Última deteção: {device.LastSeen}");

        if (device.BacnetDeviceId.HasValue || !string.IsNullOrWhiteSpace(device.BacnetVendorName))
        {
            sb.AppendLine($"BACnet ID: {device.BacnetDeviceId}");
            sb.AppendLine($"BACnet Vendor: {device.BacnetVendorName}");
            sb.AppendLine($"BACnet Modelo: {device.BacnetModelName}");
        }

        if (device.ModbusUnitId.HasValue)
            sb.AppendLine($"Modbus Unit: {device.ModbusUnitId}");

        if (!string.IsNullOrWhiteSpace(device.OnvifXAddr))
            sb.AppendLine($"ONVIF XAddr: {device.OnvifXAddr}");

        MessageBox.Show(sb.ToString(), "Detalhes do Dispositivo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EditDevice_Click(object sender, RoutedEventArgs e)
    {
        DeviceInfo? device = (sender as Button)?.DataContext as DeviceInfo
            ?? DevicesGrid.SelectedItem as DeviceInfo
            ?? ShadowGrid.SelectedItem as DeviceInfo;

        if (device == null)
        {
            MessageBox.Show("Selecione um dispositivo para editar a sua informação.", "Editar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editWindow = new EditDeviceWindow(device, _repo) { Owner = this };

        if (editWindow.ShowDialog() == true)
        {
            ShowToast("Sucesso", "Detalhes gravados com sucesso!", 3);
            ApplyFilters();

     
            DevicesGrid.Items.Refresh();
            ShadowGrid.Items.Refresh();
        }
    }
    private void BtnDetalhes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DeviceInfo device)
        {
            var db = new AssetDatabase();
            db.EnsureDatabaseCreated();

            var window = new DeviceDetailsWindow(device);
            window.Owner = this;
            window.ShowDialog();
        }
    }
}
