using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsbLock;
internal static class Program
{
 [STAThread] static int Main()
 {
  try
  {
   var peripherals = UsbPeripherals.Scan();
   if (peripherals.Any(d => !PeripheralIdentity.IsPhysicalUsb(d.InstanceId)) || peripherals.Select(d => d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != peripherals.Count) throw new Exception("Invalid or duplicate physical USB inventory");
   var combined = ProtectionSources.Scan([]);
   if (combined.Devices is null || combined.PresentKeys.Count != 0) throw new Exception("Combined discovery returned invalid empty-trust snapshot");
   Console.WriteLine($"PASS: live SetupAPI enumeration ({peripherals.Count} physical USB devices); combined scan with no trusted sources.");
   // Hidden-window fast path must agree with full enumeration, both for present devices and for an absent one.
   var trustedLive = peripherals.Select(d => new TrustedKey { Kind = ProtectionSourceKind.UsbDevice, DeviceInstanceId = d.InstanceId.ToLowerInvariant(), Hash = "x" }).ToList();
   var absent = new TrustedKey { Kind = ProtectionSourceKind.UsbDevice, DeviceInstanceId = @"USB\VID_FFFF&PID_FFFF\USBLOCK-ABSENT", Hash = "x" };
   trustedLive.Add(absent);
   var full = ProtectionSources.Scan(trustedLive); var fast = ProtectionSources.Scan(trustedLive, includeDeviceList: false);
   if (!full.PresentKeys.SetEquals(fast.PresentKeys) || fast.PresentKeys.Contains(absent.Id) || fast.Devices is not null || fast.PresentKeys.Count != peripherals.Count) throw new Exception("Fast presence check disagrees with full enumeration");
   var clock = System.Diagnostics.Stopwatch.StartNew(); for (var i = 0; i < 20; i++) ProtectionSources.Scan(trustedLive); var fullMs = clock.Elapsed.TotalMilliseconds / 20;
   clock.Restart(); for (var i = 0; i < 20; i++) ProtectionSources.Scan(trustedLive, includeDeviceList: false); var fastMs = clock.Elapsed.TotalMilliseconds / 20;
   Console.WriteLine($"PASS: fast presence check matches full enumeration for {peripherals.Count} live devices + 1 absent ({fullMs:0.00} ms -> {fastMs:0.00} ms per scan).");
   var dataFolder = Path.Combine(Environment.CurrentDirectory, "usb-lock", "artifacts", "storage-check");
   var store = new Storage(dataFolder);
   var config = new Configuration { Preferences = new Preferences { DelaySeconds = 23 }, Keys = [new TrustedKey { Id = "test", Hash = KeyValidation.Hash("test") }] };
   try {
   store.Save(config);
   if (store.Load().Preferences.DelaySeconds != 23 || store.Load().Keys.Single().Id != "test") throw new Exception("DPAPI roundtrip failed");
   var dataPath = Path.Combine(dataFolder, "settings.dat");
   if (File.ReadAllText(dataPath).Contains("DelaySeconds")) throw new Exception("Settings stored in plaintext");
   File.WriteAllText(dataPath, "corrupt");
   var rejected = false; try { store.Load(); } catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
   if (!rejected) throw new Exception("Corrupt protected settings accepted");
   Console.WriteLine("PASS: DPAPI settings roundtrip, ciphertext and corrupt-file rejection.");
   } catch (System.Security.Cryptography.CryptographicException error) { Console.WriteLine("SKIP: DPAPI unavailable in execution profile: " + error.Message); }
   var app = new App { PreviewMode = true }; app.InitializeComponent();
   var controller = new ProtectionController(new Configuration());
   controller.OpenWindow();
   var window = controller.Window ?? throw new Exception("Window was not created");
   // Closing must destroy the window (not hide it) and reopening must build a fresh one.
   window.Close();
   if (controller.Window is not null) throw new Exception("Closed window is still referenced");
   controller.OpenWindow(); window = controller.Window ?? throw new Exception("Window was not recreated");
   Console.WriteLine("PASS: window is destroyed on close and rebuilt on open.");
   var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
   timer.Tick += (_, _) =>
   {
    timer.Stop();
    try
    {
     var folder = Path.Combine(Environment.CurrentDirectory, "usb-lock", "artifacts"); Directory.CreateDirectory(folder);
     void Render(string name)
     {
      window.UpdateLayout();
      var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
      var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(folder,name+".png")); png.Save(stream);
     }
     Render("protection");
     var sourceMode = (ComboBox)window.FindName("SourceMode"); sourceMode.SelectedIndex = 1; Render("usb-devices");
     var nav = (ListBox)window.FindName("Navigation"); nav.SelectedIndex = 1; Render("settings"); nav.SelectedIndex = 2; Render("history");
     Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, true);
     nav.SelectedIndex = 0; Render("usb-devices-dark"); sourceMode.SelectedIndex = 0; Render("protection-dark"); nav.SelectedIndex = 1; Render("settings-dark");
     window.Width = 880; window.Height = 640; Render("settings-compact"); nav.SelectedIndex = 0; sourceMode.SelectedIndex = 1; Render("usb-devices-compact");
     Console.WriteLine("PASS: constructed native WPF window, loaded resources and rendered all 3 pages.");
    }
    catch (Exception e) { Console.WriteLine(e); Environment.ExitCode = 1; }
    finally { controller.Exit(); }
   };
   timer.Start(); app.Run(); return Environment.ExitCode;
  }
  catch (Exception e) { Console.WriteLine(e); return 1; }
 }
}





