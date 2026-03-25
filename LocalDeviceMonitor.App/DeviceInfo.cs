using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalDeviceMonitor.App;

public class DeviceInfo : INotifyPropertyChanged
{
    private string _id = "";
    private string _origin = "";
    private string _icon = "❓";
    private string _deviceType = "Desconhecido";
    private string _name = "";
    private string _manufacturer = "";
    private string _macAddress = "";
    private string _ipAddress = "";
    private string _status = "Offline";
    private string _protocol = "";
    private int? _rssi;
    private double? _estimatedDistanceMeters;
    private string _openPorts = "";
    private string _detectedServices = "";

    private uint? _bacnetDeviceId;
    private string _bacnetVendorName = "";
    private string _bacnetModelName = "";
    private string _bacnetFirmware = "";
    private string _bacnetObjectSummary = "";

    private byte? _modbusUnitId;
    private string _modbusRegisterSummary = "";

    private string _onvifXAddr = "";
    private string _onvifScopes = "";
    private string _onvifEndpointAddress = "";

    private DateTime _lastSeen = DateTime.MinValue;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Origin
    {
        get => _origin;
        set => SetField(ref _origin, value);
    }

    public string Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public string DeviceType
    {
        get => _deviceType;
        set => SetField(ref _deviceType, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Manufacturer
    {
        get => _manufacturer;
        set => SetField(ref _manufacturer, value);
    }

    public string MacAddress
    {
        get => _macAddress;
        set => SetField(ref _macAddress, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetField(ref _ipAddress, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Protocol
    {
        get => _protocol;
        set => SetField(ref _protocol, value);
    }

    public int? Rssi
    {
        get => _rssi;
        set => SetField(ref _rssi, value);
    }

    public double? EstimatedDistanceMeters
    {
        get => _estimatedDistanceMeters;
        set => SetField(ref _estimatedDistanceMeters, value);
    }

    public string OpenPorts
    {
        get => _openPorts;
        set => SetField(ref _openPorts, value);
    }

    public string DetectedServices
    {
        get => _detectedServices;
        set => SetField(ref _detectedServices, value);
    }

    public uint? BacnetDeviceId
    {
        get => _bacnetDeviceId;
        set => SetField(ref _bacnetDeviceId, value);
    }

    public string BacnetVendorName
    {
        get => _bacnetVendorName;
        set => SetField(ref _bacnetVendorName, value);
    }

    public string BacnetModelName
    {
        get => _bacnetModelName;
        set => SetField(ref _bacnetModelName, value);
    }

    public string BacnetFirmware
    {
        get => _bacnetFirmware;
        set => SetField(ref _bacnetFirmware, value);
    }

    public string BacnetObjectSummary
    {
        get => _bacnetObjectSummary;
        set => SetField(ref _bacnetObjectSummary, value);
    }

    public byte? ModbusUnitId
    {
        get => _modbusUnitId;
        set => SetField(ref _modbusUnitId, value);
    }

    public string ModbusRegisterSummary
    {
        get => _modbusRegisterSummary;
        set => SetField(ref _modbusRegisterSummary, value);
    }

    public string OnvifXAddr
    {
        get => _onvifXAddr;
        set => SetField(ref _onvifXAddr, value);
    }

    public string OnvifScopes
    {
        get => _onvifScopes;
        set => SetField(ref _onvifScopes, value);
    }

    public string OnvifEndpointAddress
    {
        get => _onvifEndpointAddress;
        set => SetField(ref _onvifEndpointAddress, value);
    }

    public DateTime LastSeen
    {
        get => _lastSeen;
        set => SetField(ref _lastSeen, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}