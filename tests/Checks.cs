using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tanva;

public static class Checks {
    static int passed;
    static void Check(bool result,string label) {
        if (!result) throw new Exception(label);
        Console.WriteLine("PASS " + label); passed++;
    }
    static void Reject(Action action,string label) {
        try { action(); } catch (InvalidDataException) { Check(true,label); return; }
        throw new Exception(label);
    }
    static T Control<T>(Window window,string name) where T:FrameworkElement { return (T)window.FindName(name); }
    static void Click(Window window,string name) { Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    static IEnumerable<T> Children<T>(DependencyObject node) where T:DependencyObject {
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) {
            var child=VisualTreeHelper.GetChild(node,i);
            if (child is T) yield return (T)child;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    static void Render(Window window,string path) {
        if (String.IsNullOrEmpty(path)) return;
        window.UpdateLayout();
        var content=(FrameworkElement)window.Content;
        var image=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);
        image.Render(content);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using(var output=File.Create(path)) encoder.Save(output);
    }
    static void CheckMstsc(Window window, string dir, Profile p) {
        var grid=Control<DataGrid>(window,"Devices");
        grid.SelectedItem=grid.Items.Cast<Profile>().Single(x=>x.Id==p.Id);
        Click(window,"Connect");
        string sessionDir=Path.Combine(dir,"sessions");
        string rdp=Directory.GetFiles(sessionDir,"*.rdp").Single();
        Check(File.ReadAllText(rdp).Contains("full address:s:"+p.Endpoint),"Connect writes the selected loopback endpoint");
        int parent=Process.GetCurrentProcess().Id;
        using(var query=new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='mstsc.exe' AND ParentProcessId="+parent))
        using(var children=query.Get()) {
            var child=children.Cast<ManagementObject>().Single(c=>Convert.ToString(c["CommandLine"]).Contains(rdp));
            using(var process=Process.GetProcessById(Convert.ToInt32(child["ProcessId"]))) {
                Check(!process.HasExited,"Connect starts the Windows MSTSC client");
                process.Kill(); process.WaitForExit(5000);
            }
        }
        Check(System.Threading.SpinWait.SpinUntil(delegate { return !File.Exists(rdp); },5000),"remove temporary RDP file after client exit");
        Click(window,"Recent");
        Check(grid.Items.Count==1 && ((Profile)grid.Items[0]).Id==p.Id,"record client launch in Recent");
        Click(window,"All");
    }
    [STAThread] public static int Main(string[] args) {
        var dir=Path.Combine(Path.GetTempPath(),"tanva-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var p=new Profile { Name="QA machine", Host="::1", Port=3391, User=@"LAB\tester", Password="fixture-only-secret", Group="Lab", Display="multi", Clipboard=true };
            p.Validate(); Check(p.Endpoint=="[::1]:3391","IPv6 endpoint with custom port");
            var settings=RemoteDesktop.Settings(p);
            Check(settings.Contains("use multimon:i:1") && settings.Contains("redirectclipboard:i:1") && settings.Contains("authentication level:i:2"),"MSTSC display, clipboard and authentication properties");
            var secret=settings.Split(new[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries).Single(s=>s.StartsWith("password 51:b:")).Substring("password 51:b:".Length);
            var bytes=Enumerable.Range(0,secret.Length/2).Select(i=>Convert.ToByte(secret.Substring(i*2,2),16)).ToArray();
            Check(Encoding.Unicode.GetString(ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser))==p.Password && !settings.Contains(p.Password),"DPAPI password field round-trip");
            var bad=p.Copy(); bad.Host="localhost\r\nredirectclipboard:i:1";
            Reject(delegate{ RemoteDesktop.Settings(bad); },"reject multiline host");
            bad=p.Copy();bad.User="u\r\nother:s:value";
            Reject(delegate{ RemoteDesktop.Settings(bad); },"reject multiline username");
            bad=p.Copy();bad.Port=65536;Reject(bad.Validate,"reject invalid port");
            var blank=new Profile {Name="Desk",Host="desk.example.test",Display="window"};
            Check(!RemoteDesktop.Settings(blank).Contains("password 51") && RemoteDesktop.Settings(blank).Contains("desktopwidth:i:1280"),"native sign-in and windowed mode");
            var file=Path.Combine(dir,"library.dat");
            Storage.Save(file,new List<Profile>{p}); Storage.Save(file,new List<Profile>{p,blank});
            Check(Storage.Load(file)[0].Password==p.Password && Storage.Load(file).Count==2,"atomic save and reopen");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(p.Password),"encrypted local store");
            string phrase="test-only passphrase";
            var archive=Backup.Seal(Storage.Load(file),phrase);
            Check(Backup.Open(archive,phrase)[0].Password==p.Password,"password-protected backup round-trip");
            var restored=Storage.Missing(new List<Profile>{p},Backup.Open(archive,phrase));
            Check(restored.Count==1 && restored[0].Id==blank.Id,"restore skips existing IDs and keeps new profiles");
            Reject(delegate { Backup.Open(archive,"incorrect passphrase"); },"reject wrong backup password");
            archive[42]^=1; Reject(delegate { Backup.Open(archive,phrase); },"reject modified backup");
            Reject(delegate { Backup.Open(new byte[7],phrase); },"reject truncated backup");

            var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            using(var resource=typeof(App).Assembly.GetManifestResourceStream("Theme.xaml"))
                app.Resources=(ResourceDictionary)System.Windows.Markup.XamlReader.Load(resource);
            var controller=Activator.CreateInstance(typeof(App).Assembly.GetType("Tanva.Workspace"),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,new object[]{dir},null);
            var window=(Window)controller.GetType().GetField("Window").GetValue(controller);
            window.WindowStartupLocation=WindowStartupLocation.Manual; window.Left=-12000; window.Top=-12000;
            window.ShowInTaskbar=false; window.ShowActivated=false; window.Show(); window.UpdateLayout();
            var grid=Control<DataGrid>(window,"Devices");
            var search=Control<TextBox>(window,"Search");
            Check(grid.Items.Count==2,"WPF library loads saved profiles");
            search.Text="LAB";Check(grid.Items.Count==1,"group and user search");
            search.Text="3391";Check(grid.Items.Count==1,"custom port search");
            search.Clear();
            grid.SelectedItem=grid.Items.Cast<Profile>().First(x=>x.Id==p.Id);Click(window,"Pin");Click(window,"Pins");
            Check(grid.Items.Count==1 && Storage.Load(file).First(x=>x.Id==p.Id).Pinned,"favorite action and persistence");
            Click(window,"Recent");Check(grid.Items.Count==0,"empty recent view");
            Click(window,"All");Control<ComboBox>(window,"Groups").SelectedIndex=1;
            Check(grid.Items.Count==1,"group selector"); Control<ComboBox>(window,"Groups").SelectedIndex=0;
            // Drive the real modal editor through its named accessible fields.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate {
                var dialog=app.Windows.Cast<Window>().Single(w=>w!=window);
                var boxes=Children<TextBox>(dialog).ToList();
                boxes.Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="ชื่อเครื่อง").Text="Office";
                boxes.Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="ชื่อโฮสต์หรือ IP").Text="office.example.test";
                Children<Button>(dialog).Single(b=>b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Click(window,"Add");
            Check(grid.Items.Count==3 && ((Profile)grid.SelectedItem).Name=="Office","create profile through modal editor");
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate {
                var dialog=app.Windows.Cast<Window>().Single(w=>w!=window);
                Children<TextBox>(dialog).Single(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="ชื่อเครื่อง").Text="Office PC";
                Children<Button>(dialog).Single(b=>b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Click(window,"Edit");
            Check(Storage.Load(file).Any(x=>x.Name=="Office PC"),"edit selected profile through modal editor");
            if (args.Contains("--launch-check")) {
                var listener=new TcpListener(IPAddress.IPv6Loopback,0);
                listener.Start();
                try {
                    var target=grid.Items.Cast<Profile>().Single(x=>x.Id==p.Id);
                    target.Port=((IPEndPoint)listener.LocalEndpoint).Port;
                    CheckMstsc(window,dir,target);
                } finally { listener.Stop(); }
            }
            Render(window,args.FirstOrDefault(arg=>arg!="--launch-check"));
            window.Width=790;window.Height=490;window.UpdateLayout();
            Check(grid.ActualWidth>350 && grid.ActualHeight>90 && Control<Button>(window,"Delete").IsVisible,"minimum window layout");
            window.Close();Console.WriteLine(passed+" checks passed.");return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(dir,true); }
    }
}
