using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Forms = System.Windows.Forms;

namespace UsbLock;
public partial class MainWindow : FluentWindow
{
 private readonly Configuration _config;
 private readonly Storage _storage = new();
 private readonly GuardState _guard = new();
 private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
 private readonly Forms.NotifyIcon _tray;
 private readonly Forms.ToolStripMenuItem _trayToggle;
 private readonly Forms.ToolStripMenuItem _trayStatus;
 private ScanResult _scan = new([], [], []);
 private bool _scanning, _exiting, _autoArmPending, _sessionLocked;
 private bool _ready, _arming, _creating;
 private DateTimeOffset _lastNativeLockRequest = DateTimeOffset.MinValue;
 private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
 public MainWindow(Configuration config)
 {
  _config = config; InitializeComponent(); LoadPreferences(); ApplyTheme();
  KeysBox.ItemsSource = _config.Keys.ToList();
  var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new System.Drawing.Font("Segoe UI", 9) };
  _trayStatus = new Forms.ToolStripMenuItem("Захист вимкнено") { Enabled = false };
  _trayToggle = new Forms.ToolStripMenuItem("Увімкнути захист", null, (_, _) => ToggleArm(this, new RoutedEventArgs()));
  menu.Items.Add(_trayStatus); menu.Items.Add(new Forms.ToolStripSeparator());
  menu.Items.Add("Відкрити USB Lock", null, (_, _) => OpenWindow()); menu.Items.Add(_trayToggle);
  menu.Items.Add("Заблокувати Windows", null, (_, _) => LockWindows());
  menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Вийти", null, (_, _) => ExitApp());
  _tray = new Forms.NotifyIcon { Text = "USB Lock — захист вимкнено", Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/UsbLock;component/Assets/app.ico")).Stream), ContextMenuStrip = menu, Visible = true };
  _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenWindow(); };
  Closing += OnClosing;
  SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.UserPreferenceChanged += ThemeChanged;
  _autoArmPending = config.Preferences.AutoArm;
  _timer.Tick += async (_, _) => await ScanAsync(); _ready = true; _timer.Start();
  Storage.Log("Програму запущено. Захист вимкнено до перевірки ключа.");
  _ = ScanAsync();
 }
 private void Navigate(object sender, SelectionChangedEventArgs e)
 {
  if (ProtectionPage is null) return;
  ProtectionPage.Visibility = Navigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
  SettingsPage.Visibility = Navigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
  HistoryPage.Visibility = Navigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
  if (Navigation.SelectedIndex == 2) UpdateHistory();
 }
 private async Task ScanAsync()
 {
  if (_scanning || _exiting) return; _scanning = true;
  try
  {
   var keys = _config.Keys.ToList();
   var next = await Task.Run(() => ProtectionSources.Scan(keys)); if (_exiting) return;
   if (!keys.Select(k => k.Id).SequenceEqual(_config.Keys.Select(k => k.Id))) return; // Ignore scans started before trust changed.
   _scan = next;
   var signature = string.Join("|", next.Drives.Select(d => d.ToString()));
   if ((string?)DrivesBox.Tag != signature)
   {
    var root = (DrivesBox.SelectedItem as UsbDrive)?.Root;
    DrivesBox.ItemsSource = next.Drives; DrivesBox.SelectedItem = next.Drives.FirstOrDefault(d => d.Root == root) ?? next.Drives.FirstOrDefault(); DrivesBox.Tag = signature;
   }
   UpdateDeviceChoices(next.Devices ?? []);
   DriveHint.Text = next.Errors.Count > 0 ? string.Join("\n", next.Errors) : next.Drives.Count == 0 ? "Знімних накопичувачів немає. Під’єднайте USB-флешку." : $"Знайдено накопичувачів: {next.Drives.Count}. Довірених джерел під’єднано: {next.PresentKeys.Count}.";
   if (_autoArmPending && !_guard.Armed && !_arming && !_creating)
   {
    var present = _config.Keys.FirstOrDefault(k => next.PresentKeys.Contains(k.Id));
    if (present is not null) { _guard.Arm(present.Id); _autoArmPending = false; Event("Захист увімкнено автоматично."); }
   }
   var wasPending = _guard.Deadline.HasValue;
   if (!_sessionLocked)
   {
    if (_guard.LockRequested && DateTimeOffset.UtcNow - _lastNativeLockRequest > TimeSpan.FromSeconds(5)) { _guard.LockFailed(); Storage.Log("Підтвердження блокування не отримано; повторна перевірка."); }
    var shouldLock = _guard.Tick(_guard.ActiveKey is not null && next.PresentKeys.Contains(_guard.ActiveKey), DateTimeOffset.UtcNow, _config.Preferences.DelaySeconds);
    if (shouldLock) LockWindows();
    else if (!wasPending && _guard.Deadline.HasValue)
    {
     Event($"{ActiveNoun} від’єднано. Блокування через {_config.Preferences.DelaySeconds} с.");
     if (!IsVisible) { Show(); WindowState = WindowState.Normal; }
    }
    else if (wasPending && !_guard.Deadline.HasValue && !_guard.LockRequested) Event($"{ActiveNoun} повернуто. Відлік скасовано.");
   }
   UpdateStatus();
  }
  catch (Exception error)
  {
   Storage.Log("Помилка перевірки USB: " + error.Message);
   // An unexpected enumeration failure still counts as missing key while armed.
   if (!_sessionLocked && _guard.Tick(false, DateTimeOffset.UtcNow, _config.Preferences.DelaySeconds)) LockWindows();
   FeedbackMessage("Помилка перевірки USB", error.Message, true); UpdateStatus();
  }
  finally { _scanning = false; }
 }
 private void UpdateStatus()
 {
  if (!_ready) return;
  var present = _guard.ActiveKey is not null && _scan.PresentKeys.Contains(_guard.ActiveKey);
  StatusTitle.Text = _sessionLocked ? "Сеанс Windows заблоковано" : _guard.LockRequested ? "Надіслано запит блокування" : _guard.Deadline.HasValue ? $"{ActiveNoun} від’єднано" : _guard.Armed ? "Захист увімкнено" : "Захист вимкнено";
  StatusDetail.Text = _guard.Armed ? $"{_config.Keys.FirstOrDefault(k => k.Id == _guard.ActiveKey)?.Name} · {(present ? "під’єднано" : "очікуємо повернення")}" : _autoArmPending ? "Очікуємо довірений ключ або пристрій для автоматичного ввімкнення." : KeysBox.SelectedItem is TrustedKey selectedSource ? $"Вибрано: {selectedSource.Name} · {(_scan.PresentKeys.Contains(selectedSource.Id) ? "під’єднано" : "не під’єднано")}" : "Виберіть під’єднане довірене джерело та ввімкніть захист.";
  ArmButton.Content = _guard.Armed ? "Вимкнути" : "Увімкнути";
  ArmButton.IsEnabled = !_arming && !_creating && (_guard.Armed || PeripheralIdentity.SelectForArming(_config.Keys, _scan.PresentKeys, KeysBox.SelectedItem as TrustedKey) is not null);
  CreateButton.IsEnabled = !_guard.Armed && !_creating && !_arming && DrivesBox.SelectedItem is not null;
  TrustDeviceButton.IsEnabled = !_guard.Armed && !_creating && !_arming && DevicesBox.SelectedItem is UsbPeripheral device && !_config.Keys.Any(k => PeripheralIdentity.Matches(k, device.InstanceId));
  KeysEmpty.Visibility = _config.Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed; RevokeButton.IsEnabled = _config.Keys.Count > 0; RevokeButton.Visibility = KeysBox.Visibility = _config.Keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  CountdownPanel.Visibility = _guard.Deadline.HasValue ? Visibility.Visible : Visibility.Collapsed;
  CountdownText.Text = $"Windows заблокується через {Math.Max(0, Math.Ceiling(((_guard.Deadline ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds))} с";
  CancelButton.Visibility = _config.Preferences.AllowCancel ? Visibility.Visible : Visibility.Collapsed;
  _trayToggle.Text = _guard.Armed ? "Вимкнути захист" : "Увімкнути захист";
  _trayToggle.Enabled = ArmButton.IsEnabled; _trayStatus.Text = StatusTitle.Text;
  _tray.Text = "USB Lock — " + StatusTitle.Text.ToLowerInvariant();
 }
 private async void ToggleArm(object sender, RoutedEventArgs e)
 {
  if (_guard.Armed) { _guard.Disarm(); _autoArmPending = false; Event("Захист вимкнено."); UpdateStatus(); return; }
  if (_arming || _creating) return;
  _arming = true; UpdateStatus();
  try { var trusted = _config.Keys.ToList(); _scan = await Task.Run(() => ProtectionSources.Scan(trusted)); }
  catch (Exception error) { FeedbackMessage("Помилка перевірки ключа", error.Message, true); return; }
  finally { _arming = false; UpdateStatus(); }
  var selected = KeysBox.SelectedItem as TrustedKey;
  var key = PeripheralIdentity.SelectForArming(_config.Keys, _scan.PresentKeys, selected);
  if (!_guard.Arm(key?.Id)) FeedbackMessage("Джерело не під’єднано", "Під’єднайте вибраний ключ або пристрій. Інше джерело не використовується замість нього.", true);
  else { _autoArmPending = false; Event("Захист увімкнено: " + key!.Name); }
  UpdateStatus();
 }
 private void CancelCountdown(object sender, RoutedEventArgs e)
 {
  if (!_config.Preferences.AllowCancel) return;
  _guard.Disarm(); _autoArmPending = false; Event("Відлік скасовано. Захист вимкнено."); UpdateStatus();
 }
 private async void CreateKey(object sender, RoutedEventArgs e)
 {
  if (_guard.Armed || _creating || _arming || DrivesBox.SelectedItem is not UsbDrive drive) return;
  _creating = true; UpdateStatus();
  try
  {
   _storage.Save(_config); // Verify DPAPI and local persistence before touching the USB drive.
   var bind = BindSerial.IsChecked == true; var name = KeyName.Text;
   var key = await Task.Run(() => UsbKeys.Create(drive, bind, name));
   _config.Keys.Add(key);
   try { _storage.Save(_config); }
   catch { _config.Keys.Remove(key); throw new IOException("Ключ записано на USB, але довіру не вдалося зберегти. Видаліть .usb-lock.key із флешки та повторіть створення."); }
   KeysBox.ItemsSource = _config.Keys.ToList(); KeysBox.SelectedItem = key;
   Event("Створено ключ: " + key.Name); FeedbackMessage("Ключ готовий", "Тепер можна ввімкнути захист."); await ScanAsync();
  }
  catch (Exception error) { FeedbackMessage("Не вдалося створити ключ", error is IOException && File.Exists(Path.Combine(drive.Root, UsbKeys.FileName)) ? error.Message + "\nІснуючі файли ключа не перезаписуються." : error.Message, true); }
  finally { _creating = false; UpdateStatus(); }
 }
 private void RevokeKey(object sender, RoutedEventArgs e)
 {
  if (KeysBox.SelectedItem is not TrustedKey key) { FeedbackMessage("Виберіть джерело", "Виберіть ключ або пристрій у списку довірених."); return; }
  if (System.Windows.MessageBox.Show($"Відкликати «{key.Name}»? Це джерело більше не зможе ввімкнути захист. Якщо він активний, захист буде вимкнено.", "Відкликання джерела", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  _config.Keys.Remove(key);
  try { _storage.Save(_config); }
  catch (Exception error) { _config.Keys.Add(key); FeedbackMessage("Не вдалося зберегти", error.Message, true); return; }
  if (_guard.ActiveKey == key.Id) { _guard.Disarm(); _autoArmPending = false; }
  KeysBox.ItemsSource = _config.Keys.ToList(); Event("Джерело відкликано: " + key.Name); UpdateStatus();
 }
 private string ActiveNoun => _config.Keys.FirstOrDefault(k => k.Id == _guard.ActiveKey)?.Kind == ProtectionSourceKind.UsbDevice ? "Пристрій" : "Ключ";
 private void SourceModeChanged(object sender, SelectionChangedEventArgs e)
 {
  if (FileSetup is null || PeripheralSetup is null) return;
  FileSetup.Visibility = SourceMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
  PeripheralSetup.Visibility = SourceMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
 }
 private void TrustedSelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdateStatus(); }
 private void UpdateDeviceChoices(List<UsbPeripheral> devices)
 {
  var signature = string.Join("|", devices.Select(d => d.InstanceId + d.DisplayName));
  if ((string?)DevicesBox.Tag != signature)
  {
   var previous = (DevicesBox.SelectedItem as UsbPeripheral)?.InstanceId;
   DevicesBox.ItemsSource = devices;
   DevicesBox.SelectedItem = devices.FirstOrDefault(d => d.InstanceId == previous) ?? devices.FirstOrDefault();
   DevicesBox.Tag = signature;
  }
  DeviceSelectionChanged(this, null!);
 }
 private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
 {
  if (DeviceHint is null) return;
  if (DevicesBox.SelectedItem is UsbPeripheral device)
  {
   var known = _config.Keys.Any(k => PeripheralIdentity.Matches(k, device.InstanceId));
   DeviceHint.Text = (known ? "Уже додано. " : "") + (device.UniqueId ? "Windows повідомляє унікальний ID пристрою." : "Без унікального ID: використовуйте той самий USB-порт; в іншому порту може знадобитися нова прив’язка.");
   DeviceIdentity.Text = device.InstanceId;
  }
  else { DeviceHint.Text = _scan.Errors.FirstOrDefault(error => error.StartsWith("USB-пристрої:")) ?? "Під’єднаних USB-пристроїв не знайдено."; DeviceIdentity.Text = ""; }
  if (_ready) UpdateStatus();
 }
 private async void TrustDevice(object sender, RoutedEventArgs e)
 {
  if (_guard.Armed || _creating || _arming || DevicesBox.SelectedItem is not UsbPeripheral device) return;
  _creating = true; UpdateStatus();
  try
  {
   var devices = await Task.Run(UsbPeripherals.Scan);
   var fresh = devices.FirstOrDefault(d => d.InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase))
    ?? throw new IOException("Пристрій уже від’єднано. Під’єднайте його й повторіть.");
   if (_config.Keys.Any(k => PeripheralIdentity.Matches(k, fresh.InstanceId))) throw new IOException("Цей пристрій уже є у списку довірених.");
   var key = new TrustedKey { Kind = ProtectionSourceKind.UsbDevice, DeviceInstanceId = fresh.InstanceId, Name = string.IsNullOrWhiteSpace(DeviceName.Text) ? fresh.Name : DeviceName.Text.Trim() };
   _config.Keys.Add(key);
   try { _storage.Save(_config); } catch { _config.Keys.Remove(key); throw; }
   KeysBox.ItemsSource = _config.Keys.ToList(); KeysBox.SelectedItem = key;
   Event("Додано USB-пристрій: " + key.Name); FeedbackMessage("Пристрій додано", "Виберіть його у списку довірених і ввімкніть захист.");
   await ScanAsync();
  }
  catch (Exception error) { FeedbackMessage("Не вдалося додати пристрій", error.Message, true); }
  finally { _creating = false; DeviceSelectionChanged(this, null!); UpdateStatus(); }
 }
 private void LockWindows()
 {
  if (_sessionLocked) return;
  _lastNativeLockRequest = DateTimeOffset.UtcNow;
  if (_config.Preferences.Sound) System.Media.SystemSounds.Exclamation.Play();
  if (!Native.LockWorkStation())
  {
   _guard.LockFailed(); FeedbackMessage("Windows не прийняла запит", new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message, true); Storage.Log("Помилка запиту блокування Windows.");
  }
  else Event("Надіслано запит блокування Windows.");
 }
 private void SessionChanged(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
 {
  if (e.Reason == SessionSwitchReason.SessionLock) { _sessionLocked = true; Storage.Log("Windows підтвердила блокування сеансу."); }
  if (e.Reason == SessionSwitchReason.SessionUnlock) { _sessionLocked = false; _guard.SessionUnlocked(); Storage.Log("Вхід до Windows."); _ = ScanAsync(); }
  UpdateStatus();
 });
 private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => { _ = ScanAsync(); }); }
 private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) { if (_config.Preferences.Theme == "System") Dispatcher.BeginInvoke(ApplyTheme); }
 private void ApplyTheme()
 {
  var theme = _config.Preferences.Theme;
  using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
  var dark = theme == "Dark" || theme == "System" && key?.GetValue("AppsUseLightTheme") is int light && light == 0;
  ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
 }
 private void LoadPreferences()
 {
  var p = _config.Preferences; DelayBox.Text = p.DelaySeconds.ToString(); AllowCancel.IsChecked = p.AllowCancel; Notifications.IsChecked = p.Notifications; Sound.IsChecked = p.Sound;
  StartMinimized.IsChecked = p.StartMinimized; AutoArm.IsChecked = p.AutoArm;
  foreach (ComboBoxItem item in ThemeBox.Items) if ((string)item.Tag == p.Theme) ThemeBox.SelectedItem = item;
  using var key = Registry.CurrentUser.OpenSubKey(RunPath); Startup.IsChecked = key?.GetValue("USB Lock") is string;
 }
 private void SavePreferences(object sender, RoutedEventArgs e)
 {
  try
  {
   if (!int.TryParse(DelayBox.Text, out var seconds)) throw new InvalidDataException("Введіть цілу кількість секунд.");
   var p = new Preferences { DelaySeconds = seconds, AllowCancel = AllowCancel.IsChecked == true, Notifications = Notifications.IsChecked == true, Sound = Sound.IsChecked == true, StartMinimized = StartMinimized.IsChecked == true, AutoArm = AutoArm.IsChecked == true, Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag };
   p.Validate();
   var old = _config.Preferences; _config.Preferences = p;
   try { _storage.Save(_config); } catch { _config.Preferences = old; throw; }
   // Startup registration is per-user and only changes on an explicit Save.
   using var run = Registry.CurrentUser.CreateSubKey(RunPath);
   if (Startup.IsChecked == true)
   {
    var executable = Environment.ProcessPath ?? throw new IOException("Шлях програми невідомий.");
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("Для автозапуску відкрийте UsbLock.exe.");
    run.SetValue("USB Lock", "\"" + executable + "\"");
   }
   else run.DeleteValue("USB Lock", false);
   ApplyTheme(); _autoArmPending = p.AutoArm && !_guard.Armed; UpdateStatus(); FeedbackMessage("Налаштування збережено", "Параметри застосовано."); Storage.Log("Налаштування оновлено.");
  }
  catch (Exception error) { FeedbackMessage("Помилка збереження", error.Message + "\nПеревірте параметри та автозапуск.", true); }
 }
 private void ExportPreferences(object sender, RoutedEventArgs e)
 {
  var dialog = new SaveFileDialog { Filter = "USB Lock preferences (*.json)|*.json", FileName = "usb-lock-preferences.json" };
  if (dialog.ShowDialog(this) != true) return;
  try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_config.Preferences, new JsonSerializerOptions { WriteIndented = true })); FeedbackMessage("Експорт готовий", "Збережено лише параметри, без ключів."); }
  catch (Exception error) { FeedbackMessage("Помилка експорту", error.Message, true); }
 }
 private void ImportPreferences(object sender, RoutedEventArgs e)
 {
  var dialog = new OpenFileDialog { Filter = "USB Lock preferences (*.json)|*.json" }; if (dialog.ShowDialog(this) != true) return;
  try
  {
   if (new FileInfo(dialog.FileName).Length > 16384) throw new InvalidDataException("Завеликий файл налаштувань.");
   var p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("Порожній файл."); p.Validate();
   // Import previews settings. User applies them explicitly with Save.
   DelayBox.Text = p.DelaySeconds.ToString(); AllowCancel.IsChecked = p.AllowCancel; Notifications.IsChecked = p.Notifications; Sound.IsChecked = p.Sound; StartMinimized.IsChecked = p.StartMinimized; AutoArm.IsChecked = p.AutoArm;
   foreach (ComboBoxItem item in ThemeBox.Items) if ((string)item.Tag == p.Theme) ThemeBox.SelectedItem = item;
   FeedbackMessage("Параметри імпортовано", "Перегляньте їх і натисніть «Зберегти», щоб застосувати.");
  }
  catch (Exception error) { FeedbackMessage("Помилка імпорту", error.Message, true); }
 }
 private void Event(string message)
 {
  Storage.Log(message);
  if (_config.Preferences.Notifications) _tray.ShowBalloonTip(3500, "USB Lock", message, Forms.ToolTipIcon.Info);
 }
 private void FeedbackMessage(string title, string message, bool error = false) { Feedback.Title = title; Feedback.Message = message; Feedback.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; Feedback.IsOpen = true; }
 private void UpdateHistory()
 {
  try { var path = Path.Combine(Storage.Folder, "activity.log"); HistoryText.Text = File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(100).Reverse()) : "Подій ще немає."; }
  catch (Exception error) { HistoryText.Text = error.Message; }
 }
 private void OpenWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
 private void OnClosing(object? sender, CancelEventArgs e) { if (!_exiting) { e.Cancel = true; Hide(); } }
 private void ExitApp()
 {
  if (_guard.Armed && System.Windows.MessageBox.Show("Вихід припинить USB-захист. Вийти?", "USB Lock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  _exiting = true; _timer.Stop(); _guard.Disarm();
  SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.UserPreferenceChanged -= ThemeChanged;
  _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); Storage.Log("Програму закрито. Захист вимкнено."); Close(); System.Windows.Application.Current.Shutdown();
 }
 private async void RefreshDrives(object sender, RoutedEventArgs e) => await ScanAsync();
 private void RefreshHistory(object sender, RoutedEventArgs e) => UpdateHistory();
 private void LockNow(object sender, RoutedEventArgs e) => LockWindows();
 private void HideWindow(object sender, RoutedEventArgs e) => Hide();
}






