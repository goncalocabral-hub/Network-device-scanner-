using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Windows.Devices.Bluetooth.Advertisement;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using NModbus;

namespace LocalDeviceMonitor.App;

public partial class MainWindow : Window
{
    private readonly List<DeviceInfo> _allDevices = new();

    private static readonly int[] CommonPorts =
    {
        80, 443, 22, 554, 8000, 8080, 47808, 502, 3702
    };

    public MainWindow()
    {
        InitializeComponent();
        DevicesGrid.ItemsSource = _allDevices;
        OriginComboBox.SelectedIndex = 0;
        StatusTextBlock.Text = "Pronto.";
        UpdateSummaryCards(_allDevices);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusTextBlock.Text = "A executar scan BLE + WIFI + LAN + BACNET + MODBUS + ONVIF...";
            _allDevices.Clear();
            DevicesGrid.ItemsSource = null;
            UpdateSummaryCards(_allDevices);

            var bleTask = ScanBleAsync();
            var wifiTask = ScanWifiAsync();
            var lanTask = ScanLanAsync();
            var bacnetTask = ScanBacnetAsync();
            var modbusTask = ScanModbusAsync();
            var onvifTask = ScanOnvifAsync();

            await Task.WhenAll(bleTask, wifiTask, lanTask, bacnetTask, modbusTask, onvifTask);

            _allDevices.AddRange(bleTask.Result);
            _allDevices.AddRange(wifiTask.Result);
            _allDevices.AddRange(lanTask.Result);
            _allDevices.AddRange(bacnetTask.Result);
            _allDevices.AddRange(modbusTask.Result);
            _allDevices.AddRange(onvifTask.Result);

            var uniqueDevices = _allDevices
                .GroupBy(d => $"{d.Origin}|{d.Id}|{d.IpAddress}")
                .Select(g => g.First())
                .ToList();

            _allDevices.Clear();
            _allDevices.AddRange(uniqueDevices);

            ApplyFilters();
            StatusTextBlock.Text = $"Scan concluído. {_allDevices.Count} dispositivo(s)/rede(s) encontrados.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao executar scan: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Erro no scan.";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        OriginComboBox.SelectedIndex = 0;
        NameFilterTextBox.Text = string.Empty;
        ManufacturerFilterTextBox.Text = string.Empty;

        DevicesGrid.ItemsSource = null;
        DevicesGrid.ItemsSource = _allDevices;

        if (_allDevices.Count > 0)
            DevicesGrid.SelectedIndex = 0;

        UpdateSummaryCards(_allDevices);
        UpdateDetailsStatusBadge();
        StatusTextBlock.Text = $"Filtros limpos. {_allDevices.Count} registo(s) disponíveis.";
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded)
            return;

