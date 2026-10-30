using UsbLock;
using System.Security.Cryptography;
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
var now = DateTimeOffset.UtcNow;
var g = new GuardState();
Check(!g.Arm(null), "Cannot arm without a valid key");
Check(!g.Tick(false, now, 0), "Disarmed guard never requests a lock");
Check(g.Arm("A") && g.ActiveKey == "A", "Arming remembers the exact key");
Check(!g.Tick(true, now, 10) && g.Deadline is null, "Connected key does not start countdown");
Check(!g.Tick(false, now, 10) && g.Deadline == now.AddSeconds(10), "Removal starts delay");
Check(!g.Tick(false, now.AddSeconds(9), 10), "No early lock");
Check(!g.Tick(true, now.AddSeconds(9), 10) && g.Deadline is null, "Reinsertion cancels delay");
Check(!g.Tick(false, now.AddSeconds(20), 10), "Second removal gets a new delay");
Check(g.Tick(false, now.AddSeconds(30), 10), "Deadline requests native lock");
Check(!g.Tick(false, now.AddSeconds(31), 10), "No duplicate lock while waiting for session event");
g.SessionUnlocked();
Check(!g.Tick(false, now.AddSeconds(40), 10) && g.Deadline.HasValue, "Unlock without USB starts protection again");
g.Disarm();
Check(g.Deadline is null && !g.Tick(false, now.AddHours(1), 0), "Cancel clears countdown and disarms");
g.Arm("A"); Check(g.Tick(false, now, 0), "Zero delay locks at detection");
g.LockFailed(); Check(g.Tick(false, now.AddSeconds(1), 0), "Failed native request can retry");
var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var trust = new TrustedKey { Id = "A", Hash = KeyValidation.Hash(secret), Serial = "1234" };
var file = new KeyFile(1, "A", secret);
Check(KeyValidation.Matches(file, trust, "1234"), "Valid bound key accepted");
Check(!KeyValidation.Matches(file, trust, "9999"), "Copied key on wrong volume rejected");
Check(!KeyValidation.Matches(file with { Id = "B" }, trust, "1234"), "Different key identity rejected");
Check(!KeyValidation.Matches(file with { Secret = new string('0',64) }, trust, "1234"), "Modified key rejected");
Check(!KeyValidation.Matches(file with { Version = 2 }, trust, "1234"), "Unsupported format rejected");
Check(!KeyValidation.Matches(file with { Secret = null! }, trust, "1234"), "Malformed secret rejected");
trust.Serial = null;
Check(KeyValidation.Matches(file, trust, "9999"), "Unbound key works across volumes");
trust.Hash = "invalid";
Check(!KeyValidation.Matches(file, trust, "1234"), "Malformed local hash fails closed");
foreach (var delay in new[] { -1, 3601 })
{
 var rejected = false; try { new Preferences { DelaySeconds = delay }.Validate(); } catch (InvalidDataException) { rejected = true; }
 Check(rejected, "Invalid delay rejected: " + delay);
}
new Preferences { DelaySeconds = 0 }.Validate(); new Preferences { DelaySeconds = 3600 }.Validate();
Check(true, "Delay boundaries accepted");
var invalidTheme = false; try { new Preferences { Theme = "unknown" }.Validate(); } catch (InvalidDataException) { invalidTheme = true; }
Check(invalidTheme, "Unknown imported theme rejected");

const string cameraId = @"USB\VID_046D&PID_0825\CAMERA123";
const string otherId = @"USB\VID_046D&PID_0825\CAMERA456";
var camera = new TrustedKey { Id = "camera", Name = "Webcam", Kind = ProtectionSourceKind.UsbDevice, DeviceInstanceId = cameraId };
Check(PeripheralIdentity.IsPhysicalUsb(cameraId), "Physical USB identity accepted");
Check(!PeripheralIdentity.IsPhysicalUsb(@"USB\VID_046D&PID_0825&MI_00\child"), "Composite interface not offered as a second physical device");
Check(!PeripheralIdentity.IsPhysicalUsb(@"USB\ROOT_HUB30\123"), "Root hub excluded");
Check(!PeripheralIdentity.IsPhysicalUsb(@"PCI\VID_046D&PID_0825\123"), "Non-USB bus excluded");
Check(!PeripheralIdentity.IsPhysicalUsb(null) && !PeripheralIdentity.IsPhysicalUsb(@"USB\VID_046D&PID_0825\"), "Missing USB identity rejected");
Check(PeripheralIdentity.Matches(camera, cameraId.ToLowerInvariant()), "Windows identity matching is case insensitive");
Check(!PeripheralIdentity.Matches(camera, otherId), "Same webcam model with different instance cannot substitute");
Check(!PeripheralIdentity.Matches(trust, cameraId), "File trust cannot act as a peripheral trust");
Check(!KeyValidation.Matches(file with { Id = camera.Id }, camera, "1234"), "A key file cannot impersonate peripheral trust");
var trusted = new List<TrustedKey> { trust, camera };
var connected = new HashSet<string> { trust.Id };
Check(PeripheralIdentity.SelectForArming(trusted, connected, camera) is null, "Selected absent camera never falls back to another connected key");
connected.Add(camera.Id);
Check(PeripheralIdentity.SelectForArming(trusted, connected, camera)?.Id == camera.Id, "Selected connected camera is armed explicitly");
Check(PeripheralIdentity.SelectForArming([trust], connected, camera) is null, "Revoked selection cannot arm from a stale snapshot");
Check(PeripheralIdentity.SelectForArming(trusted, connected, null)?.Id == trust.Id, "Automatic selection still chooses a connected trusted source");
var legacy = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{\"Keys\":[{\"Id\":\"old\",\"Hash\":\"hash\",\"Name\":\"Existing USB\"}]}")!;
Check(legacy.Keys.Single().Kind == ProtectionSourceKind.FileKey, "Existing v0.1 settings default to file key without migration");
var roundtrip = System.Text.Json.JsonSerializer.Deserialize<TrustedKey>(System.Text.Json.JsonSerializer.Serialize(camera))!;
Check(PeripheralIdentity.Matches(roundtrip, cameraId), "Peripheral trust identity survives settings serialization");
var deviceGuard = new GuardState(); deviceGuard.Arm(camera.Id);
Check(!deviceGuard.Tick(false, now, 10) && deviceGuard.Deadline.HasValue, "Peripheral removal starts shared countdown");
Check(!deviceGuard.Tick(PeripheralIdentity.Matches(camera, otherId), now.AddSeconds(2), 10) && deviceGuard.Deadline.HasValue, "Another physical instance cannot cancel camera countdown");
Check(!deviceGuard.Tick(PeripheralIdentity.Matches(camera, cameraId), now.AddSeconds(3), 10) && deviceGuard.Deadline is null, "Same camera returning cancels pending lock");
Console.WriteLine($"{count} checks passed.");
