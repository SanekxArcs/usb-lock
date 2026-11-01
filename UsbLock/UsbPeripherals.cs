using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace UsbLock;

// Enumerate present USB PnP nodes without opening cameras, HID endpoints or storage.
public static class UsbPeripherals
{
 private sealed record Node(string Id, string Name, string Class, string Location, bool UniqueId, uint DevInst, uint Parent);
 public static List<UsbPeripheral> Scan()
 {
  var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04); // PRESENT | ALLCLASSES
  if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
  try
  {
   var nodes = new List<Node>();
   for (uint index = 0; ; index++)
   {
    var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    if (!SetupDiEnumDeviceInfo(set, index, ref data))
    {
     var error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new Win32Exception(error);
    }
    var id = new StringBuilder(1024);
    if (!SetupDiGetDeviceInstanceId(set, ref data, id, (uint)id.Capacity, out _)) continue;
    // A node can disappear while enumerating; only keep currently present devnodes.
    if (CM_Get_DevNode_Status(out _, out _, data.DevInst, 0) != 0) continue;
    var friendly = Property(set, ref data, 12);
    var description = Property(set, ref data, 0);
    var name = string.IsNullOrWhiteSpace(friendly) ? description : friendly;
    var location = Property(set, ref data, 13);
    var deviceClass = Property(set, ref data, 7);
    var caps = new byte[4];
    var unique = SetupDiGetDeviceRegistryProperty(set, ref data, 15, out _, caps, 4, out _) && (BitConverter.ToUInt32(caps) & 0x10) != 0;
    CM_Get_Parent(out var parent, data.DevInst, 0);
    nodes.Add(new(id.ToString(), string.IsNullOrWhiteSpace(name) ? "USB-пристрій" : name, deviceClass, location, unique, data.DevInst, parent));
   }
   return nodes.Where(n => PeripheralIdentity.IsPhysicalUsb(n.Id)).Select(n =>
   {
    // Composite webcams expose camera/microphone children; show these names once under their physical parent.
    var children = nodes.Where(child => child.Parent == n.DevInst && child.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).Select(child => child.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(name => name != n.Name).Take(3).ToArray();
    var name = children.Length == 0 ? n.Name : n.Name + " — " + string.Join(", ", children);
    return new UsbPeripheral(n.Id, name, n.Class, string.IsNullOrWhiteSpace(n.Location) ? n.Id.Split('\\')[2] : n.Location, n.UniqueId);
   }).DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Name).ThenBy(d => d.InstanceId).ToList();
  }
  finally { SetupDiDestroyDeviceInfoList(set); }
 }
 // CM_LOCATE_DEVNODE_NORMAL only finds devnodes that are currently present (case-insensitive ID).
 public static bool IsPresent(string instanceId) =>
  CM_Locate_DevNode(out var devInst, instanceId, 0) == 0 && CM_Get_DevNode_Status(out _, out _, devInst, 0) == 0;
 private static string Property(IntPtr set, ref DeviceInfo data, uint property)
 {
  var bytes = new byte[8192];
  return SetupDiGetDeviceRegistryProperty(set, ref data, property, out var type, bytes, (uint)bytes.Length, out var size) && type == 1
   ? Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(size, (uint)bytes.Length)).TrimEnd('\0') : "";
 }
 [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
 [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
 [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo data);
 [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo data, StringBuilder id, uint size, out uint required);
 [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo data, uint property, out uint type, byte[] bytes, uint size, out uint required);
 [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
 [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")] private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);
 [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
 [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}

public static class ProtectionSources
{
 // includeDeviceList=false skips the SetupAPI walk when nobody is looking at the list and no device is trusted.
 public static ScanResult Scan(IReadOnlyList<TrustedKey> keys, bool includeDeviceList = true)
 {
  ScanResult files;
  try { files = UsbKeys.Scan(keys.Where(k => k.Kind == ProtectionSourceKind.FileKey).ToList()); }
  catch (Exception e) { files = new([], [], ["Накопичувачі: " + e.Message]); }
  if (!includeDeviceList)
  {
   // Point lookup per trusted device instead of walking every USB node.
   try
   {
    foreach (var key in keys.Where(k => k.Kind == ProtectionSourceKind.UsbDevice))
     if (PeripheralIdentity.IsPhysicalUsb(key.DeviceInstanceId) && UsbPeripherals.IsPresent(key.DeviceInstanceId!)) files.PresentKeys.Add(key.Id);
   }
   catch (Exception e) { files.Errors.Add("USB-пристрої: " + e.Message); }
   return files;
  }
  List<UsbPeripheral> devices;
  try { devices = UsbPeripherals.Scan(); }
  catch (Exception e) { devices = []; files.Errors.Add("USB-пристрої: " + e.Message); }
  foreach (var key in keys.Where(k => k.Kind == ProtectionSourceKind.UsbDevice))
   if (devices.Any(d => PeripheralIdentity.Matches(key, d.InstanceId))) files.PresentKeys.Add(key.Id);
  return files with { Devices = devices };
 }
}
