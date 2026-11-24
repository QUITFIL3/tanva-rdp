using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tanva {
    internal sealed class Dialog {
        readonly Window window;
        readonly StackPanel fields;
        readonly TextBlock error;
        readonly Button accept;
        readonly Grid body;

        Dialog(Window owner, string title, string action, double width) {
            window = new Window { Owner=owner, Title=title, Width=width, SizeToContent=SizeToContent.Height,
                MaxHeight=Math.Min(SystemParameters.WorkArea.Height-20,795), ResizeMode=ResizeMode.CanResize,
                MinWidth=Math.Min(width,390), WindowStartupLocation=WindowStartupLocation.CenterOwner, ShowInTaskbar=false };
            body = new Grid { Margin=new Thickness(23,16,23,17) };
            body.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            var heading = new TextBlock { Text=title, FontSize=24, FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,5) };
            body.Children.Add(heading);
            fields = new StackPanel();
            var scroll = new ScrollViewer { Content=fields, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            Grid.SetRow(scroll,1); body.Children.Add(scroll);
            var footer = new StackPanel();
            error = new TextBlock { Foreground=Brushes.Firebrick, TextWrapping=TextWrapping.Wrap, Visibility=Visibility.Collapsed };
            AutomationProperties.SetLiveSetting(error,AutomationLiveSetting.Assertive); footer.Children.Add(error);
            var buttons = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,12,0,0) };
            buttons.Children.Add(new Button { Content="ยกเลิก", IsCancel=true, Margin=new Thickness(0,0,8,0) });
            accept = new Button { Content=action, IsDefault=true, Style=(Style)Application.Current.FindResource("Primary") };
            buttons.Children.Add(accept); footer.Children.Add(buttons); Grid.SetRow(footer,2); body.Children.Add(footer); window.Content=body;
        }
        void Field(Panel parent, string label, Control control) {
            var caption = new Label { Content=label, Target=control, Padding=new Thickness(0,5,0,2) };
            AutomationProperties.SetName(control,label); parent.Children.Add(caption); parent.Children.Add(control);
        }
        TextBox Text(Panel parent,string label,string value,int limit) {
            var box=new TextBox { Text=value,MaxLength=limit }; Field(parent,label,box); return box;
        }
        PasswordBox Secret(string label,string value) {
            var box=new PasswordBox { Password=value,MaxLength=512 }; Field(fields,label,box); return box;
        }
        bool Run(Action save, Control focus) {
            window.ContentRendered += delegate { focus.Focus(); };
            accept.Click += delegate {
                try { save(); window.DialogResult=true; }
                catch (InvalidDataException ex) { error.Text=ex.Message; error.Visibility=Visibility.Visible; }
            };
            return window.ShowDialog()==true;
        }
        public static Profile Edit(Window owner, Profile original) {
            var p=original == null ? new Profile() : original.Copy();
            var d=new Dialog(owner, original == null ? "เพิ่มเครื่อง" : "แก้ไขเครื่อง","บันทึก",480);
            var name=d.Text(d.fields,"ชื่อเครื่อง",p.Name,100);
            var pair=new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(98) });
            var left=new StackPanel { Margin=new Thickness(0,0,10,0) }; var right=new StackPanel();
            Grid.SetColumn(right,1); pair.Children.Add(left); pair.Children.Add(right); d.fields.Children.Add(pair);
            var host=d.Text(left,"ชื่อโฮสต์หรือ IP",p.Host,253); var port=d.Text(right,"พอร์ต",p.Port.ToString(CultureInfo.InvariantCulture),5);
            var group=d.Text(d.fields,"กลุ่ม (ถ้ามี)",p.Group,100);
            var user=d.Text(d.fields,"ชื่อผู้ใช้ (ถ้ามี)",p.User,256);
            var password=d.Secret("รหัสผ่าน (เว้นว่างเพื่อใช้การเข้าสู่ระบบของ Windows)",p.Password);
            var display=new ComboBox { ItemsSource=new[]{"เต็มหน้าจอ","หน้าต่าง 1280 × 720","หลายจอภาพ"}, SelectedIndex=p.Display=="multi"?2:p.Display=="window"?1:0 };
            d.Field(d.fields,"การแสดงผล",display);
            var clipboard=new CheckBox { Content="แชร์คลิปบอร์ดกับเครื่องปลายทาง",IsChecked=p.Clipboard,Margin=new Thickness(0,13,0,5) };
            d.fields.Children.Add(clipboard);
            return d.Run(delegate {
                int n;
                if (!Int32.TryParse(port.Text,NumberStyles.None,CultureInfo.InvariantCulture,out n)) { port.Focus(); throw new InvalidDataException("กรอกพอร์ตเป็นตัวเลข 1 ถึง 65535"); }
                p.Name=name.Text.Trim(); p.Host=host.Text.Trim(); p.Port=n; p.Group=group.Text.Trim();
                p.User=user.Text.Trim(); p.Password=password.Password; p.Display=new[]{"full","window","multi"}[display.SelectedIndex]; p.Clipboard=clipboard.IsChecked==true;
                p.Validate();
            },name) ? p : null;
        }
        public static string Passphrase(Window owner,bool exporting) {
            var d=new Dialog(owner,exporting?"ตั้งรหัสผ่านไฟล์สำรอง":"เปิดไฟล์สำรอง",exporting?"สำรองข้อมูล":"กู้คืน",440);
            d.fields.Children.Add(new TextBlock { Text=exporting ? "ใช้รหัสนี้เมื่อกู้คืนโปรไฟล์และรหัสผ่านเครื่องบน Windows เครื่องอื่น" : "เพิ่มเฉพาะโปรไฟล์ที่ยังไม่มีในคลังนี้",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8) });
            var pass=d.Secret(exporting?"รหัสผ่านสำรอง (อย่างน้อย 12 ตัวอักษร)":"รหัสผ่านของไฟล์สำรอง","");
            var confirm=exporting?d.Secret("ยืนยันรหัสผ่านสำรอง",""):null;
            return d.Run(delegate {
                if (pass.Password.Length==0 || (exporting && pass.Password.Length<12)) throw new InvalidDataException("กรุณากรอกรหัสผ่านสำรอง"+(exporting?"อย่างน้อย 12 ตัวอักษร":""));
                if (exporting && pass.Password!=confirm.Password) throw new InvalidDataException("รหัสผ่านทั้งสองช่องไม่ตรงกัน");
            },pass) ? pass.Password : null;
        }
    }
}
