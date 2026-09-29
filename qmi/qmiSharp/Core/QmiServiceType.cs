namespace qmiSharp.Core;

/// <summary>
/// QMUX / QMI Service Types (matches Qualcomm QCQMI.h and libqmi qmi-enums.h)
/// </summary>
public enum QmiServiceType : ushort
{
    Control = 0x00,   // CTL - Control Service
    CTL = 0x00,       // Alias for Control Service matching libqmi naming
    WDS = 0x01,       // WDS - Wireless Data Service
    DMS = 0x02,       // DMS - Device Management Service
    NAS = 0x03,       // NAS - Network Access Stratum
    QOS = 0x04,       // QOS - Quality of Service
    WMS = 0x05,       // WMS - Wireless Messaging Service
    PDS = 0x06,       // PDS - Position Determination Service
    AUTH = 0x07,      // AUTH - Authentication
    AT = 0x08,        // AT - AT Service
    VOICE = 0x09,     // VOICE - Voice Service
    CAT2 = 0x0A,      // CAT2 - Card Application Toolkit v2
    UIM = 0x0B,       // UIM - User Identity Module
    PBM = 0x0C,       // PBM - Phonebook Manager
    QCHAT = 0x0D,     // QCHAT Service
    RMTFS = 0x0E,     // Remote File System Service
    TEST = 0x0F,      // Test Service
    LOC = 0x10,       // LOC - Location / GNSS Service
    SAR = 0x11,       // SAR - Specific Absorption Rate
    IMS = 0x12,       // IMS - IP Multimedia Subsystem Settings
    ADC = 0x13,       // ADC - Analog to Digital Converter
    CSD = 0x14,       // CSD - Core Sound Driver
    MFS = 0x15,       // MFS - Modem Embedded File System
    TIME = 0x16,      // TIME - Time Service
    TS = 0x17,        // TS - Thermal Sensors
    TMD = 0x18,       // TMD - Thermal Mitigation Device
    SAP = 0x19,       // SAP - Service Access Proxy
    WDA = 0x1A,       // WDA - Wireless Data Admin
    WDSIPv6 = 0x1B,   // WDS for IPv6 (internal use)
    TSYNC = 0x1B,     // TSYNC Control Service
    RFSA = 0x1C,      // Remote File System Access
    CSVT = 0x1D,      // Circuit Switched Videotelephony
    QCMAP = 0x1E,     // Qualcomm Mobile Access Point
    IMSP = 0x1F,      // IMSP - IMS Presence Service
    IMSVT = 0x20,     // IMS Video Telephony
    IMSA = 0x21,      // IMSA - IMS Application Service
    COEX = 0x22,      // COEX - Coexistence
    PDC = 0x24,       // PDC - Persistent Device Configuration (MBN)
    STX = 0x26,       // Simultaneous Transmit
    BIT = 0x27,       // Bearer Independent Transport
    IMSRTP = 0x28,    // IMS RTP Service
    RFRPE = 0x29,     // RF Radiated Performance Enhancement
    DSD = 0x2A,       // DSD - Data System Determination
    SSCTL = 0x2B,     // Subsystem Control
    DPM = 0x2F,       // DPM - Data Port Mapper
    UIM_RMT = 0x32,   // UIM Remote Transport
    UIM_HTTP = 0x47,  // UIM HTTP Service
    CAT = 0xE0,       // Card Application Toolkit v1
    RMS = 0xE1,       // Remote Management Service
    OMA = 0xE2,       // OMA - Open Mobile Alliance Device Management
    FOX = 0xE3,       // Foxconn General Modem Service
    FOTA = 0xE6,      // Firmware Over The Air
    GMS = 0xE7,       // General Modem Service
    GAS = 0xE8,       // General Application Service
    ATR = 0xED,       // AT Relay Service
    SSC = 0x190,      // Snapdragon Sensor Core
    IMSDCM = 0x302    // IMS Data Channel Manager
}
