using System.Windows;
namespace UsbLock;
public partial class App : System.Windows.Application
{
 public bool PreviewMode { get; init; }
 private Mutex? _mutex;
 public ProtectionController? Controller { get; private set; }
 protected override void OnStartup(StartupEventArgs e)
 {
  if (PreviewMode) return;
  base.OnStartup(e);
  _mutex = new Mutex(true, @"Local\UsbLock-" + Environment.UserName, out var first);
  if (!first) { MessageBox.Show("USB Lock уже працює. Відкрийте його через піктограму в треї.", "USB Lock"); Shutdown(); return; }
  DispatcherUnhandledException += (_, args) => { Storage.Log(args.Exception.ToString()); MessageBox.Show(args.Exception.Message, "USB Lock — помилка"); args.Handled = true; };
  try
  {
   var config = new Storage().Load(); Controller = new ProtectionController(config);
   if (!config.Preferences.StartMinimized || e.Args.Contains("--show")) Controller.OpenWindow();
  }
  catch (Exception error)
  {
   Storage.Log(error.ToString()); MessageBox.Show("Не вдалося запустити програму. Захист не ввімкнено. Файл збережено без змін.\n\n" + error.Message, "USB Lock"); Shutdown(1);
  }
 }
 protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}



