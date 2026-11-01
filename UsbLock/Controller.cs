using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Forms = System.Windows.Forms;

namespace UsbLock;

// Owns protection state, the tray icon and device events for the whole process lifetime.
// The settings window is created on demand and destroyed when hidden, so while the app sits
// in the tray it keeps no WPF visual tree and never initialises a GPU render device.
public sealed class ProtectionController
{
 public Configuration Config { get; }
 public GuardState Guard { get; } = new();
 public ScanResult Scan { get; private set; } = new([], [], []);
 public bool AutoArmPending { get; private set; }
 public bool SessionLocked { get; private set; }
 public bool Busy => _arming || _creating;
 public TrustedKey? SelectedKey { get; set; }
 public MainWindow? Window => _window;
 public event Action? Changed;
 public event Action<string, string, bool>? Feedback;

 private readonly Storage _storage = new();
 private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
 // Scans are driven by device events; the timer is a safety net plus countdown ticks (see NextInterval).
 private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
 private readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
 private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(3) };
 private readonly Forms.NotifyIcon _tray;
 private readonly Forms.ToolStripMenuItem _trayToggle, _trayStatus;
 private readonly DeviceEventWindow _events;
 private MainWindow? _window;
 private bool _scanning, _rescanPending, _exiting, _arming, _creating;
 private DateTimeOffset _lastNativeLockRequest = DateTimeOffset.MinValue;

 public ProtectionController(Configuration config)
 {
  Config = config;
  var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Segoe UI", 9) };
  _trayStatus = new Forms.ToolStripMenuItem("Захист вимкнено") { Enabled = false };
  _trayToggle = new Forms.ToolStripMenuItem("Увімкнути захист", null, (_, _) => _ = ToggleArmAsync());
  menu.Items.Add(_trayStatus); menu.Items.Add(new Forms.ToolStripSeparator());
  menu.Items.Add("Відкрити USB Lock", null, (_, _) => OpenWindow()); menu.Items.Add(_trayToggle);
  menu.Items.Add("Заблокувати Windows", null, (_, _) => LockWindows());
  menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Вийти", null, (_, _) => Exit());
  using (var icon = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/UsbLock;component/Assets/app.ico")).Stream)
   _tray = new Forms.NotifyIcon { Text = "USB Lock — захист вимкнено", Icon = new System.Drawing.Icon(icon), ContextMenuStrip = menu, Visible = true };
  _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenWindow(); };
  _events = new DeviceEventWindow(() => { UsbKeys.ForgetEmptyDrives(); RequestScan(); });
  SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
  AutoArmPending = config.Preferences.AutoArm;
  _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); _ = ScanAsync(); };
  _trim.Tick += (_, _) => { _trim.Stop(); TrimMemory(); };
  _timer.Tick += async (_, _) => await ScanAsync(); _timer.Start();
  Storage.Log("Програму запущено. Захист вимкнено до перевірки ключа.");
  _ = ScanAsync();
  _trim.Start(); // drop startup allocations once the first scan has settled (cancelled if the window opens)
 }

 // ---- state shown in tray and window ----
 public string ActiveNoun => Config.Keys.FirstOrDefault(k => k.Id == Guard.ActiveKey)?.Kind == ProtectionSourceKind.UsbDevice ? "Пристрій" : "Ключ";
 public string StatusTitle => SessionLocked ? "Сеанс Windows заблоковано" : Guard.LockRequested ? "Надіслано запит блокування" : Guard.Deadline.HasValue ? $"{ActiveNoun} від’єднано" : Guard.Armed ? "Захист увімкнено" : "Захист вимкнено";
 public string StatusDetail
 {
  get
  {
   var present = Guard.ActiveKey is not null && Scan.PresentKeys.Contains(Guard.ActiveKey);
   return Guard.Armed ? $"{Config.Keys.FirstOrDefault(k => k.Id == Guard.ActiveKey)?.Name} · {(present ? "під’єднано" : "очікуємо повернення")}"
    : AutoArmPending ? "Очікуємо довірений ключ або пристрій для автоматичного ввімкнення."
    : SelectedKey is TrustedKey selected ? $"Вибрано: {selected.Name} · {(Scan.PresentKeys.Contains(selected.Id) ? "під’єднано" : "не під’єднано")}"
    : "Виберіть під’єднане довірене джерело та ввімкніть захист.";
  }
 }
 public bool CanToggle => !Busy && (Guard.Armed || PeripheralIdentity.SelectForArming(Config.Keys, Scan.PresentKeys, SelectedKey) is not null);

 public void Notify()
 {
  if (_exiting) return;
  var title = StatusTitle;
  _trayStatus.Text = title; _trayToggle.Text = Guard.Armed ? "Вимкнути захист" : "Увімкнути захист"; _trayToggle.Enabled = CanToggle;
  _tray.Text = "USB Lock — " + title.ToLowerInvariant();
  var interval = NextInterval(); if (_timer.Interval != interval) _timer.Interval = interval;
  Changed?.Invoke();
 }
 private TimeSpan NextInterval() =>
  SessionLocked ? TimeSpan.FromSeconds(30) // unlock triggers its own scan
  : Guard.Deadline.HasValue || Guard.LockRequested ? TimeSpan.FromMilliseconds(500) // countdown / lock confirmation
  : Guard.Armed || AutoArmPending || _window is not null ? TimeSpan.FromSeconds(3) // safety net if a device event is missed
  : TimeSpan.FromSeconds(30);

 // ---- scanning ----
 // One plug/unplug fires a burst of messages; coalesce them into a single scan.
 public void RequestScan() { if (_exiting) return; _deviceDebounce.Stop(); _deviceDebounce.Start(); }
 public Task RescanNowAsync() { UsbKeys.ForgetEmptyDrives(); return ScanAsync(); }
 public async Task ScanAsync()
 {
  if (_exiting) return;
  if (_scanning) { _rescanPending = true; return; }
  _scanning = true;
  try
  {
   var keys = Config.Keys.ToList(); var withDeviceList = _window is not null;
   var next = await Task.Run(() => ProtectionSources.Scan(keys, includeDeviceList: withDeviceList)); if (_exiting) return;
   if (!keys.Select(k => k.Id).SequenceEqual(Config.Keys.Select(k => k.Id))) return; // Ignore scans started before trust changed.
   // Keep the last device list while hidden so reopening the window shows something until its own scan lands.
   Scan = next.Devices is null && Scan.Devices is not null ? next with { Devices = Scan.Devices } : next;
   if (AutoArmPending && !Guard.Armed && !Busy)
   {
    var present = Config.Keys.FirstOrDefault(k => next.PresentKeys.Contains(k.Id));
    if (present is not null) { Guard.Arm(present.Id); AutoArmPending = false; Event("Захист увімкнено автоматично."); }
   }
   var wasPending = Guard.Deadline.HasValue;
   if (!SessionLocked)
   {
    if (Guard.LockRequested && DateTimeOffset.UtcNow - _lastNativeLockRequest > TimeSpan.FromSeconds(5)) { Guard.LockFailed(); Storage.Log("Підтвердження блокування не отримано; повторна перевірка."); }
    var shouldLock = Guard.Tick(Guard.ActiveKey is not null && next.PresentKeys.Contains(Guard.ActiveKey), DateTimeOffset.UtcNow, Config.Preferences.DelaySeconds);
    if (shouldLock) LockWindows();
    else if (!wasPending && Guard.Deadline.HasValue)
    {
     Event($"{ActiveNoun} від’єднано. Блокування через {Config.Preferences.DelaySeconds} с.");
     OpenWindow(activate: false);
    }
    else if (wasPending && !Guard.Deadline.HasValue && !Guard.LockRequested) Event($"{ActiveNoun} повернуто. Відлік скасовано.");
   }
   Notify();
  }
  catch (Exception error)
  {
   Storage.Log("Помилка перевірки USB: " + error.Message);
   // An unexpected enumeration failure still counts as missing key while armed.
   if (!SessionLocked && Guard.Tick(false, DateTimeOffset.UtcNow, Config.Preferences.DelaySeconds)) LockWindows();
   Feedback?.Invoke("Помилка перевірки USB", error.Message, true); Notify();
  }
  finally
  {
   _scanning = false;
   if (_rescanPending) { _rescanPending = false; RequestScan(); } // a device event arrived mid-scan
  }
 }

 // ---- actions ----
 public async Task ToggleArmAsync()
 {
  if (Guard.Armed) { Guard.Disarm(); AutoArmPending = false; Event("Захист вимкнено."); Notify(); return; }
  if (Busy) return;
  _arming = true; Notify();
  try { var trusted = Config.Keys.ToList(); Scan = await Task.Run(() => ProtectionSources.Scan(trusted)); }
  catch (Exception error) { Feedback?.Invoke("Помилка перевірки ключа", error.Message, true); return; }
  finally { _arming = false; Notify(); }
  var key = PeripheralIdentity.SelectForArming(Config.Keys, Scan.PresentKeys, SelectedKey);
  if (!Guard.Arm(key?.Id)) Feedback?.Invoke("Джерело не під’єднано", "Під’єднайте вибраний ключ або пристрій. Інше джерело не використовується замість нього.", true);
  else { AutoArmPending = false; Event("Захист увімкнено: " + key!.Name); }
  Notify();
 }
 public void CancelCountdown()
 {
  if (!Config.Preferences.AllowCancel) return;
  Guard.Disarm(); AutoArmPending = false; Event("Відлік скасовано. Захист вимкнено."); Notify();
 }
 public async Task CreateKeyAsync(UsbDrive drive, bool bind, string name)
 {
  if (Guard.Armed || Busy) return;
  _creating = true; Notify();
  try
  {
   _storage.Save(Config); // Verify DPAPI and local persistence before touching the USB drive.
   var key = await Task.Run(() => UsbKeys.Create(drive, bind, name));
   Config.Keys.Add(key);
   try { _storage.Save(Config); }
   catch { Config.Keys.Remove(key); throw new IOException("Ключ записано на USB, але довіру не вдалося зберегти. Видаліть .usb-lock.key із флешки та повторіть створення."); }
   SelectedKey = key;
   Event("Створено ключ: " + key.Name); Feedback?.Invoke("Ключ готовий", "Тепер можна ввімкнути захист.", false); await ScanAsync();
  }
  catch (Exception error) { Feedback?.Invoke("Не вдалося створити ключ", error is IOException && File.Exists(Path.Combine(drive.Root, UsbKeys.FileName)) ? error.Message + "\nІснуючі файли ключа не перезаписуються." : error.Message, true); }
  finally { _creating = false; Notify(); }
 }
 public async Task TrustDeviceAsync(UsbPeripheral device, string name)
 {
  if (Guard.Armed || Busy) return;
  _creating = true; Notify();
  try
  {
   var devices = await Task.Run(UsbPeripherals.Scan);
   var fresh = devices.FirstOrDefault(d => d.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase))
    ?? throw new IOException("Пристрій уже від’єднано. Під’єднайте його й повторіть.");
   if (Config.Keys.Any(k => PeripheralIdentity.Matches(k, fresh.InstanceId))) throw new IOException("Цей пристрій уже є у списку довірених.");
   var key = new TrustedKey { Kind = ProtectionSourceKind.UsbDevice, DeviceInstanceId = fresh.InstanceId, Name = string.IsNullOrWhiteSpace(name) ? fresh.Name : name.Trim() };
   Config.Keys.Add(key);
   try { _storage.Save(Config); } catch { Config.Keys.Remove(key); throw; }
   SelectedKey = key;
   Event("Додано USB-пристрій: " + key.Name); Feedback?.Invoke("Пристрій додано", "Виберіть його у списку довірених і ввімкніть захист.", false);
   await ScanAsync();
  }
  catch (Exception error) { Feedback?.Invoke("Не вдалося додати пристрій", error.Message, true); }
  finally { _creating = false; Notify(); }
 }
 public bool Revoke(TrustedKey key)
 {
  Config.Keys.Remove(key);
  try { _storage.Save(Config); }
  catch (Exception error) { Config.Keys.Add(key); Feedback?.Invoke("Не вдалося зберегти", error.Message, true); return false; }
  if (Guard.ActiveKey == key.Id) { Guard.Disarm(); AutoArmPending = false; }
  if (SelectedKey?.Id == key.Id) SelectedKey = null;
  Event("Джерело відкликано: " + key.Name); Notify(); return true;
 }
 public void ApplyPreferences(Preferences p)
 {
  p.Validate();
  var old = Config.Preferences; Config.Preferences = p;
  try { _storage.Save(Config); } catch { Config.Preferences = old; throw; }
  AutoArmPending = p.AutoArm && !Guard.Armed; Notify();
 }
 public void LockWindows()
 {
  if (SessionLocked) return;
  _lastNativeLockRequest = DateTimeOffset.UtcNow;
  if (Config.Preferences.Sound) System.Media.SystemSounds.Exclamation.Play();
  if (!Native.LockWorkStation())
  {
   Guard.LockFailed(); Feedback?.Invoke("Windows не прийняла запит", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message, true); Storage.Log("Помилка запиту блокування Windows.");
  }
  else Event("Надіслано запит блокування Windows.");
 }
 private void Event(string message)
 {
  Storage.Log(message);
  if (Config.Preferences.Notifications) _tray.ShowBalloonTip(3500, "USB Lock", message, Forms.ToolTipIcon.Info);
 }

 // ---- window lifetime ----
 public void OpenWindow(bool activate = true)
 {
  if (_exiting) return;
  _trim.Stop();
  if (_window is null)
  {
   var window = new MainWindow(this);
   window.Closed += (_, _) => { if (_window == window) _window = null; Notify(); _trim.Start(); };
   _window = window; System.Windows.Application.Current.MainWindow = window;
  }
  _window.Show();
  if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
  if (activate) _window.Activate();
  RequestScan(); // fill the device list, which is skipped while hidden
  Notify();
 }
 // Called a few seconds after the window closes (or after startup): give back the window's memory.
 private void TrimMemory()
 {
  if (_window is not null || _exiting) return;
  GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
  GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
  GC.WaitForPendingFinalizers(); GC.Collect();
  Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), -1, -1);
 }

 private void SessionChanged(object? sender, SessionSwitchEventArgs e) => _dispatcher.BeginInvoke(() =>
 {
  if (e.Reason == SessionSwitchReason.SessionLock) { SessionLocked = true; Storage.Log("Windows підтвердила блокування сеансу."); }
  if (e.Reason == SessionSwitchReason.SessionUnlock) { SessionLocked = false; Guard.SessionUnlocked(); Storage.Log("Вхід до Windows."); _ = ScanAsync(); }
  Notify();
 });
 private void PowerChanged(object? sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(() => { _ = ScanAsync(); }); }

 public void Exit()
 {
  if (Guard.Armed && System.Windows.MessageBox.Show("Вихід припинить USB-захист. Вийти?", "USB Lock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  _exiting = true; _timer.Stop(); _deviceDebounce.Stop(); _trim.Stop(); Guard.Disarm();
  SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
  _events.Dispose();
  _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); Storage.Log("Програму закрито. Захист вимкнено.");
  _window?.Close(); System.Windows.Application.Current.Shutdown();
 }
}

// Hidden top-level Win32 window (no WPF, no rendering) that receives WM_DEVICECHANGE.
// Must not be a message-only window: those miss the DBT_DEVNODES_CHANGED / volume broadcasts.
internal sealed class DeviceEventWindow : Forms.NativeWindow, IDisposable
{
 private readonly Action _changed;
 private IntPtr _notification;
 public DeviceEventWindow(Action changed)
 {
  _changed = changed;
  CreateHandle(new Forms.CreateParams { Caption = "USB Lock device events" });
  _notification = Native.RegisterAllDeviceInterfaces(Handle);
 }
 protected override void WndProc(ref Forms.Message m)
 {
  // DBT_DEVNODES_CHANGED, DBT_DEVICEARRIVAL, DBT_DEVICEREMOVECOMPLETE
  if (m.Msg == 0x0219 && m.WParam.ToInt64() is 0x0007 or 0x8000 or 0x8004) _changed();
  base.WndProc(ref m);
 }
 public void Dispose()
 {
  if (_notification != IntPtr.Zero) { Native.UnregisterDeviceNotification(_notification); _notification = IntPtr.Zero; }
  DestroyHandle();
 }
}
