Network Device Scanner 

<img width="1536" height="1024" alt="image" src="https://github.com/user-attachments/assets/7f886a4d-7280-4e52-8f5d-4419c7150383" />

📌 Description

Desktop application developed in C# (WPF) for monitoring and identifying devices within a local network, supporting multiple discovery and communication protocols.
The system enables continuous network scanning, device tracking, Shadow IT detection, and direct interaction with network endpoints.

🎯 Problem
In heterogeneous network environments, it is difficult to:
  - Identify all active devices
  - Monitor real-time network changes
  - Detect unauthorized or unknown devices (Shadow IT)

💡 Solution
This application provides:
  - Multi-protocol device discovery
  - Continuous scanning and real-time updates
  - Baseline creation for network state tracking
  - Automatic detection of new devices
  - Direct interaction with devices (ping, browser access)

🏗️ Architecture Overview

The application is structured in multiple layers:

  - User Interface (WPF)
  - Provides a Master-Detail interface with a device list and detailed panel.
  - Core Logic Layer
  - Coordinates scanning, data processing, and system operations.
  - Device Scanner Engine
  - Central component responsible for orchestrating multiple scanning modules:
  - BLE Scanner
  - Wi-Fi Scanner
  - LAN Scanner
  - BACnet/IP Scanner
  - Modbus TCP Scanner
  - Device Management Layer
  - Baseline Manager
  - Stores the initial network state and compares it with future scans.
  - Shadow IT Detection Module
  - Identifies newly discovered devices not present in the baseline.
  - Device Actions Module
- Allows interaction with devices:
  - Ping functionality
  - Open device IP in browser
  - Export Module
  - Supports exporting device data into:
  - CSV
  - TXT
  - Notification System
  - Non-blocking toast notifications for user feedback.

⚙️ Technologies Used
- C#
- WPF (.NET)
- TCP/IP Networking
- BLE (Bluetooth Low Energy)
- BACnet/IP
- Modbus TCP

🚀 Features
🔍 Device Discovery
Multi-protocol scanning:
  - BLE
  - Wi-Fi
  - LAN
  - BACnet/IP
  - Modbus TCP

🔄 Continuous Monitoring
  - Automatic scanning loop
  - Real-time updates

🚨 Shadow IT Detection
  - Baseline creation
  - Detection of new/unrecognized devices

📡 Device Interaction
  - Ping devices
  - Open IP addresses in browser

📊 Data Export
  - Export results to CSV and TXT

🖥️ UI/UX
  - Master-Detail interface
  - DataGrid with detailed side panel
  - Non-blocking toast notifications
  - Fully asynchronous operations

🧠 Technical Decisions
  - Asynchronous Programming (async/await)
  - Prevents UI blocking and improves performance during network operations
  - Modular Scanner Architecture
  - Each protocol is implemented independently, allowing scalability and easy extension
  - Baseline Comparison Strategy
  - Master-Detail Pattern
  - Improves usability and data visualization

⚡ Performance
  - Non-blocking operations
  - Asynchronous network requests
  - Incremental processing of scan results
