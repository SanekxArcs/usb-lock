using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace UsbLock;
// UI only. Protection, tray and device events live in ProtectionController; this window is
// created when opened from the tray and closed (not hidden) when dismissed, to free its memory.
public partial class MainWindow : FluentWindow
{
 private readonly ProtectionController _c;
 private bool _ready, _refreshing;
 private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
 public MainWindow(ProtectionController controller)
 {
  _c = controller; InitializeComponent(); LoadPreferences(); ApplyTheme();
  _c.Changed += Refresh; _c.Feedback += FeedbackMessage; SystemEvents.UserPreferenceChanged += ThemeChanged;
  Closed += (_, _) => { _c.Changed -= Refresh; _c.Feedback -= FeedbackMessage; SystemEvents.UserPreferenceChanged -= ThemeChanged; };
  _ready = true; Refresh();
 }
 private void Navigate(object sender, SelectionChangedEventArgs e)
 {
  if (ProtectionPage is null) return;
  ProtectionPage.Visibility = Navigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
  SettingsPage.Visibility = Navigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
  HistoryPage.Visibility = Navigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
  if (Navigation.SelectedIndex == 2) UpdateHistory();
 }
 // Pull everything from the controller; list controls are only rebuilt when their contents change.
 private void Refresh()
 {
  if (!_ready) return;
  _refreshing = true;
  try
  {
   var scan = _c.Scan;
   var keySignature = string.Join("|", _c.Config.Keys.Select(k => k.Id + k.Name));
   if ((string?)KeysBox.Tag != keySignature)
   {
    KeysBox.Tag = keySignature; var selectedId = _c.SelectedKey?.Id;
    KeysBox.ItemsSource = _c.Config.Keys.ToList(); KeysBox.SelectedItem = _c.Config.Keys.FirstOrDefault(k => k.Id == selectedId);
   }
   else if (_c.SelectedKey is not null && KeysBox.SelectedItem != _c.SelectedKey) KeysBox.SelectedItem = _c.SelectedKey;
   _c.SelectedKey = KeysBox.SelectedItem as TrustedKey;
   var driveSignature = string.Join("|", scan.Drives.Select(d => d.ToString()));
   if ((string?)DrivesBox.Tag != driveSignature)
   {
    var root = (DrivesBox.SelectedItem as UsbDrive)?.Root;
    DrivesBox.ItemsSource = scan.Drives; DrivesBox.SelectedItem = scan.Drives.FirstOrDefault(d => d.Root == root) ?? scan.Drives.FirstOrDefault(); DrivesBox.Tag = driveSignature;
   }
   if (scan.Devices is not null)
   {
    var deviceSignature = string.Join("|", scan.Devices.Select(d => d.InstanceId + d.DisplayName));
    if ((string?)DevicesBox.Tag != deviceSignature)
    {
     var previous = (DevicesBox.SelectedItem as UsbPeripheral)?.InstanceId;
     DevicesBox.ItemsSource = scan.Devices;
     DevicesBox.SelectedItem = scan.Devices.FirstOrDefault(d => d.InstanceId == previous) ?? scan.Devices.FirstOrDefault();
     DevicesBox.Tag = deviceSignature;
    }
   }
   DriveHint.Text = scan.Errors.Count > 0 ? string.Join("\n", scan.Errors) : scan.Drives.Count == 0 ? "Знімних накопичувачів немає. Під’єднайте USB-флешку." : $"Знайдено накопичувачів: {scan.Drives.Count}. Довірених джерел під’єднано: {scan.PresentKeys.Count}.";
  }
  finally { _refreshing = false; }
  UpdateDeviceHint(); UpdateStatus();
 }
 private void UpdateStatus()
 {
  if (!_ready) return;
  var g = _c.Guard; var config = _c.Config;
  StatusTitle.Text = _c.StatusTitle; StatusDetail.Text = _c.StatusDetail;
  ArmButton.Content = g.Armed ? "Вимкнути" : "Увімкнути";
  ArmButton.IsEnabled = _c.CanToggle;
  CreateButton.IsEnabled = !g.Armed && !_c.Busy && DrivesBox.SelectedItem is not null;
  TrustDeviceButton.IsEnabled = !g.Armed && !_c.Busy && DevicesBox.SelectedItem is UsbPeripheral device && !config.Keys.Any(k => PeripheralIdentity.Matches(k, device.InstanceId));
  KeysEmpty.Visibility = config.Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed; RevokeButton.IsEnabled = config.Keys.Count > 0; RevokeButton.Visibility = KeysBox.Visibility = config.Keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  CountdownPanel.Visibility = g.Deadline.HasValue ? Visibility.Visible : Visibility.Collapsed;
  CountdownText.Text = $"Windows заблокується через {Math.Max(0, Math.Ceiling(((g.Deadline ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds))} с";
  CancelButton.Visibility = config.Preferences.AllowCancel ? Visibility.Visible : Visibility.Collapsed;
 }
 private void UpdateDeviceHint()
 {
  if (DeviceHint is null) return;
  if (DevicesBox.SelectedItem is UsbPeripheral device)
  {
   var known = _c.Config.Keys.Any(k => PeripheralIdentity.Matches(k, device.InstanceId));
   DeviceHint.Text = (known ? "Уже додано. " : "") + (device.UniqueId ? "Windows повідомляє унікальний ID пристрою." : "Без унікального ID: використовуйте той самий USB-порт; в іншому порту може знадобитися нова прив’язка.");
   DeviceIdentity.Text = device.InstanceId;
  }
  else { DeviceHint.Text = _c.Scan.Errors.FirstOrDefault(error => error.StartsWith("USB-пристрої:")) ?? "Під’єднаних USB-пристроїв не знайдено."; DeviceIdentity.Text = ""; }
 }
 private async void ToggleArm(object sender, RoutedEventArgs e) => await _c.ToggleArmAsync();
 private void CancelCountdown(object sender, RoutedEventArgs e) => _c.CancelCountdown();
 private async void CreateKey(object sender, RoutedEventArgs e)
 {
  if (DrivesBox.SelectedItem is UsbDrive drive) await _c.CreateKeyAsync(drive, BindSerial.IsChecked == true, KeyName.Text);
 }
 private void RevokeKey(object sender, RoutedEventArgs e)
 {
  if (KeysBox.SelectedItem is not TrustedKey key) { FeedbackMessage("Виберіть джерело", "Виберіть ключ або пристрій у списку довірених."); return; }
  if (System.Windows.MessageBox.Show($"Відкликати «{key.Name}»? Це джерело більше не зможе ввімкнути захист. Якщо він активний, захист буде вимкнено.", "Відкликання джерела", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
  _c.Revoke(key);
 }
 private async void TrustDevice(object sender, RoutedEventArgs e)
 {
  if (DevicesBox.SelectedItem is UsbPeripheral device) await _c.TrustDeviceAsync(device, DeviceName.Text);
 }
 private void SourceModeChanged(object sender, SelectionChangedEventArgs e)
 {
  if (FileSetup is null || PeripheralSetup is null) return;
  FileSetup.Visibility = SourceMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
  PeripheralSetup.Visibility = SourceMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
 }
 private void TrustedSelectionChanged(object sender, SelectionChangedEventArgs e)
 {
  if (!_ready || _refreshing) return;
  _c.SelectedKey = KeysBox.SelectedItem as TrustedKey; _c.Notify(); // tray toggle + status depend on the selection
 }
 private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e) { UpdateDeviceHint(); UpdateStatus(); }
 private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) { if (_c.Config.Preferences.Theme == "System") Dispatcher.BeginInvoke(ApplyTheme); }
 private void ApplyTheme()
 {
  var theme = _c.Config.Preferences.Theme;
  using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
  var dark = theme == "Dark" || theme == "System" && key?.GetValue("AppsUseLightTheme") is int light && light == 0;
  ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
 }
 private void LoadPreferences()
 {
  var p = _c.Config.Preferences; DelayBox.Text = p.DelaySeconds.ToString(); AllowCancel.IsChecked = p.AllowCancel; Notifications.IsChecked = p.Notifications; Sound.IsChecked = p.Sound;
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
   _c.ApplyPreferences(p);
   // Startup registration is per-user and only changes on an explicit Save.
   using var run = Registry.CurrentUser.CreateSubKey(RunPath);
   if (Startup.IsChecked == true)
   {
    var executable = Environment.ProcessPath ?? throw new IOException("Шлях програми невідомий.");
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new IOException("Для автозапуску відкрийте UsbLock.exe.");
    run.SetValue("USB Lock", "\"" + executable + "\"");
   }
   else run.DeleteValue("USB Lock", false);
   ApplyTheme(); FeedbackMessage("Налаштування збережено", "Параметри застосовано."); Storage.Log("Налаштування оновлено.");
  }
  catch (Exception error) { FeedbackMessage("Помилка збереження", error.Message + "\nПеревірте параметри та автозапуск.", true); }
 }
 private void ExportPreferences(object sender, RoutedEventArgs e)
 {
  var dialog = new SaveFileDialog { Filter = "USB Lock preferences (*.json)|*.json", FileName = "usb-lock-preferences.json" };
  if (dialog.ShowDialog(this) != true) return;
  try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_c.Config.Preferences, new JsonSerializerOptions { WriteIndented = true })); FeedbackMessage("Експорт готовий", "Збережено лише параметри, без ключів."); }
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
 private void FeedbackMessage(string title, string message, bool error = false) { Feedback.Title = title; Feedback.Message = message; Feedback.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; Feedback.IsOpen = true; }
 private void UpdateHistory()
 {
  try { var path = Path.Combine(Storage.Folder, "activity.log"); HistoryText.Text = File.Exists(path) ? string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(100).Reverse()) : "Подій ще немає."; }
  catch (Exception error) { HistoryText.Text = error.Message; }
 }
 private async void RefreshDrives(object sender, RoutedEventArgs e) => await _c.RescanNowAsync();
 private void RefreshHistory(object sender, RoutedEventArgs e) => UpdateHistory();
 private void LockNow(object sender, RoutedEventArgs e) => _c.LockWindows();
 private void HideWindow(object sender, RoutedEventArgs e) => Close();
}
