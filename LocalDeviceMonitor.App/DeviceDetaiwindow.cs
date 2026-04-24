using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace LocalDeviceMonitor.App
{
    public class DeviceDetailsWindow : Window
    {
        public DeviceDetailsWindow(DeviceInfo device)
        {
            Title = device?.Name ?? "Detalhes do Dispositivo";
            Width = 480;
            Height = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var stack = new StackPanel
            {
                Margin = new Thickness(12),
                Orientation = Orientation.Vertical
            };

            void AddLine(string label, string? value)
            {
                var tb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(value) ? $"{label}: -" : $"{label}: {value}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 6)
                };
                stack.Children.Add(tb);
            }

            if (device != null)
            {
                AddLine("Nome", device.Name);
                AddLine("Origem", device.Origin);
                AddLine("Tipo", device.DeviceType);
                AddLine("Fabricante", device.Manufacturer);
                AddLine("MAC", device.MacAddress);
                AddLine("IP", device.IpAddress);
                AddLine("Estado", device.Status);
                AddLine("Protocolo", device.Protocol);
                AddLine("Portas Abertas", device.OpenPorts);
                AddLine("Serviços", device.DetectedServices);
                AddLine("RSSI", device.Rssi?.ToString());
                AddLine("Distância (m)", device.EstimatedDistanceMeters?.ToString());
                AddLine("Última deteção", device.LastSeen == DateTime.MinValue ? "" : device.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"));

                if (device.BacnetDeviceId.HasValue || !string.IsNullOrWhiteSpace(device.BacnetVendorName))
                {
                    AddLine("BACnet ID", device.BacnetDeviceId?.ToString());
                    AddLine("BACnet Vendor", device.BacnetVendorName);
                    AddLine("BACnet Modelo", device.BacnetModelName);
                }

                if (device.ModbusUnitId.HasValue)
                {
                    AddLine("Modbus Unit", device.ModbusUnitId?.ToString());
                    AddLine("Modbus Registos", device.ModbusRegisterSummary);
                }

                if (!string.IsNullOrWhiteSpace(device.OnvifXAddr))
                {
                    AddLine("ONVIF XAddr", device.OnvifXAddr);
                    AddLine("ONVIF Scopes", device.OnvifScopes);
                    AddLine("ONVIF Endpoint", device.OnvifEndpointAddress);
                }
            }

            scroll.Content = stack;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(scroll, 0);
            grid.Children.Add(scroll);

            var closeBtn = new Button
            {
                Content = "Fechar",
                Width = 80,
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            closeBtn.Click += (s, e) => this.Close();

            Grid.SetRow(closeBtn, 1);
            grid.Children.Add(closeBtn);

            this.Content = grid;
        }
    }
}
