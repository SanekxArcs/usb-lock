using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace UsbLock;
public sealed class Storage
{
 public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USB Lock");
 private readonly string _path;
 public Storage(string? folder = null) => _path = Path.Combine(folder ?? Folder, "settings.dat");
 public Configuration Load()
 {
  if (!File.Exists(_path)) return new();
  var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
  var c = JsonSerializer.Deserialize<Configuration>(bytes) ?? throw new InvalidDataException("Порожні налаштування.");
  if (c.Preferences is null || c.Keys is null || c.Keys.Any(k => k is null || k.Id is null || k.Hash is null || !Enum.IsDefined(k.Kind) || k.Kind == ProtectionSourceKind.UsbDevice && !PeripheralIdentity.IsPhysicalUsb(k.DeviceInstanceId))) throw new InvalidDataException("Пошкоджені налаштування.");
  c.Preferences.Validate(); return c;
 }
 public void Save(Configuration c)
 {
  c.Preferences.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
  var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(c), null, DataProtectionScope.CurrentUser);
  File.WriteAllBytes(_path + ".tmp", bytes); File.Move(_path + ".tmp", _path, true);
 }
 public static void Log(string text)
 {
  try
  {
   Directory.CreateDirectory(Folder); var path = Path.Combine(Folder, "activity.log");
   if (File.Exists(path) && new FileInfo(path).Length > 512000) File.Move(path, path + ".previous", true);
   File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
  }
  catch (IOException) { } catch (UnauthorizedAccessException) { }
 }
}
public sealed record UsbDrive(string Root, string Label, string Serial, long FreeBytes)
{
 public override string ToString() => $"{Root}  {Label}  ·  {FreeBytes / 1073741824d:0.0} ГБ вільно";
}
public sealed record ScanResult(List<UsbDrive> Drives, HashSet<string> PresentKeys, List<string> Errors, List<UsbPeripheral>? Devices = null);
public static class UsbKeys
{
 public const string FileName = ".usb-lock.key";
 public static ScanResult Scan(IReadOnlyList<TrustedKey> keys)
 {
  var drives = new List<UsbDrive>(); var present = new HashSet<string>(); var errors = new List<string>();
  foreach (var drive in DriveInfo.GetDrives())
  {
   try
   {
    if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
    if (!Native.GetVolumeInformation(drive.Name, null, 0, out var serial, out _, out _, null, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    var usb = new UsbDrive(drive.Name, drive.VolumeLabel, serial.ToString("X8"), drive.AvailableFreeSpace); drives.Add(usb);
    var path = Path.Combine(drive.Name, FileName); if (!File.Exists(path)) continue;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var bytes = new byte[4097]; var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
    if (length > 4096) { errors.Add($"{drive.Name}: завеликий файл ключа"); continue; }
    var file = JsonSerializer.Deserialize<KeyFile>(bytes.AsSpan(0, length)); if (file is null) continue;
    foreach (var key in keys) if (KeyValidation.Matches(file, key, usb.Serial)) present.Add(key.Id);
   }
   catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
   { errors.Add($"{drive.Name}: {e.Message}"); }
  }
  return new(drives, present, errors);
 }
 public static TrustedKey Create(UsbDrive drive, bool bind, string name)
 {
  var fresh = Scan([]).Drives.FirstOrDefault(d => d.Root == drive.Root && d.Serial == drive.Serial) ?? throw new IOException("Накопичувач від’єднано або замінено.");
  var key = new TrustedKey { Name = string.IsNullOrWhiteSpace(name) ? $"USB {fresh.Label}" : name.Trim(), Serial = bind ? fresh.Serial : null };
  var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); key.Hash = KeyValidation.Hash(secret);
  using var stream = new FileStream(Path.Combine(fresh.Root, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
  JsonSerializer.Serialize(stream, new KeyFile(1, key.Id, secret)); stream.Flush(true); return key;
 }
}
internal static class Native
{
 [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool LockWorkStation();
 [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
 public static extern bool GetVolumeInformation(string root, StringBuilder? name, uint size, out uint serial, out uint max, out uint flags, StringBuilder? fs, uint fsSize);
}


