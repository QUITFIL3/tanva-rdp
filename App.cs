using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

[assembly: AssemblyTitle("tanva-rdp")]
[assembly: AssemblyProduct("tanva-rdp")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace Tanva {
    public static class App {
        internal static object Resource(string name) {
            using (var stream=typeof(App).Assembly.GetManifestResourceStream(name)) return XamlReader.Load(stream);
        }
        [STAThread] public static void Main() {
            var app=new Application();
            try {
                app.Resources=(ResourceDictionary)Resource("Theme.xaml");
                var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"tanva-rdp");
                Directory.CreateDirectory(root);
                FileStream fileLock;
                try { fileLock=new FileStream(Path.Combine(root,"library.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch (IOException ex) {
                    if ((ex.HResult & 65535) != 32) throw;
                    MessageBox.Show("มีหน้าต่าง tanva-rdp เปิดอยู่แล้ว","tanva-rdp"); return;
                }
                using (fileLock) {
                    var controller=new Workspace(root);
                    app.Run(controller.Window);
                }
            } catch (Exception ex) { MessageBox.Show(ex.Message,"เปิด tanva-rdp ไม่สำเร็จ",MessageBoxButton.OK,MessageBoxImage.Error); }
        }
    }
    internal sealed class Workspace {
        public readonly Window Window;
        readonly string library, sessions;
        readonly DataGrid devices;
        readonly TextBox search;
        readonly ComboBox groups;
        List<Profile> profiles;
        string view="All";
        bool refreshing, busy;
        T Get<T>(string name) where T:FrameworkElement { return (T)Window.FindName(name); }
        Button Btn(string name) { return Get<Button>(name); }
        Profile Selected { get { return devices.SelectedItem as Profile; } }
        public Workspace(string root) {
            library=Path.Combine(root,"library.dat"); sessions=Path.Combine(root,"sessions");
            profiles=Storage.Load(library);
            if (Directory.Exists(sessions))
                foreach (var file in Directory.EnumerateFiles(sessions,"*.rdp"))
                    if (File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddDays(-1)) RemoteDesktop.TryRemove(file);

            Window=(Window)App.Resource("Main.xaml");
            Window.MaxHeight=SystemParameters.WorkArea.Height;
            Window.Height=Math.Min(Window.Height,Window.MaxHeight);
            devices=Get<DataGrid>("Devices"); search=Get<TextBox>("Search"); groups=Get<ComboBox>("Groups");
            foreach (var id in new[]{"All","Pins","Recent"}) {
                var captured=id;
                OnClick(id,delegate { view=captured; Refresh(null); });
            }
            OnClick("Add",delegate { Edit(null); });
            OnClick("Edit",delegate { if (Selected!=null) Edit(Selected); });
            OnClick("Connect",Connect);
            OnClick("Pin",delegate {
                if (Selected==null) return;
                var p=Selected.Copy(); p.Pinned=!p.Pinned; Upsert(p);
                Message(p.Pinned?"เพิ่มในรายการโปรดแล้ว":"นำออกจากรายการโปรดแล้ว");
            });
            OnClick("Delete",delegate {
                var p=Selected;
                if (p!=null && MessageBox.Show(Window,"ลบโปรไฟล์ “"+p.Name+"” ออกจากคลังนี้?","ลบเครื่อง",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes) {
                    Commit(profiles.Where(x=>x.Id!=p.Id).ToList(),null); Message("ลบโปรไฟล์แล้ว");
                }
            });
            Btn("Export").Click+=async delegate { await Transfer(true); };
            Btn("Import").Click+=async delegate { await Transfer(false); };
            groups.SelectionChanged+=delegate { if (!refreshing) Refresh(null); };
            search.TextChanged+=delegate { Refresh(null); };
            devices.SelectionChanged+=delegate { Actions(); };
            devices.MouseDoubleClick+=delegate(object sender, MouseButtonEventArgs e) {
                var source=e.OriginalSource as DependencyObject;
                if (source!=null && ItemsControl.ContainerFromElement(devices,source) is DataGridRow) Safe(Connect);
            };
            devices.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) {
                if (e.Key==Key.Enter) { e.Handled=true; Safe(Connect); }
            };
            Window.PreviewKeyDown+=delegate(object sender,KeyEventArgs e) {
                if (busy || Keyboard.Modifiers!=ModifierKeys.Control) return;
                if (e.Key==Key.F) { e.Handled=true; search.Focus(); search.SelectAll(); }
                if (e.Key==Key.N) { e.Handled=true; Safe(delegate { Edit(null); }); }
            };
            Window.Closing+=delegate(object sender,CancelEventArgs e) { if (busy) e.Cancel=true; };
            FillGroups(); Refresh(null);
        }
        void OnClick(string name, Action action) { Btn(name).Click+=delegate { Safe(action); }; }
        void Safe(Action action) {
            try { action(); } catch (Exception ex) { Error(ex); }
        }
        void Error(Exception ex) { MessageBox.Show(Window,ex.Message,"tanva-rdp",MessageBoxButton.OK,MessageBoxImage.Warning); }
        void Message(string text) { Get<TextBlock>("Status").Text=text; }
        void FillGroups() {
            var old=groups.SelectedIndex>0?groups.SelectedItem as string:null;
            var values=profiles.Select(p=>p.Group).Where(g=>g.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g=>g,StringComparer.CurrentCultureIgnoreCase).ToList();
            values.Insert(0,"ทุกกลุ่ม"); refreshing=true;
            try {
                groups.ItemsSource=values;
                int index=old==null?-1:values.FindIndex(1,g=>String.Equals(g,old,StringComparison.OrdinalIgnoreCase));
                groups.SelectedIndex=Math.Max(0,index);
            } finally { refreshing=false; }
        }
        void Refresh(string select) {
            string selected=select ?? (Selected==null?null:Selected.Id);
            var query=profiles.AsEnumerable();
            if (view=="Pins") query=query.Where(p=>p.Pinned);
            if (view=="Recent") query=query.Where(p=>p.LastLaunch>0);
            string group=groups.SelectedIndex>0?groups.SelectedItem as string:null;
            if (group!=null) query=query.Where(p=>String.Equals(p.Group,group,StringComparison.OrdinalIgnoreCase));
            var term=search.Text.Trim();
            if (term.Length>0) query=query.Where(p=>new[]{p.Name,p.Endpoint,p.User,p.Group}.Any(s=>s.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0));
            var list=(view=="Recent"?query.OrderByDescending(p=>p.LastLaunch):query.OrderByDescending(p=>p.Pinned)).ThenBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
            devices.ItemsSource=list;
            devices.SelectedItem=list.FirstOrDefault(p=>p.Id==selected);
            if (devices.SelectedItem!=null) devices.ScrollIntoView(devices.SelectedItem);
            var title=view=="Pins"?"รายการโปรด":view=="Recent"?"เปิดล่าสุด":"เครื่องทั้งหมด";
            Get<TextBlock>("TitleText").Text=title;
            foreach (var id in new[]{"All","Pins","Recent"}) {
                var button=Btn(id);
                if (id==view) { button.Background=new SolidColorBrush(Color.FromRgb(48,85,76)); button.FontWeight=FontWeights.SemiBold; }
                else { button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.FontWeightProperty); }
            }
            Get<TextBlock>("Count").Text=list.Count+" / "+profiles.Count+" เครื่อง";
            Get<TextBlock>("Hint").Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
            Get<StackPanel>("Empty").Visibility=list.Count==0?Visibility.Visible:Visibility.Collapsed;
            bool filtered=term.Length!=0 || group!=null;
            Get<TextBlock>("EmptyTitle").Text=filtered?"ไม่พบเครื่องที่ตรงกัน":view=="Pins"?"ยังไม่มีรายการโปรด":view=="Recent"?"ยังไม่มีประวัติการเปิด":"เพิ่มเครื่องแรกของคุณ";
            Get<TextBlock>("EmptyHelp").Text=filtered?"ลองเปลี่ยนคำค้นหาหรือเลือกทุกกลุ่ม":view=="Pins"?"เลือกเครื่องแล้วกด “เพิ่มในรายการโปรด”":view=="Recent"?"เครื่องที่คุณเปิดผ่าน tanva-rdp จะแสดงที่นี่":"กด “เพิ่มเครื่อง” เพื่อบันทึกชื่อโฮสต์หรือ IP สำหรับเชื่อมต่อ";
            Actions();
        }
        void Actions() {
            var p=Selected;
            foreach (var id in new[]{"Connect","Edit","Pin","Delete"}) Btn(id).IsEnabled=p!=null;
            Btn("Pin").Content=p!=null && p.Pinned?"นำออกจากรายการโปรด":"เพิ่มในรายการโปรด";
            Btn("Export").IsEnabled=profiles.Count!=0;
        }
        void Commit(List<Profile> next,string select) {
            Storage.Save(library,next); profiles=next; FillGroups(); Refresh(select);
        }
        void Upsert(Profile p) {
            var next=profiles.Where(x=>x.Id!=p.Id).ToList(); next.Add(p); Commit(next,p.Id);
        }
        void Edit(Profile p) {
            var next=Dialog.Edit(Window,p);
            if (next==null) return;
            Upsert(next);
            if (p==null) { view="All"; groups.SelectedIndex=0; search.Clear(); Refresh(next.Id); }
            Message("บันทึก "+next.Name+" แล้ว");
        }
        void Connect() {
            var p=Selected;
            if (p==null) return;
            RemoteDesktop.Launch(p,sessions);
            var updated=p.Copy(); updated.LastLaunch=DateTime.UtcNow.Ticks;
            Upsert(updated); Message("เปิด Remote Desktop สำหรับ "+p.Name+" แล้ว");
        }
        async Task Transfer(bool export) {
            if (busy) return;
            try {
                FileDialog picker=export ? (FileDialog)new SaveFileDialog { FileName="tanva-rdp-"+DateTime.Now.ToString("yyyy-MM-dd"),AddExtension=true,DefaultExt=".tanva" } : new OpenFileDialog { CheckFileExists=true };
                picker.Filter="ไฟล์สำรอง tanva-rdp (*.tanva)|*.tanva";
                if (picker.ShowDialog(Window)!=true) return;
                var pass=Dialog.Passphrase(Window,export);
                if (pass==null) return;
                var path=picker.FileName;
                if (export && !String.Equals(Path.GetExtension(path),".tanva",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("กรุณาตั้งชื่อไฟล์สำรองให้ลงท้ายด้วย .tanva");
                busy=true; ((UIElement)Window.Content).IsEnabled=false;
                Message(export?"กำลังเข้ารหัสไฟล์สำรอง…":"กำลังตรวจสอบไฟล์สำรอง…");
                if (export) {
                    var snapshot=profiles.ToList();
                    await Task.Run(delegate { Storage.Write(path,Backup.Seal(snapshot,pass)); });
                    Message("สำรองข้อมูล "+snapshot.Count+" เครื่องแล้ว");
                } else {
                    var incoming=await Task.Run(delegate { return Backup.Open(Storage.Read(path),pass); });
                    var fresh=Storage.Missing(profiles,incoming);
                    if (fresh.Count>0) Commit(profiles.Concat(fresh).ToList(),null);
                    Message("กู้คืน "+fresh.Count+" เครื่อง · ข้ามโปรไฟล์เดิม "+(incoming.Count-fresh.Count)+" เครื่อง");
                }
            } catch (Exception ex) { Message("ดำเนินการไม่สำเร็จ"); Error(ex); }
            finally { busy=false; ((UIElement)Window.Content).IsEnabled=true; }
        }
    }
}
