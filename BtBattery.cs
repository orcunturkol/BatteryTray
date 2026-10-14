using System.Runtime.InteropServices;
using System.Text;

namespace BatteryTray;

/// Battery level (percent) Windows keeps for paired Bluetooth LE devices (DEVPKEY_Device_BatteryLevel, the value the
/// Settings app shows). Used for Xbox controllers over Bluetooth: XInput reports them as "disconnected" and
/// Windows.Gaming.Input gives a constant placeholder, but this property follows the real battery.
internal static class BtBattery
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid Fmtid; public uint Pid; }

    private const uint CmGetIdListFilterEnumerator = 1, CmGetIdListFilterPresent = 0x100;
    private static readonly DevPropKey BatteryLevel = new() { Fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };
    private static readonly DevPropKey FriendlyName = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    /// Levels (0-100) of the present Bluetooth LE devices whose name contains `nameContains`, in a stable order.
    public static List<int> Levels(string nameContains)
    {
        var result = new List<int>();
        try
        {
            const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
            if (CM_Get_Device_ID_List_SizeW(out uint len, "BTHLE", flags) != 0 || len < 2) return result;
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("BTHLE", buf, len, flags) != 0) return result;

            foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).Order())
            {
                if (CM_Locate_DevNodeW(out uint inst, id, 0) != 0) continue;
                if (Read(inst, FriendlyName) is not { } nameBytes ||
                    !Encoding.Unicode.GetString(nameBytes).Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
                if (Read(inst, BatteryLevel) is { Length: > 0 } lvl && lvl[0] <= 100) result.Add(lvl[0]);
            }
        }
        catch (Exception ex) { Log.Debug("[BT battery] " + ex.Message); }
        return result;
    }

    private static byte[]? Read(uint inst, DevPropKey key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);   // asks for the size
        if (size == 0) return null;
        var data = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out _, data, ref size, 0) == 0 ? data : null;
    }
}