        ApplyFilters();
    }

    private void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDetailsStatusBadge();
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
            Dispatcher.Invoke(() =>
            {
                string mac = FormatBluetoothAddress(args.BluetoothAddress);
                string name = args.Advertisement.LocalName;
                int rssi = args.RawSignalStrengthInDBm;

                string manufacturer = "Desconhecido";
                if (args.Advertisement.ManufacturerData.Count > 0)
                {
                    var companyId = args.Advertisement.ManufacturerData[0].CompanyId;
                    manufacturer = companyId == 6 ? "Kontakt.io" : $"CompanyId {companyId}";
                }

                var device = new DeviceInfo
                {
                    Id = mac,
                    Origin = "Bluetooth",
                    Name = string.IsNullOrWhiteSpace(name) ? "BLE Device" : name,
                    Manufacturer = manufacturer,
                    MacAddress = mac,
                    Rssi = rssi,
                    EstimatedDistanceMeters = EstimateDistance(rssi, -59, 2.2)
                };

                ClassifyDevice(device);
                result[args.BluetoothAddress] = device;
            });
        }

        watcher.Received += OnReceived;
        watcher.Start();
        await Task.Delay(8000);
        watcher.Stop();
        watcher.Received -= OnReceived;

        return result.Values.OrderByDescending(d => d.Rssi ?? int.MinValue).ToList();
    }

    private async Task<List<DeviceInfo>> ScanWifiAsync()
    {
        var output = await ExecuteCommandAsync("netsh", "wlan show networks mode=bssid");
        var list = new List<DeviceInfo>();

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

                var device = new DeviceInfo
                {
                    Id = string.IsNullOrWhiteSpace(bssid) ? Guid.NewGuid().ToString("N") : bssid,
                    Origin = "WIFI",
                    Name = name,
                    Manufacturer = "WiFi",
                    MacAddress = bssid,
                    Rssi = rssi,
                    EstimatedDistanceMeters = currentSignalPercent.HasValue ? EstimateDistance(rssi, -50, 2.6) : null
                };

                ClassifyDevice(device);
                list.Add(device);
            }
        }

        return list;
    }

    private async Task<List<DeviceInfo>> ScanLanAsync()
    {
        var list = new List<DeviceInfo>();
        var ipStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            var hosts = EnumerateSubnetHosts(uni.Address, uni.IPv4Mask).Take(254).ToList();

            var pingTasks = hosts.Select(async ip =>
            {
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

                    var (openPorts, services) = await DetectCommonPortsAsync(ip.ToString());

                    var device = new DeviceInfo
                    {
                        Id = ip.ToString(),
                        Origin = "LAN",
                        Name = hostName,
                        Manufacturer = "LAN Host",
                        IpAddress = ip.ToString(),
                        OpenPorts = openPorts,
                        DetectedServices = services
                    };

                    ClassifyDevice(device);
                    return device;
                }
                catch
                {
                    return null;
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
                ClassifyDevice(existing);
            }
            else if (ipStrings.Add(ip))
            {
                var (openPorts, services) = await DetectCommonPortsAsync(ip);

                var device = new DeviceInfo
                {
                    Id = ip,
                    Origin = "LAN",
                    Name = ip,
                    Manufacturer = "LAN Host",
                    IpAddress = ip,
                    MacAddress = mac,
                    OpenPorts = openPorts,
                    DetectedServices = services
                };

                ClassifyDevice(device);
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

                        var device = new DeviceInfo
                        {
                            Id = $"BACNET-{deviceId}",
                            Origin = "BACNET",
                            Name = $"BACnet Device {deviceId}",
                            Manufacturer = vendorId > 0 ? $"Vendor {vendorId}" : "BACnet",
                            IpAddress = ip,
                            OpenPorts = "47808",
                            DetectedServices = "BACnet | I-Am recebido",
                            BacnetDeviceId = deviceId,
                            Status = "Online",
                            Protocol = "BACnet/IP"
                        };

                        ClassifyDevice(device);

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

        var portChecks = candidateIps.Select(async ip =>
        {
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

                var device = new DeviceInfo
                {
                    Id = $"BACNET-PORT-{ip}",
                    Origin = "BACNET",
                    Name = $"Possível dispositivo BACnet ({ip})",
                    Manufacturer = "BACnet/IP",
                    IpAddress = ip,
                    OpenPorts = "47808",
                    DetectedServices = "BACnet | Porta 47808 detetada",
                    Status = "Online",
                    Protocol = "BACnet/IP"
                };

                ClassifyDevice(device);
                return device;
            }
            catch
            {
                return null;
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
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        var tasks = candidateIps.Select(async ip =>
        {
            try
            {
                if (!await IsTcpPortOpenAsync(ip, 502, 200))
                    return null;

                var modbusDevice = await TryReadModbusDeviceAsync(ip);
                return modbusDevice;
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var device in results.Where(d => d != null))
        {
            if (seenIps.Add(device!.IpAddress))
                list.Add(device!);
        }

        return list.OrderBy(d => d.Name).ToList();
    }

    private async Task<DeviceInfo?> TryReadModbusDeviceAsync(string ip)
    {
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(ip, 502);
            var completed = await Task.WhenAny(connectTask, Task.Delay(700));

            if (completed != connectTask || !tcpClient.Connected)
                return null;

            var factory = new ModbusFactory();
            using var master = factory.CreateMaster(tcpClient);

            byte detectedUnitId = 1;
            ushort[]? registers = null;

            for (byte unitId = 1; unitId <= 5; unitId++)
            {
                try
                {
                    registers = master.ReadHoldingRegisters(unitId, 0, 4);
                    if (registers != null && registers.Length > 0)
                    {
                        detectedUnitId = unitId;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (registers == null || registers.Length == 0)
            {
                for (byte unitId = 1; unitId <= 5; unitId++)
                {
                    try
                    {
                        registers = master.ReadInputRegisters(unitId, 0, 4);
                        if (registers != null && registers.Length > 0)
                        {
                            detectedUnitId = unitId;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            string registerSummary = registers != null && registers.Length > 0
                ? string.Join(", ", registers.Select((v, i) => $"R{i}={v}"))
                : "Sem leitura de registos";

            var services = new List<string> { "Modbus" };
            services.Add($"Unit ID: {detectedUnitId}");
            if (!string.IsNullOrWhiteSpace(registerSummary))
                services.Add($"Registos: {registerSummary}");

            var device = new DeviceInfo
            {
                Id = $"MODBUS-{ip}",
                Origin = "MODBUS",
                Name = $"Modbus Device {ip}",
                Manufacturer = "Modbus TCP",
                IpAddress = ip,
                OpenPorts = "502",
                DetectedServices = string.Join(" | ", services),
                ModbusUnitId = detectedUnitId,
                ModbusRegisterSummary = registerSummary,
                Status = "Online",
                Protocol = "Modbus TCP"
            };

            ClassifyDevice(device);
            return device;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<DeviceInfo>> ScanOnvifAsync()
    {
        var list = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient(0);
                udp.EnableBroadcast = false;
                udp.MulticastLoopback = false;

                var probeXml = BuildOnvifProbeMessage();
                var probeBytes = Encoding.UTF8.GetBytes(probeXml);

                var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
                await udp.SendAsync(probeBytes, probeBytes.Length, multicastEndpoint);

                var stopAt = DateTime.UtcNow.AddSeconds(5);

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

                    var serviceParts = new List<string> { "ONVIF" };

                    if (!string.IsNullOrWhiteSpace(parsed.Value.XAddr))
                        serviceParts.Add($"XAddr: {parsed.Value.XAddr}");

                    if (!string.IsNullOrWhiteSpace(parsed.Value.Scopes))
                        serviceParts.Add($"Scopes: {TrimText(parsed.Value.Scopes, 120)}");

                    var device = new DeviceInfo
                    {
                        Id = $"ONVIF-{parsed.Value.Ip}",
                        Origin = "ONVIF",
                        Name = BuildOnvifDisplayName(parsed.Value),
                        Manufacturer = BuildOnvifManufacturer(parsed.Value),
                        IpAddress = parsed.Value.Ip,
                        OpenPorts = "3702",
                        DetectedServices = string.Join(" | ", serviceParts),
                        Status = "Online",
                        Protocol = "ONVIF",
                        OnvifXAddr = parsed.Value.XAddr,
                        OnvifScopes = parsed.Value.Scopes,
                        OnvifEndpointAddress = parsed.Value.EndpointAddress
                    };

                    ClassifyDevice(device);
                    list.Add(device);
                }
            }
            catch
            {
            }
        });

        return list
            .GroupBy(d => $"{d.IpAddress}|{d.OnvifXAddr}")
            .Select(g => g.First())
            .OrderBy(d => d.Name)
            .ToList();
    }

    private static string BuildOnvifProbeMessage()
    {
        string messageId = $"uuid:{Guid.NewGuid()}";

        return
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
    <e:Header>
        <w:MessageID>{messageId}</w:MessageID>
        <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
        <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
    </e:Header>
    <e:Body>
        <d:Probe>
            <d:Types>dn:NetworkVideoTransmitter</d:Types>
        </d:Probe>
    </e:Body>
</e:Envelope>";
    }

    private static (string Ip, string XAddr, string Scopes, string EndpointAddress)? ParseOnvifProbeMatch(string xml, string fallbackIp)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            XNamespace soap = "http://www.w3.org/2003/05/soap-envelope";
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
        else if (origin.Contains("bacnet"))
            device.Protocol = "BACnet/IP";
        else if (origin.Contains("modbus"))
            device.Protocol = "Modbus TCP";
        else if (origin.Contains("onvif"))
            device.Protocol = "ONVIF";
        else
            device.Protocol = "Desconhecido";

        if (origin.Contains("lan") && string.IsNullOrWhiteSpace(device.IpAddress))
            device.Status = "Desconhecido";
        else if (string.IsNullOrWhiteSpace(device.Status))
            device.Status = "Online";

        if (manufacturer.Contains("kontakt"))
        {
            device.DeviceType = "Beacon BLE";
            device.Icon = "📡";
        }
        else if (origin.Contains("onvif") || services.Contains("onvif"))
        {
            device.DeviceType = "Câmara ONVIF";
            device.Icon = "🎥";
            device.Protocol = "ONVIF";
        }
        else if (manufacturer.Contains("hikvision") || openPorts.Contains("8000") || services.Contains("rtsp"))
        {
            device.DeviceType = "Câmara IP";
            device.Icon = "📷";
        }
        else if (services.Contains("modbus") || origin.Contains("modbus") || openPorts.Contains("502"))
        {
            device.DeviceType = "Dispositivo Modbus";
            device.Icon = "⚙️";
            device.Protocol = "Modbus TCP";
        }
        else if (services.Contains("bacnet") || origin.Contains("bacnet"))
        {
            device.DeviceType = "Dispositivo BACnet";
            device.Icon = "🏢";
            device.Protocol = "BACnet/IP";
        }
        else if (manufacturer.Contains("tp-link") ||
                 manufacturer.Contains("cisco") ||
                 manufacturer.Contains("mikrotik"))
        {
            device.DeviceType = "Equipamento de Rede";
            device.Icon = "🌐";
        }
        else if (manufacturer.Contains("intel") ||
                 manufacturer.Contains("dell") ||
                 manufacturer.Contains("hp") ||
                 manufacturer.Contains("lenovo"))
        {
            device.DeviceType = "Computador";
            device.Icon = "💻";
        }
        else if (manufacturer.Contains("apple") ||
                 manufacturer.Contains("samsung") ||
                 manufacturer.Contains("xiaomi"))
        {
            device.DeviceType = "Dispositivo Móvel";
            device.Icon = "📱";
        }
        else if (name.Contains("jbl") || name.Contains("edifier"))
        {
            device.DeviceType = "Dispositivo Áudio";
            device.Icon = "🎧";
        }
        else if (services.Contains("http") || services.Contains("https"))
        {
            device.DeviceType = "Dispositivo Web";
            device.Icon = "🖥️";
        }
        else if (origin.Contains("bluetooth"))
        {
            device.DeviceType = "Dispositivo BLE";
            device.Icon = "📶";
        }
        else if (origin.Contains("wifi"))
        {
            device.DeviceType = "Rede Wi-Fi";
            device.Icon = "📡";
        }
        else if (origin.Contains("lan"))
        {
            device.DeviceType = "Dispositivo LAN";
            device.Icon = "🖧";
        }
        else
        {
            device.DeviceType = "Desconhecido";
            device.Icon = "❓";
        }
    }

    private void ApplyFilters()
    {
        var selectedOrigin = GetSelectedOrigin();
        var nameFilter = NameFilterTextBox.Text?.Trim() ?? string.Empty;
        var manufacturerFilter = ManufacturerFilterTextBox.Text?.Trim() ?? string.Empty;

        IEnumerable<DeviceInfo> query = _allDevices;

        if (!string.Equals(selectedOrigin, "Todas", StringComparison.OrdinalIgnoreCase))
            query = query.Where(d => string.Equals(d.Origin, selectedOrigin, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(d => (d.Name ?? "").Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(manufacturerFilter))
            query = query.Where(d => (d.Manufacturer ?? "").Contains(manufacturerFilter, StringComparison.OrdinalIgnoreCase));

        var filteredList = query.ToList();

        DevicesGrid.ItemsSource = null;
        DevicesGrid.ItemsSource = filteredList;

        if (filteredList.Count > 0)
            DevicesGrid.SelectedIndex = 0;

        UpdateSummaryCards(filteredList);
        UpdateDetailsStatusBadge();
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

    private void UpdateDetailsStatusBadge()
    {
        if (DevicesGrid.SelectedItem is not DeviceInfo device)
        {
            DetailsStatusBadge.Background = System.Windows.Media.Brushes.LightGray;
            DetailsStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            return;
        }

        switch (device.Status)
        {
            case "Online":
                DetailsStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DCFCE7"));
                DetailsStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#166534"));
                break;

            case "Offline":
                DetailsStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEE2E2"));
                DetailsStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#991B1B"));
                break;

            default:
                DetailsStatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEF3C7"));
                DetailsStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#92400E"));
                break;
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
                Filter = "Text documents (.txt)|*.txt"
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                var lines = new List<string>();

                lines.Add("LOCAL DEVICE MONITOR");
                lines.Add("Exportação de dispositivos da rede");
                lines.Add($"Data: {DateTime.Now}");
                lines.Add("--------------------------------------------------");
                lines.Add("");

                int i = 1;

                foreach (var device in _allDevices)
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
                    lines.Add($"Portas abertas: {device.OpenPorts}");
                    lines.Add($"Serviços: {device.DetectedServices}");
                    lines.Add($"RSSI: {device.Rssi}");
                    lines.Add($"Distância: {device.EstimatedDistanceMeters} m");

                    if (device.Origin.Equals("BACNET", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"BACnet Device ID: {device.BacnetDeviceId}");
                        lines.Add($"BACnet Vendor: {device.BacnetVendorName}");
                        lines.Add($"BACnet Modelo: {device.BacnetModelName}");
                        lines.Add($"BACnet Firmware: {device.BacnetFirmware}");
                        lines.Add($"BACnet Objetos: {device.BacnetObjectSummary}");
                    }

                    if (device.Origin.Equals("MODBUS", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add($"Modbus Unit ID: {device.ModbusUnitId}");
                        lines.Add($"Modbus Registos: {device.ModbusRegisterSummary}");
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

                System.IO.File.WriteAllLines(dialog.FileName, lines);

                MessageBox.Show("Exportação TXT concluída!", "Export TXT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao exportar TXT: {ex.Message}");
        }
    }

    private string GetSelectedOrigin()
    {
        if (OriginComboBox.SelectedItem is ComboBoxItem item && item.Content is string value)
            return value;

        return "Todas";
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
}