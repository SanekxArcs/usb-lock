using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace UsbLock;
public enum ProtectionSourceKind { FileKey, UsbDevice }
public sealed class TrustedKey
{
 public string Id { get; set; } = Guid.NewGuid().ToString("N");
 public string Name { get; set; } = "USB key";
 public ProtectionSourceKind Kind { get; set; }
 public string? DeviceInstanceId { get; set; }
 public string Hash { get; set; } = "";
 public string? Serial { get; set; }
 public DateTime Created { get; set; } = DateTime.Now;
 public override string ToString() => $"{Name}  ·  {(Kind == ProtectionSourceKind.UsbDevice ? "USB-пристрій" : Serial is null ? "Файл ключа" : "Файл ключа · прив’язаний до тому")}";
}
public sealed class Preferences
{
 public int DelaySeconds { get; set; } = 10;
 public bool AllowCancel { get; set; } = true;
 public bool Notifications { get; set; } = true;
 public bool Sound { get; set; }
 public bool StartMinimized { get; set; }
 public bool AutoArm { get; set; }
 public string Theme { get; set; } = "System";
 public void Validate()
 {
  if (DelaySeconds is < 0 or > 3600) throw new InvalidDataException("Затримка має бути від 0 до 3600 секунд.");
  if (Theme is not ("System" or "Dark" or "Light")) throw new InvalidDataException("Невідома тема.");
 }
}
public sealed class Configuration
{
 public Preferences Preferences { get; set; } = new();
 public List<TrustedKey> Keys { get; set; } = [];
}
public sealed record KeyFile(int Version, string Id, string Secret);
public static class KeyValidation
{
 public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
 public static bool Matches(KeyFile file, TrustedKey trust, string serial)
 {
  if (trust.Kind != ProtectionSourceKind.FileKey || file.Version != 1 || file.Id != trust.Id || file.Secret is null || file.Secret.Length != 64) return false;
  if (trust.Serial is not null && trust.Serial != serial) return false;
  try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(trust.Hash), Convert.FromHexString(Hash(file.Secret))); }
  catch (FormatException) { return false; }
 }
}
public sealed class GuardState
{
 public bool Armed { get; private set; }
 public DateTimeOffset? Deadline { get; private set; }
 public bool LockRequested { get; private set; }
 public string? ActiveKey { get; private set; }
 public bool Arm(string? key)
 {
  if (key is null) return false;
  Armed = true; ActiveKey = key; Deadline = null; LockRequested = false; return true;
 }
 public void Disarm() { Armed = false; Deadline = null; LockRequested = false; ActiveKey = null; }
 public void SessionUnlocked() { LockRequested = false; Deadline = null; }
 public bool Tick(bool present, DateTimeOffset now, int delay)
 {
  if (!Armed) return false;
  if (present) { Deadline = null; LockRequested = false; return false; }
  if (LockRequested) return false;
  Deadline ??= now.AddSeconds(delay);
  if (now < Deadline) return false;
  Deadline = null; LockRequested = true; return true;
 }
 public void LockFailed() { LockRequested = false; Deadline = null; }
}

public sealed record UsbPeripheral(string InstanceId, string Name, string DeviceClass, string Location, bool UniqueId)
{
 public string DisplayName => $"{Name} · {Location}";
 public override string ToString() => DisplayName;
}
public static class PeripheralIdentity
{
 public static bool IsPhysicalUsb(string? id)
 {
  if (id is null) return false;
  var parts = id.Split('\\');
  return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
   && System.Text.RegularExpressions.Regex.IsMatch(parts[1], @"^VID_[0-9A-F]{4}&PID_[0-9A-F]{4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
   && !string.IsNullOrWhiteSpace(parts[2]);
 }
 public static bool Matches(TrustedKey trust, string presentId) => trust.Kind == ProtectionSourceKind.UsbDevice
  && IsPhysicalUsb(trust.DeviceInstanceId) && string.Equals(trust.DeviceInstanceId, presentId, StringComparison.OrdinalIgnoreCase);
 public static TrustedKey? SelectForArming(IReadOnlyList<TrustedKey> trusted, ISet<string> present, TrustedKey? selected)
  => selected is not null ? trusted.FirstOrDefault(k => k.Id == selected.Id && present.Contains(k.Id)) : trusted.FirstOrDefault(k => present.Contains(k.Id));
}
