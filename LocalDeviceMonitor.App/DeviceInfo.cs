namespace LocalDeviceMonitor.App;

public class DeviceInfo
{
    public string Id { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Icon { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public string Protocol { get; set; } = "";
    public int? Rssi { get; set; }
    public double? EstimatedDistanceMeters { get; set; }

    public string OpenPorts { get; set; } = "";
    public string DetectedServices { get; set; } = "";
    public uint? BacnetDeviceId { get; set; }
    public string BacnetVendorName { get; set; } = "";
    public string BacnetModelName { get; set; } = "";
    public string BacnetFirmware { get; set; } = "";
    public string BacnetObjectSummary { get; set; } = "";
    public byte? ModbusUnitId { get; set; }
    public string ModbusRegisterSummary { get; set; } = "";
    public string OnvifXAddr { get; set; } = "";
    public string OnvifScopes { get; set; } = "";
    public string OnvifEndpointAddress { get; set; } = "";
}
