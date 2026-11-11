using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Vigil;

record DeviceBattery(string Name, int Percent);

/// <summary>
/// Battery levels of Bluetooth devices, read from the same device property Windows Settings shows
/// ("Bluetooth & devices"). No admin, no driver, no network. Only devices that report a level to
/// Windows appear; the value is whatever Windows last heard, so it can lag a little.
/// </summary>
static class BluetoothBatteries
{
    // {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2 = Bluetooth battery level (byte, 0-100)
    static DEVPROPKEY BatteryKey = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    static DEVPROPKEY FriendlyNameKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    static DEVPROPKEY DescriptionKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };

    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    const uint DEVPROP_TYPE_BYTE = 0x3, DEVPROP_TYPE_STRING = 0x12;

    public static IReadOnlyList<DeviceBattery> Read()
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1)) return Array.Empty<DeviceBattery>();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var one = new byte[1];
            // ponytail: walks every present device (a few hundred) once a minute; a few ms. Filter by
            // enumerator (BTHENUM/BTHLE) if it ever shows up in a profile.
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDevicePropertyW(set, ref data, ref BatteryKey, out uint type, one, 1, out _, 0)
                    || type != DEVPROP_TYPE_BYTE || one[0] > 100)
                    continue;

                string? name = ReadString(set, ref data, ref FriendlyNameKey) ?? ReadString(set, ref data, ref DescriptionKey);
                if (string.IsNullOrWhiteSpace(name)) continue;
                // One device often has several nodes (e.g. headset + hands-free); keep one row per name.
                found.TryAdd(name.Trim(), one[0]);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        var list = new List<DeviceBattery>();
        foreach (var (name, pct) in found) list.Add(new DeviceBattery(name, pct));
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    static string? ReadString(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref data, ref key, out _, null, 0, out uint size, 0);
        if (size == 0 || size > 4096) return null;
        var buf = new byte[size];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out uint type, buf, size, out _, 0) || type != DEVPROP_TYPE_STRING)
            return null;
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint type, byte[]? buffer, uint size, out uint required, uint flags);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
