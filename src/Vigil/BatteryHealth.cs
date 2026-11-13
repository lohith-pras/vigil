using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vigil;

/// <summary>Battery wear: capacities in mWh as the battery reports them, cycles 0 when not reported.</summary>
record BatteryHealth(uint Designed, uint FullCharge, uint Cycles)
{
    public double Fraction => (double)FullCharge / Designed;

    public string Text => $"Health {Fraction * 100:0}%" + (Cycles > 0 ? $" · {Cycles} cycles" : "");

    /// <summary>
    /// Asks the battery driver directly (IOCTL_BATTERY_QUERY_INFORMATION), same data as `powercfg /batteryreport`.
    /// No admin, no WMI. Null when there's no battery or it doesn't report real capacities.
    /// ponytail: first battery that answers; a laptop with two batteries shows only one.
    /// </summary>
    public static BatteryHealth? Read()
    {
        var cls = GUID_DEVCLASS_BATTERY;
        IntPtr set = SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; i < 8 && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, i, ref iface); i++)
            {
                string? path = InterfacePath(set, ref iface);
                if (path is null) continue;
                var h = Query(path);
                if (h is not null) return h;
            }
            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    static string? InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0 || size > 4096) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize + WCHAR path[]; cbSize is 8 on x64 (padding), 6 on x86.
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    static BatteryHealth? Query(string path)
    {
        using SafeFileHandle h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                             IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        uint wait = 0, tag = 0;
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out tag, 4, out _, IntPtr.Zero) || tag == 0)
            return null; // 0 = no battery in this slot

        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 /* BatteryInformation */ };
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(),
                             out BATTERY_INFORMATION info, Marshal.SizeOf<BATTERY_INFORMATION>(), out _, IntPtr.Zero))
            return null;
        return FromInfo(info.Capabilities, info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount);
    }

    /// <summary>Validation split out so the self-test can feed it fake driver values.</summary>
    internal static BatteryHealth? FromInfo(uint capabilities, uint designed, uint full, uint cycles)
    {
        const uint BATTERY_CAPACITY_RELATIVE = 0x40000000, UNKNOWN = 0xFFFFFFFF;
        // Relative = the driver reports percent, not mWh: the ratio would always read 100 %.
        if ((capabilities & BATTERY_CAPACITY_RELATIVE) != 0) return null;
        if (designed is 0 or UNKNOWN || full is 0 or UNKNOWN) return null;
        return new BatteryHealth(designed, full, cycles == UNKNOWN ? 0 : cycles);
    }

    static Guid GUID_DEVCLASS_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    // CTL_CODE(FILE_DEVICE_BATTERY 0x29, 0x10 / 0x11, METHOD_BUFFERED, FILE_READ_ACCESS)
    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1, Reserved2, Reserved3;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail,
        int size, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, int inSize, out uint output,
        int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION input, int inSize,
        out BATTERY_INFORMATION output, int outSize, out int returned, IntPtr overlapped);
}
