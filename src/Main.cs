using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace QingZip {
    static class Program {
        [STAThread] static void Main(string[] args) {
            try {Run(args);}catch(Exception ex){
                try{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"last-crash.txt"),ex.ToString());}catch{}
                if(args.Length==0||!args[0].StartsWith("--"))MessageBox.Show(ex.Message,"轻ZIP",MessageBoxButton.OK,MessageBoxImage.Error);
                Environment.ExitCode=1;
            }
        }
        static void Run(string[] args) {
            var app = new Application();
            app.DispatcherUnhandledException += (s,e) => { MessageBox.Show(e.Exception.Message,"轻ZIP",MessageBoxButton.OK,MessageBoxImage.Warning); e.Handled=true; };
            var window = new MainWindow();
            bool automation = args.Length>1 && args[0].StartsWith("--",StringComparison.Ordinal);
            if (automation) {
                window.ShowInTaskbar=false; window.WindowStartupLocation=WindowStartupLocation.Manual; window.Left=-20000; window.Top=-20000;
                window.Loaded += async (s,e) => {
                    try {
                        if(args[0]=="--self-test") await window.SelfTest(args);
                        else {
                            if(args.Length>2) { if(args[0].StartsWith("--preview-create")) window.LoadCreatePreview(args[2]); else window.LoadPreview(args[2]); }
                            if(args[0]=="--preview-small"||args[0]=="--preview-create-small") { window.Width=960;window.Height=680; }
                            if(args[0]=="--preview-motion") {
                                Directory.CreateDirectory(args[1]); window.PlayEntrance();
                                foreach(int frame in Enumerable.Range(0,16)) { await Task.Delay(50); window.SavePreview(Path.Combine(args[1],frame.ToString("00")+".png")); }
                                window.BrowseNav.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});
                                await Task.Delay(180);window.SavePreview(Path.Combine(args[1],"hover.png"));
                            } else { await Task.Delay(650);window.SavePreview(args[1]); }
                        }
                    } catch(Exception ex) { File.WriteAllText(args[1]+".error.txt",ex.ToString()); Environment.ExitCode=1; }
                    window.Close();
                };
            } else if(args.Length>0) window.Loaded += async(s,e) => await window.OpenArchive(args[0]);
            app.Run(window);
        }
    }
    public sealed class DisplayRow {
        public string Name {get;set;} public string Type {get;set;} public string Size {get;set;} public string Packed {get;set;}
        public string Glyph {get;set;} public Brush Tint {get;set;} public Brush IconColor {get;set;}
    }
    public sealed class MainWindow : Window {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
        [StructLayout(LayoutKind.Sequential)] struct Margins {public int Left,Right,Top,Bottom;}
        readonly Grid surface,page;
        readonly ListBox fileList;
        readonly Grid emptyGrid;
        readonly List<string> sources = new List<string>();
        readonly ComboBox encoding,compression;
        readonly TextBlock title,subtitle,status,countValue,sizeValue,packedValue,countLabel,sizeLabel,packedLabel;
        readonly Border progressTrack,progressFill;
        readonly Button primary,cancel,resultButton,openButton,filesButton,folderButton,removeButton,createNav;
        public readonly Button BrowseNav;
        readonly bool motion = SystemParameters.ClientAreaAnimation;
        bool busy,createMode,closeAfterCancel;
        string archivePath,lastOutput;
        ZipCatalog catalog;
        CancellationTokenSource cancellation;
        public int AcrylicResult=-1;
        static readonly Brush FolderTint=ColorBrush("#E8E0F4"),ImageTint=ColorBrush("#DBEEE8"),FileTint=ColorBrush("#DFEAF3");
        static readonly Brush FolderInk=ColorBrush("#8A76A4"),ImageInk=ColorBrush("#519585"),FileInk=ColorBrush("#6C93B1");
        public MainWindow() {
            Title="轻ZIP · 毛玻璃版";Width=1160;Height=800;MinWidth=960;MinHeight=680;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            FontFamily=new FontFamily("Microsoft YaHei UI");FontSize=13;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
            Background=Brushes.Transparent;
            WindowChrome.SetWindowChrome(this,new WindowChrome {CaptionHeight=52,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(-1),UseAeroCaptionButtons=false});
            string xaml;
            using(var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("QingZip.MainView.xaml"))) xaml=reader.ReadToEnd();
            if(!motion) xaml=Regex.Replace(xaml,"Duration=\"0:0:[0-9.]+\"","Duration=\"0:0:0\"");
            surface=(Grid)XamlReader.Parse(xaml);Content=surface;
            page=Find<Grid>("Page");fileList=Find<ListBox>("FileList");emptyGrid=Find<Grid>("EmptyPanel");
            title=Find<TextBlock>("PageTitle");subtitle=Find<TextBlock>("Subtitle");status=Find<TextBlock>("Status");
            countValue=Find<TextBlock>("CountValue");sizeValue=Find<TextBlock>("SizeValue");packedValue=Find<TextBlock>("PackedValue");
            countLabel=Find<TextBlock>("CountLabel");sizeLabel=Find<TextBlock>("SizeLabel");packedLabel=Find<TextBlock>("PackedLabel");
            encoding=Find<ComboBox>("EncodingBox");compression=Find<ComboBox>("CompressionBox");
            progressTrack=Find<Border>("ProgressTrack");progressFill=Find<Border>("ProgressFill");
            primary=Find<Button>("PrimaryButton");cancel=Find<Button>("CancelButton");resultButton=Find<Button>("ResultButton");
            openButton=Find<Button>("OpenButton");filesButton=Find<Button>("FilesButton");folderButton=Find<Button>("FolderButton");removeButton=Find<Button>("RemoveButton");
            BrowseNav=Find<Button>("BrowseNav");createNav=Find<Button>("CreateNav");
            foreach(string name in new[]{"MinimizeButton","MaximizeButton","CloseButton"}) WindowChrome.SetIsHitTestVisibleInChrome(Find<Button>(name),true);
            Find<Button>("MinimizeButton").Click+=(s,e)=>SystemCommands.MinimizeWindow(this);
            Find<Button>("MaximizeButton").Click+=(s,e)=>{if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);};
            Find<Button>("CloseButton").Click+=(s,e)=>Close();
            StateChanged+=(s,e)=>surface.Margin=WindowState==WindowState.Maximized?new Thickness(7):new Thickness(0);
            BrowseNav.Click+=(s,e)=>SwitchMode(false,true);createNav.Click+=(s,e)=>SwitchMode(true,true);
            openButton.Click+=(s,e)=>ChooseArchive();filesButton.Click+=(s,e)=>ChooseFiles();folderButton.Click+=(s,e)=>ChooseFolder();removeButton.Click+=(s,e)=>RemoveSources();
            primary.Click+=async(s,e)=>{if(createMode)await CreateZip();else await ExtractZip();};cancel.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();};
            resultButton.Click+=(s,e)=>OpenResult();
            encoding.SelectedIndex=0;compression.SelectedIndex=0;
            encoding.SelectionChanged+=async(s,e)=>{if(archivePath!=null&&!busy)await OpenArchive(archivePath);};
            fileList.SelectionChanged+=(s,e)=>removeButton.IsEnabled=!busy && createMode && fileList.SelectedItems.Count>0;
            AllowDrop=true;PreviewDragOver+=(s,e)=>{e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};
            Drop+=(s,e)=>{if(busy||!e.Data.GetDataPresent(DataFormats.FileDrop))return;HandleDrop((string[])e.Data.GetData(DataFormats.FileDrop));e.Handled=true;};
            PreviewKeyDown+=async(s,e)=>{
                if(busy){if(e.Key==Key.Escape){cancellation.Cancel();e.Handled=true;}return;}
                if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;
                if(e.Key==Key.O){ChooseArchive();e.Handled=true;}
                if(e.Key==Key.N){SwitchMode(true,true);e.Handled=true;}
                if(e.Key==Key.E){await ExtractZip();e.Handled=true;}
            };
            Closing+=(s,e)=>{if(busy){e.Cancel=true;closeAfterCancel=true;cancellation.Cancel();status.Text="正在取消任务，随后关闭…";}};
            SourceInitialized+=(s,e)=>EnableAcrylic();
            Loaded+=(s,e)=>{PlayEntrance();FloatEmptyIcon();};
            SwitchMode(false,false);
        }
        T Find<T>(string name) where T:FrameworkElement {return (T)surface.FindName(name);}
        static Brush ColorBrush(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
        void EnableAcrylic() {
            try {
                var hwnd=new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor=Colors.Transparent;
                int round=2;DwmSetWindowAttribute(hwnd,33,ref round,4);
                int dark=0;DwmSetWindowAttribute(hwnd,20,ref dark,4);
                int backdrop=3;AcrylicResult=DwmSetWindowAttribute(hwnd,38,ref backdrop,4);
                var margins=new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1};
                if(AcrylicResult==0) DwmExtendFrameIntoClientArea(hwnd,ref margins);
                else {Background=ColorBrush("#F3F7FB");WindowChrome.GetWindowChrome(this).GlassFrameThickness=new Thickness(0);}
            }catch(DllNotFoundException){Background=ColorBrush("#F3F7FB");}
        }
        static DoubleAnimation Ease(double from,double to,int milliseconds,int delay=0) {
            return new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(milliseconds)) {BeginTime=TimeSpan.FromMilliseconds(delay),EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop};
        }
        public void PlayEntrance() {
            if(!motion)return;
            page.Opacity=1;page.BeginAnimation(OpacityProperty,Ease(0,1,420));
            ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty,Ease(24,0,480));
            int delay=60;
            foreach(string name in new[]{"Card1","Card2","Card3"}) {
                var card=Find<Border>(name);card.RenderTransform=new TranslateTransform();
                card.BeginAnimation(OpacityProperty,Ease(0,1,340,delay));
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,Ease(15,0,400,delay));delay+=60;
            }
        }
        void FloatEmptyIcon() {
            if(!motion)return;
            var icon=Find<Grid>("EmptyIcon");
            var animation=new DoubleAnimation(0,-6,TimeSpan.FromSeconds(2.2)) {AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase {EasingMode=EasingMode.EaseInOut}};
            ((TranslateTransform)icon.RenderTransform).BeginAnimation(TranslateTransform.YProperty,animation);
        }
        void SetProgress(int percent) {
            double width=160*Math.Max(0,Math.Min(100,percent))/100.0;
            if(motion)progressFill.BeginAnimation(WidthProperty,new DoubleAnimation(width,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
            else progressFill.Width=width;
        }
        int CodePage {get{return new[]{0,65001,936,437}[Math.Max(0,encoding.SelectedIndex)];}}
        public static string HumanSize(long size){if(size<1024)return size+" B";if(size<1048576)return(size/1024d).ToString("0.0")+" KB";if(size<1073741824)return(size/1048576d).ToString("0.0")+" MB";return(size/1073741824d).ToString("0.00")+" GB";}
        static DisplayRow Row(string name,bool folder,long size,long packed,bool pending=false) {
            string ext=Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            bool image=new[]{"PNG","JPG","JPEG","GIF","WEBP","BMP"}.Contains(ext);
            return new DisplayRow {Name=name,Type=folder?"文件夹":ext.Length>0&&ext.Length<=5?ext:"文件",Size=folder?(pending?"待统计":"—"):HumanSize(size),Packed=folder||pending?"—":HumanSize(packed),Glyph=folder?"\uE8B7":image?"\uEB9F":"\uE8A5",Tint=folder?FolderTint:image?ImageTint:FileTint,IconColor=folder?FolderInk:image?ImageInk:FileInk};
        }
        void SwitchMode(bool create,bool animate) {
            if(busy)return;createMode=create;
            BrowseNav.Background=ColorBrush(create?"#00FFFFFF":"#CFFFFFFF");createNav.Background=ColorBrush(create?"#CFFFFFFF":"#00FFFFFF");
            BrowseNav.BorderBrush=ColorBrush(create?"#00FFFFFF":"#FFFFFFFF");createNav.BorderBrush=ColorBrush(create?"#FFFFFFFF":"#00FFFFFF");
            title.Text=create?"创建一个 ZIP":"浏览与解压";
            subtitle.Text=create?"把需要的文件放在一起，轻松打包分享。":archivePath==null?"打开压缩包，让里面的文件一目了然。":Path.GetFileName(archivePath)+"  ·  ZIP 压缩包";
            Find<StackPanel>("BrowseTools").Visibility=create?Visibility.Collapsed:Visibility.Visible;
            Find<StackPanel>("CreateTools").Visibility=create?Visibility.Visible:Visibility.Collapsed;
            encoding.Visibility=create?Visibility.Collapsed:Visibility.Visible;compression.Visibility=create?Visibility.Visible:Visibility.Collapsed;
            primary.Content=create?"创建 ZIP  →":"解压到文件夹  →";
            Find<TextBlock>("EmptyTitle").Text=create?"把文件拖到这里":"把 ZIP 拖到这里";
            Find<TextBlock>("EmptyHint").Text=create?"也可以添加文件或整个文件夹，保留目录结构":"或点击上方「打开 ZIP」，开始整理文件";
            cancel.Visibility=progressTrack.Visibility=Visibility.Collapsed;
            if(create)UpdateSourceRows();else UpdateArchiveRows();
            if(animate)PlayEntrance();
        }
        void UpdateRows(List<DisplayRow> rows) {
            fileList.ItemsSource=rows;emptyGrid.Visibility=rows.Count==0?Visibility.Visible:Visibility.Collapsed;
            primary.IsEnabled=!busy&&(createMode?sources.Count>0:catalog!=null);
            removeButton.IsEnabled=!busy&&createMode&&fileList.SelectedItems.Count>0;
        }
        void UpdateArchiveRows() {
            var rows=catalog==null?new List<DisplayRow>():catalog.Items.Select(x=>Row(x.Name,x.IsDirectory,x.Size,x.PackedSize)).ToList();
            countLabel.Text="包内项目";sizeLabel.Text="原始大小";packedLabel.Text="ZIP 大小";
            countValue.Text=rows.Count.ToString();sizeValue.Text=HumanSize(catalog==null?0:catalog.TotalSize);packedValue.Text=HumanSize(archivePath==null?0:new FileInfo(archivePath).Length);
            status.Text=catalog==null?"准备就绪 · 支持 ZIP / ZIP64":"已读取目录 · "+rows.Count+" 个项目";UpdateRows(rows);
        }
        void UpdateSourceRows() {
            var rows=sources.Select(p=>Row(Path.GetFileName(p),Directory.Exists(p),File.Exists(p)?new FileInfo(p).Length:0,0,true)).ToList();
            countLabel.Text="已选项目";sizeLabel.Text="打包格式";packedLabel.Text="目录结构";
            countValue.Text=sources.Count.ToString();sizeValue.Text="ZIP";packedValue.Text="保留";
            status.Text="文件夹内的文件与空目录会一起打包";UpdateRows(rows);
        }
        void ChooseArchive(){var d=new Microsoft.Win32.OpenFileDialog {Filter="ZIP 压缩包 (*.zip)|*.zip",Title="打开 ZIP",CheckFileExists=true};if(d.ShowDialog(this)==true) {var ignored=OpenArchive(d.FileName);}}
        public async Task OpenArchive(string path) {
            if(busy)return;SwitchMode(false,false);int cp=CodePage;SetBusy(true);cancel.Visibility=Visibility.Collapsed;
            status.Text="正在读取压缩包目录…";
            try {var read=await Task.Run(()=>ArchiveEngine.Inspect(path,cp));archivePath=Path.GetFullPath(path);catalog=read;subtitle.Text=Path.GetFileName(archivePath)+"  ·  ZIP 压缩包";UpdateArchiveRows();PlayEntrance();}
            catch(Exception ex){ShowError(ex);}finally{SetBusy(false);if(closeAfterCancel)Close();}
        }
        public void LoadPreview(string path){archivePath=Path.GetFullPath(path);catalog=ArchiveEngine.Inspect(path,0);SwitchMode(false,false);}
        public void LoadCreatePreview(string folder){SwitchMode(true,false);AddSources(Directory.GetFileSystemEntries(folder));}
        void ChooseFiles(){var d=new Microsoft.Win32.OpenFileDialog {Multiselect=true,Title="选择要打包的文件",Filter="所有文件 (*.*)|*.*"};if(d.ShowDialog(this)==true)AddSources(d.FileNames);}
        void ChooseFolder(){using(var d=new Forms.FolderBrowserDialog {Description="选择要打包的文件夹"})if(d.ShowDialog(new DialogOwner(this))==Forms.DialogResult.OK)AddSources(new[]{d.SelectedPath});}
        void AddSources(IEnumerable<string> paths){foreach(string p in paths.Select(Path.GetFullPath))if(!sources.Contains(p,StringComparer.OrdinalIgnoreCase)&&(File.Exists(p)||Directory.Exists(p)))sources.Add(p);UpdateSourceRows();}
        void RemoveSources(){foreach(var row in fileList.SelectedItems.Cast<DisplayRow>().ToArray()){int index=fileList.Items.IndexOf(row);if(index>=0)sources[index]=null;}sources.RemoveAll(p=>p==null);UpdateSourceRows();}
        async void HandleDrop(string[] paths){if(!createMode&&paths.Length==1&&File.Exists(paths[0])&&Path.GetExtension(paths[0]).Equals(".zip",StringComparison.OrdinalIgnoreCase))await OpenArchive(paths[0]);else{SwitchMode(true,true);AddSources(paths);}}
        void SetBusy(bool value) {
            busy=value;foreach(var b in new[]{BrowseNav,createNav,openButton,filesButton,folderButton})b.IsEnabled=!value;
            encoding.IsEnabled=compression.IsEnabled=!value;primary.IsEnabled=!value&&(createMode?sources.Count>0:catalog!=null);
            removeButton.IsEnabled=!value&&createMode&&fileList.SelectedItems.Count>0;
            cancel.Visibility=progressTrack.Visibility=value?Visibility.Visible:Visibility.Collapsed;
            if(value){if(cancellation!=null)cancellation.Dispose();cancellation=new CancellationTokenSource();progressFill.BeginAnimation(WidthProperty,null);progressFill.Width=0;resultButton.Visibility=Visibility.Collapsed;}
        }
        Action<WorkProgress> ProgressCallback() {
            var clock=Stopwatch.StartNew();long last=-100;var owner=cancellation;
            return p=>{long now=clock.ElapsedMilliseconds;if(p.Percent!=100&&now-last<80)return;last=now;if(Dispatcher.HasShutdownStarted)return;
                Dispatcher.BeginInvoke((Action)(()=>{if(busy&&ReferenceEquals(owner,cancellation)){SetProgress(p.Percent);status.Text=p.Message;}}),DispatcherPriority.Background);};
        }
        async Task<bool> RunJob(Func<CancellationToken,Action<WorkProgress>,string> work,bool silent=false) {
            SetBusy(true);bool success=false;
            try {var token=cancellation.Token;var notify=ProgressCallback();lastOutput=await Task.Run(()=>work(token,notify));success=true;SetProgress(100);status.Text="完成 · "+lastOutput;resultButton.Visibility=Visibility.Visible;
                if(motion)resultButton.BeginAnimation(OpacityProperty,Ease(0,1,320));
            }catch(OperationCanceledException){status.Text="任务已取消，未提交不完整的文件。";}
            catch(Exception ex){if(silent)throw;ShowError(ex);}finally{SetBusy(false);if(closeAfterCancel)Close();}
            return success;
        }
        async Task ExtractZip() {
            if(busy||createMode||catalog==null)return;
            using(var d=new Forms.FolderBrowserDialog {Description="选择解压位置。轻ZIP 会在里面创建新的文件夹。"})if(d.ShowDialog(new DialogOwner(this))==Forms.DialogResult.OK){
                string name=Path.GetFileNameWithoutExtension(archivePath);foreach(char c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');if(string.IsNullOrWhiteSpace(name))name="ZIP";
                string root=Path.Combine(d.SelectedPath,name+"-解压"),dest=root;int n=2;while(Directory.Exists(dest)||File.Exists(dest))dest=root+" ("+(n++)+")";
                int cp=CodePage;string archive=archivePath;await RunJob((token,p)=>ArchiveEngine.Extract(archive,dest,cp,token,p));
            }
        }
        async Task CreateZip() {
            if(busy||!createMode||sources.Count==0)return;
            var d=new Microsoft.Win32.SaveFileDialog {Filter="ZIP 压缩包 (*.zip)|*.zip",DefaultExt=".zip",AddExtension=true,OverwritePrompt=true,FileName=sources.Count==1?Path.GetFileNameWithoutExtension(sources[0])+".zip":"我的文件.zip",Title="保存 ZIP"};
            if(d.ShowDialog(this)==true){string target=d.FileName;string[] input=sources.ToArray();bool store=compression.SelectedIndex==1;await RunJob((token,p)=>{ArchiveEngine.Create(input,target,store,token,p);return target;});}
        }
        void OpenResult(){if(lastOutput==null)return;try{Process.Start("explorer.exe",Directory.Exists(lastOutput)?"\""+lastOutput+"\"":"/select,\""+lastOutput+"\"");}catch(Exception ex){ShowError(ex);}}
        void ShowError(Exception ex){status.Text="操作未完成";MessageBox.Show(this,ex.Message,"轻ZIP",MessageBoxButton.OK,MessageBoxImage.Warning);}
        sealed class DialogOwner:Forms.IWin32Window {readonly Window window;public DialogOwner(Window w){window=w;}public IntPtr Handle{get{return new WindowInteropHelper(window).Handle;}}}
        public void SavePreview(string path) {
            surface.UpdateLayout();int w=(int)Math.Ceiling(surface.ActualWidth),h=(int)Math.Ceiling(surface.ActualHeight);
            var brush=new VisualBrush(surface){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,w,h),Stretch=Stretch.Fill};
            var visual=new DrawingVisual();using(var context=visual.RenderOpen()){context.DrawRectangle(ColorBrush("#F3F7FB"),null,new Rect(0,0,w,h));context.DrawRectangle(brush,null,new Rect(0,0,w,h));}
            var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);
        }
        public async Task SelfTest(string[] args) {
            // Exercise the real UI state and task orchestration, with test-owned paths instead of modal dialogs.
            var checks=new List<string>();Action<bool,string> check=(ok,name)=>{if(!ok)throw new InvalidOperationException("UI test: "+name);checks.Add(name);};
            await Task.Delay(600);
            check(!createMode&&!primary.IsEnabled&&emptyGrid.Visibility==Visibility.Visible,"empty state");
            if(motion){BrowseNav.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});await Task.Delay(250);
                var shell=(Border)BrowseNav.Template.FindName("Shell",BrowseNav);var transforms=(TransformGroup)shell.RenderTransform;
                check(((ScaleTransform)transforms.Children[0]).ScaleX>1.01&&((TranslateTransform)transforms.Children[1]).Y< -1,"animated hover scale and lift");
                BrowseNav.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseLeaveEvent});await Task.Delay(300);
                check(Math.Abs(((ScaleTransform)transforms.Children[0]).ScaleX-1)<0.001,"hover animation restores");}
            createNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(650);
            check(createMode&&encoding.Visibility==Visibility.Collapsed&&compression.Visibility==Visibility.Visible,"create navigation");
            compression.IsDropDownOpen=true;await Task.Delay(80);check(compression.IsDropDownOpen,"custom dropdown opens");compression.IsDropDownOpen=false;
            LoadCreatePreview(args[3]);int original=sources.Count;check(original>0&&primary.IsEnabled,"add source and enable primary");
            fileList.SelectedIndex=0;removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(sources.Count==original-1,"remove selected source");
            AddSources(Directory.GetFileSystemEntries(args[3]));check(sources.Count==original,"add source without duplicates");
            string target=Path.Combine(args[4],"ui-created.zip"),extract=Path.Combine(args[4],"ui-extracted");
            Directory.CreateDirectory(args[4]);var input=sources.ToArray();
            var job=RunJob((token,p)=>{Thread.Sleep(250);ArchiveEngine.Create(input,target,false,token,p);return target;},true);
            check(busy&&!primary.IsEnabled&&!BrowseNav.IsEnabled&&cancel.Visibility==Visibility.Visible,"busy controls");await job;
            check(!busy&&File.Exists(target)&&resultButton.Visibility==Visibility.Visible,"create completion");
            await OpenArchive(args[2]);check(!createMode&&catalog.Items.Count==fileList.Items.Count&&primary.IsEnabled,"async archive load");
            string archive=archivePath;await RunJob((token,p)=>ArchiveEngine.Extract(archive,extract,0,token,p),true);
            check(Directory.Exists(extract)&&!busy&&resultButton.Visibility==Visibility.Visible,"extract completion");
            var cancelJob=RunJob((token,p)=>{while(true){token.ThrowIfCancellationRequested();Thread.Sleep(15);}},true);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await cancelJob;
            check(!busy&&status.Text.Contains("取消")&&resultButton.Visibility==Visibility.Collapsed,"cancel task via button");
            SwitchMode(true,false);Width=960;Height=680;await Task.Delay(180);surface.UpdateLayout();check(Find<StackPanel>("CreateTools").ActualWidth+170<page.ActualWidth,"minimum window toolbar fits");
            string json="{\"passed\":"+checks.Count+",\"acrylic_hresult\":"+AcrylicResult+",\"checks\":["+string.Join(",",checks.Select(x=>"\""+x+"\""))+"],\"created_zip\":\""+target.Replace("\\","\\\\")+"\",\"extracted_folder\":\""+extract.Replace("\\","\\\\")+"\"}";
            File.WriteAllText(args[1],json);
        }
    }
}
