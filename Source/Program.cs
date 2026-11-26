using System.IO;
using Path = System.IO.Path;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Globalization;
namespace NagoyaConsole;
public static class Program { [STAThread] public static void Main() { new Application().Run(new ConsoleWindow()); } }
public sealed class Midi : IDisposable {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Caps { public ushort mid,pid; public uint version; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string name; public ushort technology,voices,notes,channelMask; public uint support; }
 [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
 [DllImport("winmm.dll",CharSet=CharSet.Unicode)] static extern uint midiOutGetDevCapsW(UIntPtr id,out Caps caps,uint size);
 [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr h,uint id,IntPtr cb,IntPtr instance,uint flags);
 [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr h,uint msg);
 [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr h);
 IntPtr handle; public bool Connected=>handle!=IntPtr.Zero;
 public static List<(uint Id,string Name)> Devices(){var list=new List<(uint,string)>();for(uint i=0;i<midiOutGetNumDevs();i++)if(midiOutGetDevCapsW((UIntPtr)i,out var c,(uint)Marshal.SizeOf<Caps>())==0)list.Add((i,c.name));return list;}
 public bool Open(uint id){Dispose();if(midiOutOpen(out handle,id,IntPtr.Zero,IntPtr.Zero,0)==0)return true;handle=IntPtr.Zero;return false;}
 public bool CC(int ch,int id,int v)=>Short(0xB0,ch,id,v);
 public bool Short(int type,int ch,int id,int v)=>Connected&&midiOutShortMsg(handle,(uint)(type|ch|(id<<8)|(v<<16)))==0;
 public void Dispose(){if(Connected){midiOutClose(handle);handle=IntPtr.Zero;}}
}
public class PresetData { public string Selected {get;set;}="標準"; public Dictionary<string,string[]> Presets {get;set;}=new(); }
public class Settings { public string Device {get;set;}="Nagoya Console MIDI"; public bool AutoConnect {get;set;}=true; public double Width {get;set;}=1280; public double Height {get;set;}=800; }
public static class PanelTint {
 public static Brush Get(double brightness,Color target){double t=Math.Clamp(brightness,0,1);byte Mix(byte channel)=>(byte)Math.Round(127.5+(channel-127.5)*t);var b=new SolidColorBrush(Color.FromRgb(Mix(target.R),Mix(target.G),Mix(target.B)));b.Freeze();return b;}
 public static Brush State(double brightness,int state)=>Get(brightness,state==2?Colors.Lime:state==1?Color.FromRgb(255,128,0):Colors.Red);
}
public static class DialDeadZone {
 // Same normalized dead zone as CenterConsoleController and IntegratedSystem.
 public const double Lower=0.49, Upper=0.51;
 public const int NeutralCode=512;
 public static int State(double v)=>v<=Lower?-1:v>=Upper?1:0;
 public static int Encode(double v){int state=State(v);if(state==0)return NeutralCode;int q=(int)Math.Round(Math.Clamp(v,0,1)*1023);return state<0?Math.Min(q,501):Math.Max(q,522);}
}
public record Knob(int Id,string Label,int Kind); // 0 volume, 1 +/- selector, 2 binary, 3 four-state, 4 dial
public sealed partial class ConsoleWindow:Window {
 static Brush B(string x)=>(Brush)new BrushConverter().ConvertFromString(x)!;
 static readonly Brush Silver=PaintedMetal.Brush, Black=B("#080809"), Red=B("#FF645A"), Green=B("#59E878"), Orange=B("#FFAD37");
 readonly string data=Path.Combine(AppContext.BaseDirectory,"Data"); readonly Midi midi=new(); readonly double[] values=new double[40]; readonly int[] lastDialCode=Enumerable.Repeat(-1,40).ToArray(); readonly int[] macros=new int[61]; readonly Dictionary<int,KnobVisual> visuals=new(); readonly Dictionary<int,Button> macroButtons=new(); readonly HashSet<int> held=new(); readonly Grid content=new(); readonly TextBlock midiStatus=new(){TextWrapping=TextWrapping.Wrap,FontSize=13}, feedbackStatus=new(){TextWrapping=TextWrapping.Wrap,FontSize=13}; Button? connectionButton, fullscreenButton; readonly TextBox shortLog=new(){IsReadOnly=true,Background=Black,Foreground=Green,FontSize=11,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,TextWrapping=TextWrapping.Wrap}; readonly TextBlock status=new(){Foreground=Brushes.LightGray}; readonly TextBox log=new(){IsReadOnly=true,AcceptsReturn=true,Background=Black,Foreground=Green,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}; readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(100)}; readonly List<Button> tabs=new(); readonly List<(uint Id,string Name)> devices=new(); PresetData presets; Settings settings; string[] labels; bool full; WindowState oldState; WindowStyle oldStyle; ResizeMode oldResize; string? logPath; long logOffset; string pending=""; DateTime lastFeedback=DateTime.MinValue; int tab;
 InfoFrame? infoFrame; DateTime lastInfoFeedback=DateTime.MinValue; InfoDisplay? infoDisplay;
 bool InfoLive=>infoFrame!=null&&DateTime.UtcNow-lastInfoFeedback<TimeSpan.FromSeconds(4);
 void RefreshInfo(){infoDisplay?.SetState(InfoLive?infoFrame:null,values[33]);}
 void ReadInfo(string payload){if(!InfoFrame.TryParse(payload,out var frame))return;infoFrame=frame;lastInfoFeedback=DateTime.UtcNow;RefreshInfo();}
 static readonly string[] DefaultLabels= new[]{"原点復帰","再生","一時停止","サーチ","またたき","上昇","下降","停止","夕／朝","南／北"}.Concat(Enumerable.Range(1,51).Select(i=>"Macro "+i)).ToArray();
 static readonly Knob[] Knobs={new(14,"時圏回転",1),new(37,"時圏",0),new(19,"年周12分",1),new(28,"子午線",0),new(38,"黄道星座",0),new(5,"日周超低速",1),new(34,"惑星",0),new(2,"風速",4),new(15,"予備",2),new(10,"歳差円",2),new(17,"年周4分",1),new(7,"赤道",2),new(25,"恒星",0),new(4,"日周12分",1),new(36,"太陽",0),new(22,"雲",0),new(9,"歳差",1),new(11,"歳差黄赤道",2),new(18,"年周11秒",1),new(6,"黄道",2),new(33,"パネル照明",0),new(3,"日周4分",1),new(30,"月",0),new(20,"全天星座",0),new(8,"緯度",1),new(27,"地平高度回転",4),new(26,"地平高度",0),new(32,"夜天光",0),new(21,"矢印",0),new(24,"方位灯",0),new(13,"回転方位灯",2),new(31,"朝夕焼",0),new(1,"架台方位",4),new(23,"昼光",0),new(29,"天の川",0),new(35,"モニター",0),new(12,"基準LED",3)};
 static readonly int[] ConsoleLayout={14,37,19,28,38,5,34,2,15,10,17,7,25,4,36,22,9,11,18,6,33,3,30,20,8,27,26,-1,21,24,13,31,12,1,29,23,32,35};
 T Load<T>(string name,T fallback) where T:class {try{return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(data,name)))??fallback;}catch{return fallback;}}
 bool Save<T>(string name,T value){try{WriteSettingsFile(Path.Combine(data,name),value);return true;}catch(Exception e){Log("保存失敗: "+e.Message);return false;}}
 public ConsoleWindow(){Directory.CreateDirectory(data);settings=Load("settings.json",new Settings());presets=Load("macro.json",new PresetData());if(presets.Presets.Count==0)presets.Presets["標準"]=(string[])DefaultLabels.Clone();if(!presets.Presets.ContainsKey(presets.Selected))presets.Selected=presets.Presets.Keys.First();labels=Normalize(presets.Presets[presets.Selected]);Save("macro.json",presets);Title="NagoyaPlanetConsole — UNIVERSARIUM IX";Width=Math.Clamp(settings.Width,640,2400);Height=Math.Clamp(settings.Height,400,1600);MinWidth=640;MinHeight=400;Background=B("#292A2D");Foreground=Brushes.White;FontFamily=new FontFamily("Yu Gothic UI"); foreach(var k in Knobs){values[k.Id]=k.Kind==4?.5:k.Kind==3?-1:0;if(k.Kind==4)lastDialCode[k.Id]=DialDeadZone.NeutralCode;}
 var root=new Grid{Margin=new Thickness(10)};root.RowDefinitions.Add(new(){Height=new GridLength(48)});root.RowDefinitions.Add(new(){Height=new GridLength(58)});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=new GridLength(28)});Content=root;
 var head=new DockPanel();var fs=Btn("全画面",()=>ToggleFull());fullscreenButton=fs;fs.Width=100;fs.Margin=new Thickness(8,3,3,3);DockPanel.SetDock(fs,Dock.Right);head.Children.Add(fs);connectionButton=Btn("接続",ToggleConnection);connectionButton.Width=96;connectionButton.Margin=new Thickness(3,3,8,3);DockPanel.SetDock(connectionButton,Dock.Right);head.Children.Add(connectionButton);head.Children.Add(new TextBlock{Text="UNIVERSARIUM IX  /  PLANETARIUM CONTROL SYSTEM",FontSize=19,VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.LightGray});root.Children.Add(head);
 var bar=new UniformGrid{Columns=6};Grid.SetRow(bar,1);root.Children.Add(bar);foreach(var n in new[]{"操作卓 / CONSOLE","マクロ / MACRO","設定 / SETUP","ソフトMIDI","外部MIDI","通信 / MONITOR"}){int i=tabs.Count;var b=Btn(n,()=>Show(i));tabs.Add(b);bar.Children.Add(b);}var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(4,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star),MinWidth=125});body.Children.Add(content);var side=new DockPanel{Background=Black,Margin=new Thickness(6,4,0,4),LastChildFill=true};var sideHead=new StackPanel{Margin=new Thickness(8)};sideHead.Children.Add(new TextBlock{Text="COMMUNICATION",Foreground=Red,FontSize=12});sideHead.Children.Add(midiStatus);sideHead.Children.Add(feedbackStatus);sideHead.Children.Add(Btn("ログ消去",ClearLogs));DockPanel.SetDock(sideHead,Dock.Top);side.Children.Add(sideHead);side.Children.Add(shortLog);Grid.SetColumn(side,1);body.Children.Add(side);Grid.SetRow(body,2);root.Children.Add(body);Grid.SetRow(status,3);root.Children.Add(status);Show(0);PreviewKeyDown+=(s,e)=>{if(e.Key==Key.F11||(e.Key==Key.Escape&&full)){ToggleFull();e.Handled=true;}};Deactivated+=(s,e)=>ReleaseAll();Closing+=(s,e)=>{ReleaseAll();CloseExternal();externalTimer.Stop();timer.Stop();midi.Dispose();if(!full&&WindowState==WindowState.Normal){settings.Width=Width;settings.Height=Height;}Save("settings.json",settings);};timer.Tick+=(s,e)=>{ReadFeedback();UpdateStatus();ExternalFeedback();};timer.Start();if(settings.AutoConnect)Connect();else UpdateStatus();InitExternal(); }
 static string[] Normalize(string[] a)=>DefaultLabels.Select((v,i)=>i<a.Length&&!string.IsNullOrWhiteSpace(a[i])?a[i]:v).ToArray();
 Button Btn(string text,Action action){var b=new Button{Content=text,Background=Black,Foreground=Red,BorderBrush=B("#66666A"),BorderThickness=new Thickness(3),Margin=new Thickness(3),Padding=new Thickness(8),FontSize=15};var border=new FrameworkElementFactory(typeof(Border));border.Name="ButtonFrame";border.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});border.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding("BorderBrush"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});border.SetBinding(Border.BorderThicknessProperty,new System.Windows.Data.Binding("BorderThickness"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.Name="ButtonText";presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);var template=new ControlTemplate(typeof(Button)){VisualTree=border};var pressed=new Trigger{Property=Button.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(Border.BorderBrushProperty,Orange,"ButtonFrame"));pressed.Setters.Add(new Setter(Border.BackgroundProperty,B("#392413"),"ButtonFrame"));pressed.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty,Orange,"ButtonText"));template.Triggers.Add(pressed);b.Template=template;Stylus.SetIsPressAndHoldEnabled(b,false);Stylus.SetIsFlicksEnabled(b,false);b.Click+=(s,e)=>action();return b;}
 FrameworkElement? externalPageCache;
 void Show(int i){bool enteringSetup=i==2&&tab!=2;CancelLearn();PauseBatch();if(i==2||i==3||i==4){ReleaseExternal();ResetExternalTracking();}if(i!=2)CancelLabelRequest();infoDisplay=null;ReleaseAll();visuals.Clear();macroButtons.Clear();tab=i;content.Children.Clear();foreach(var b in tabs)SetSelected(b,tabs.IndexOf(b)==i);content.Children.Add(i==0?ConsolePage():i==1?MacroPage():i==2?SettingsPage():i==3?SettingsPage(true):i==4?(externalPageCache??=ExternalPage()):MonitorPage());if(enteringSetup)RequestLabels();}
 void SetSelected(Button b,bool selected){b.Foreground=selected?Orange:Red;b.BorderBrush=selected?Orange:B("#66666A");}
 FrameworkElement Scale(UIElement e)=>new Viewbox{Stretch=Stretch.Uniform,Child=e};
 // Independent metal plates; screws are decorative and never intercept input.
 Grid Plate(Grid inner, double w, double h){var plate=new Grid{Width=w,Height=h};plate.Children.Add(new Border{Background=Silver,CornerRadius=new CornerRadius(5),IsHitTestVisible=false});inner.Margin=new Thickness(12);plate.Children.Add(inner);foreach(var x in new[]{HorizontalAlignment.Left,HorizontalAlignment.Right})foreach(var y in new[]{VerticalAlignment.Top,VerticalAlignment.Bottom})plate.Children.Add(new ScrewHead{Width=12,Height=12,Margin=new Thickness(2),HorizontalAlignment=x,VerticalAlignment=y,IsHitTestVisible=false});return plate;}
 static void Place(Canvas c,UIElement e,double x,double y){Canvas.SetLeft(e,x);Canvas.SetTop(e,y);c.Children.Add(e);}
 FrameworkElement ConsolePage(bool edit=false){if(!edit){visuals.Clear();macroButtons.Clear();}var canvas=new Canvas{Width=1120,Height=edit?620:778};if(!edit){var header=new Canvas{Width=1120,Height=150,Background=B("#292A2D")};infoDisplay=new InfoDisplay{Width=810,Height=150,IsHitTestVisible=false};Place(header,infoDisplay,0,0);RefreshInfo();foreach(var item in new[]{(5,820,10),(6,820,78),(7,968,78)}){var b=MacroButton(item.Item1);b.Width=144;b.Height=58;Place(header,b,item.Item2,item.Item3);}Place(canvas,header,0,0);}for(int half=0;half<2;half++){var inner=new Grid();var panel=new Canvas();inner.Children.Add(panel);for(int row=0;row<5;row++)for(int col=0;col<4;col++){int slot=row*8+half*4+col;if(slot>=ConsoleLayout.Length)continue;int id=ConsoleLayout[slot];if(id<0)continue;var k=Knobs.First(x=>x.Id==id);var v=new KnobVisual(k,values[k.Id],x=>{if(!edit){pickupAcquired.Remove(k.Id);pickupPrevious.Remove(k.Id);SetKnob(k,x);}}){Width=126,Height=row==4?104:113,PanelBrightness=values[33],IsHitTestVisible=!edit};if(!edit)visuals[k.Id]=v;Place(panel,v,col*132,row*113);}
 if(half==0){var origin=MacroButton(0,edit);origin.Width=94;origin.Height=28;Place(panel,origin,148,559);}else{var north=MacroButton(9,edit);north.Width=126;north.Height=40;Place(panel,north,264,452);var evening=MacroButton(8,edit);evening.Width=126;evening.Height=40;Place(panel,evening,396,452);var blink=MacroButton(4,edit);blink.Width=126;blink.Height=40;Place(panel,blink,264,498);var reset=Btn("RESET",ResetConsole);reset.Width=86;reset.Height=36;reset.IsEnabled=!edit;Place(panel,reset,436,550);}
 Place(canvas,Plate(inner,556,620),half*564,edit?0:158);}return Scale(canvas);}
 void ResetConsole(){if(MessageBox.Show("操作卓をリセットしますか？","RESET",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;ReleaseAll();ReleaseExternal();ResetExternalTracking();if(midi.Connected){Send(5,0,1);return;}foreach(var k in Knobs){values[k.Id]=k.Kind==4?.5:k.Kind==3?-1:0;if(k.Kind==4)lastDialCode[k.Id]=DialDeadZone.NeutralCode;if(visuals.TryGetValue(k.Id,out var visual))visual.SetRemote(values[k.Id]);}Array.Clear(macros);RefreshPanelLighting();Log("LOCAL RESET");}
 void SetKnob(Knob k,double v){values[k.Id]=v;if(k.Id==33)RefreshPanelLighting();if(k.Kind==0){int q=(int)Math.Round(v*1023);Send(0,k.Id,q&127);Send(1,k.Id,q>>7);}else if(k.Kind==4){int q=DialDeadZone.Encode(v);if(lastDialCode[k.Id]==q)return;lastDialCode[k.Id]=q;Send(0,k.Id,q&127);Send(1,k.Id,q>>7);}else Send(2,k.Id,(int)v+(k.Kind==1||k.Kind==3?1:0));}
 void Send(int ch,int id,int v){bool ok=midi.CC(ch,id,v);Log($"{(ok?"TX":"LOCAL")} CC {ch+1} {id} {v}");}
 Button MacroButton(int id,bool edit=false){var b=Btn(labels[id],()=>{});if(edit)b.Click+=(s,e)=>{EditLabel(id);b.Content=labels[id];};b.FontSize=14;b.Foreground=PanelTint.State(values[33],macros[id]);if(!edit){macroButtons[id]=b;RefreshMacro(id);b.PreviewMouseLeftButtonDown+=(s,e)=>{if(e.StylusDevice!=null)return;Press(id);b.CaptureMouse();e.Handled=true;};b.PreviewMouseLeftButtonUp+=(s,e)=>{if(e.StylusDevice!=null)return;Release(id);b.ReleaseMouseCapture();e.Handled=true;};b.LostMouseCapture+=(s,e)=>Release(id);b.PreviewTouchDown+=(s,e)=>{Press(id);b.CaptureTouch(e.TouchDevice);e.Handled=true;};b.PreviewTouchUp+=(s,e)=>{Release(id);b.ReleaseTouchCapture(e.TouchDevice);e.Handled=true;};b.LostTouchCapture+=(s,e)=>Release(id);b.PreviewStylusDown+=(s,e)=>{if(e.StylusDevice.TabletDevice.Type==TabletDeviceType.Touch)return;Press(id);b.CaptureStylus();e.Handled=true;};b.PreviewStylusUp+=(s,e)=>{if(e.StylusDevice.TabletDevice.Type==TabletDeviceType.Touch)return;Release(id);b.ReleaseStylusCapture();e.Handled=true;};b.LostStylusCapture+=(s,e)=>Release(id);}return b;}
 void Press(int id){if(!held.Add(id))return;if(!externalHeld.Contains(id))Send(4,id,1);if(!FeedbackLive)macros[id]=1;RefreshMacro(id);}
 void Release(int id){if(!held.Remove(id))return;if(!externalHeld.Contains(id))Send(4,id,0);if(!FeedbackLive)macros[id]=0;RefreshMacro(id);}
 void ReleaseAll(){foreach(int id in held.ToArray())Release(id);}
 void RefreshMacro(int id){if(macroButtons.TryGetValue(id,out var b)){bool pressed=held.Contains(id)||externalHeld.Contains(id);b.Foreground=pressed?Orange:PanelTint.State(values[33],macros[id]);b.BorderBrush=pressed?Orange:B("#66666A");}}
 void RefreshPanelLighting(){RefreshInfo();foreach(var v in visuals.Values)v.PanelBrightness=values[33];for(int i=0;i<61;i++)RefreshMacro(i);}
 FrameworkElement MacroPage(bool edit=false){if(!edit)macroButtons.Clear();
 // Fixed nominal sizes; uniform downscaling only, never stretch individual buttons.
 var canvas=new Canvas{Width=1120,Height=526,Background=B("#292A2D")};
 var left=new Grid();var lc=new Canvas();left.Children.Add(lc);
 for(int i=0;i<20;i++){var b=MacroButton(i+10,edit);b.Width=94;b.Height=52;Place(lc,b,2+(i%5)*96,15+(i/5+(i>=10?1:0))*72);}
 Place(canvas,Plate(left,505,404),0,0);
 var right=new Grid();var rc=new Canvas();right.Children.Add(rc);
 for(int i=0;i<30;i++){var b=MacroButton(i+30,edit);b.Width=94;b.Height=52;Place(rc,b,3+(i%6)*97,15+(i/6)*72);}
 Place(canvas,Plate(right,609,404),511,0);
 var bottom=new Grid();var bc=new Canvas();bottom.Children.Add(bc);int col=0;
 foreach(int id in new[]{1,2,3,60}){var b=MacroButton(id,edit);b.Width=158;b.Height=52;Place(bc,b,9+col++*188,20);}
 Place(canvas,Plate(bottom,1120,114),0,412);
 return new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=canvas,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};}
 void EditLabel(int id){var w=new Window{Owner=this,Title="ラベル編集",Width=380,Height=180,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=B("#292A2D"),Foreground=Brushes.White};var p=new StackPanel{Margin=new Thickness(12)};var t=new TextBox{Text=labels[id],FontSize=18};p.Children.Add(t);p.Children.Add(Btn("適用",()=>{labels[id]=t.Text;w.DialogResult=true;}));w.Content=p;w.ShowDialog();}
 FrameworkElement SettingsPage(bool midiPage=false){
 var p=new StackPanel{Margin=new Thickness(12)};
 StackPanel Section(string title,string detail){var inner=new StackPanel{Margin=new Thickness(14)};inner.Children.Add(new TextBlock{Text=title,Foreground=Red,FontSize=19,FontWeight=FontWeights.SemiBold});inner.Children.Add(new TextBlock{Text=detail,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,12)});p.Children.Add(new Border{Background=B("#202124"),BorderBrush=B("#66666A"),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,12),Child=inner});return inner;}
 void Label(Panel parent,string text){parent.Children.Add(new TextBlock{Text=text,Foreground=Brushes.LightGray,FontSize=14,Margin=new Thickness(3,8,3,5)});}
 TextBox Field(string text){return new TextBox{Text=text,Background=Black,Foreground=Brushes.White,BorderBrush=B("#66666A"),BorderThickness=new Thickness(1),FontSize=16,Padding=new Thickness(8),MinHeight=38};}
 ListBox Choices(IEnumerable<string> items,string selected){var box=new ListBox{ItemsSource=items.ToArray(),SelectedItem=selected,Background=Black,Foreground=Brushes.White,BorderBrush=B("#66666A"),FontSize=15,MinHeight=48,MaxHeight=130,Padding=new Thickness(4)};var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));var selectedTrigger=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty,Orange));selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty,B("#392413")));style.Triggers.Add(selectedTrigger);box.ItemContainerStyle=style;return box;}
 Button ActionButton(string text,Action action){var btn=Btn(text,action);btn.MinHeight=40;return btn;}
 if(!midiPage){
 var preset=Section("01  マクロプリセット / PRESET","ラベルの組み合わせを保存・読み出しします。操作命令は送信されません。");
 var active=new TextBlock{Text="使用中："+presets.Selected,Foreground=Green,FontSize=15,Margin=new Thickness(3)};preset.Children.Add(active);
 Label(preset,"保存済みプリセットを選択");var combo=Choices(presets.Presets.Keys,presets.Selected);preset.Children.Add(combo);
 Label(preset,"新規保存・名前変更に使用する名前");var name=Field(presets.Selected);preset.Children.Add(name);combo.SelectionChanged+=(s,e)=>{if(combo.SelectedItem is string n)name.Text=n;};
 var buttons=new UniformGrid{Columns=3,Margin=new Thickness(0,8,0,0)};preset.Children.Add(buttons);var notice=new TextBlock{Foreground=Green,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,5,3,0)};preset.Children.Add(notice);
 buttons.Children.Add(ActionButton("読み出し",()=>{if(combo.SelectedItem is string n){presets.Selected=n;labels=Normalize(presets.Presets[n]);Save("macro.json",presets);Show(2);}}));
 buttons.Children.Add(ActionButton("保存（使用中）",()=>{presets.Presets[presets.Selected]=(string[])labels.Clone();Save("macro.json",presets);notice.Text="保存処理："+presets.Selected+"（エラーは通信ログに表示）";}));
 buttons.Children.Add(ActionButton("名前を付けて保存",()=>{var n=name.Text.Trim();if(n.Length==0||presets.Presets.ContainsKey(n)){notice.Text="未使用の名前を入力してください。";return;}presets.Selected=n;presets.Presets[n]=(string[])labels.Clone();Save("macro.json",presets);Show(2);}));
 buttons.Children.Add(ActionButton("名前変更",()=>{if(combo.SelectedItem is not string old)return;var n=name.Text.Trim();if(n.Length==0||presets.Presets.ContainsKey(n)){notice.Text="未使用の名前を入力してください。";return;}presets.Presets[n]=presets.Presets[old];presets.Presets.Remove(old);if(presets.Selected==old)presets.Selected=n;Save("macro.json",presets);Show(2);}));
 buttons.Children.Add(ActionButton("削除（選択中）",()=>{if(combo.SelectedItem is not string n||presets.Presets.Count<=1)return;if(MessageBox.Show(n+" を削除しますか？","確認",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;presets.Presets.Remove(n);if(presets.Selected==n){presets.Selected=presets.Presets.Keys.First();labels=Normalize(presets.Presets[presets.Selected]);}Save("macro.json",presets);Show(2);}));
 buttons.Children.Add(ActionButton("Scene JSONを取り込む",()=>{var dlg=new Microsoft.Win32.OpenFileDialog{Filter="JSON (*.json)|*.json"};if(dlg.ShowDialog()!=true)return;try{var incoming=JsonSerializer.Deserialize<PresetData>(File.ReadAllText(dlg.FileName));if(incoming==null||incoming.Presets==null||incoming.Presets.Count==0)throw new InvalidDataException("プリセットがありません");foreach(var entry in incoming.Presets){if(entry.Value==null||entry.Value.Length!=61)throw new InvalidDataException("61ラベル必要: "+entry.Key);}presets.Presets[presets.Selected]=(string[])labels.Clone();string? selected=null;foreach(var entry in incoming.Presets){string n=entry.Key;int suffix=2;while(presets.Presets.ContainsKey(n))n=entry.Key+" ("+(suffix++)+ ")";presets.Presets[n]=Normalize(entry.Value);if(selected==null||entry.Key==incoming.Selected)selected=n;}presets.Selected=selected!;labels=Normalize(presets.Presets[presets.Selected]);Save("macro.json",presets);Show(2);}catch(Exception e){MessageBox.Show(e.Message,"JSON取り込み失敗");}}));
 buttons.Children.Add(ActionButton("初期ラベルに戻す",()=>{labels=(string[])DefaultLabels.Clone();Show(2);}));
 var labelSection=Section("VRChat ラベル取得","設定を開くと1回だけ取得要求します。取得したラベルは取り込みまで現在のプリセットに反映しません。");
 labelStatus=new TextBlock{Text=labelSnapshot!=null?"取得済み: 61/61":labelPending?"取得中…":"未取得",Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};labelSection.Children.Add(labelStatus);
 labelSection.Children.Add(ActionButton("VRChatから再取得",RequestLabels));
 labelSection.Children.Add(ActionButton("取得したラベルを別プリセットとして取り込む",ImportLabelSnapshot));
 var edit=Section("02  マクロラベル / LABEL","ボタンを選択するか、一覧から編集してください。変更はプリセットの保存で記録します。");
 var modes=new UniformGrid{Columns=2};edit.Children.Add(modes);var host=new ContentControl{Margin=new Thickness(0,8,0,0)};edit.Children.Add(host);
 int editMode=0;Button? panelMode=null,listMode=null;void RefreshModes(){if(panelMode!=null)SetSelected(panelMode,editMode==1);if(listMode!=null)SetSelected(listMode,editMode==2);}
 void OpenPanel(){if(editMode==1){host.Content=null;editMode=0;RefreshModes();return;}var preview=new StackPanel();preview.Children.Add(MacroPage(true));preview.Children.Add(new TextBlock{Text="操作卓へ移動したマクロ",Foreground=Brushes.LightGray,Margin=new Thickness(4,12,4,4)});var moved=new UniformGrid{Columns=4};foreach(int id in new[]{0,9,8,4,5,6,7})moved.Children.Add(MacroButton(id,true));preview.Children.Add(moved);host.Content=preview;editMode=1;RefreshModes();}
 void OpenList(){if(editMode==2){host.Content=null;editMode=0;RefreshModes();return;}var entries=new StackPanel();for(int i=0;i<61;i++){int id=i;var row=new DockPanel{Margin=new Thickness(2)};var caption=new TextBlock{Text=DefaultLabels[i],Width=135,Foreground=Brushes.LightGray,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(caption,Dock.Left);row.Children.Add(caption);var t=Field(labels[i]);t.TextChanged+=(s,e)=>labels[id]=t.Text;row.Children.Add(t);entries.Children.Add(row);}host.Content=new ScrollViewer{Content=entries,MaxHeight=380,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};editMode=2;RefreshModes();}
 panelMode=ActionButton("ボタンを選択して編集",OpenPanel);listMode=ActionButton("一覧から編集",OpenList);modes.Children.Add(panelMode);modes.Children.Add(listMode);
 }else{
 var midiSection=Section("01  MIDI接続設定 / MIDI","出力先を選択し、画面上部の接続ボタンで接続・切断します。");
 Label(midiSection,"MIDI出力デバイス");devices.Clear();devices.AddRange(Midi.Devices());var dev=Choices(devices.Select(d=>d.Name).Append(settings.Device).Distinct(),settings.Device);midiSection.Children.Add(dev);var saveState=new TextBlock{Foreground=Green,Text="設定は自動保存されます",TextWrapping=TextWrapping.Wrap};midiSection.Children.Add(saveState);bool restoring=false;
 void MidiSaveState(bool ok){saveState.Text=ok?"設定を保存しました":"保存失敗：通信ログと書き込み権限を確認してください";saveState.Foreground=ok?Green:Red;}
 dev.SelectionChanged+=(s,e)=>{if(restoring||dev.SelectedItem is not string n||n==settings.Device)return;string old=settings.Device;settings.Device=n;bool ok=Save("settings.json",settings);if(!ok){settings.Device=old;restoring=true;dev.SelectedItem=old;restoring=false;}else if(midi.Connected)Disconnect();MidiSaveState(ok);};
 midiSection.Children.Add(ActionButton("デバイス一覧を更新",()=>{devices.Clear();devices.AddRange(Midi.Devices());restoring=true;dev.ItemsSource=devices.Select(d=>d.Name).Append(settings.Device).Distinct().ToArray();dev.SelectedItem=settings.Device;restoring=false;}));
 var auto=new CheckBox{Content="起動時に自動接続",IsChecked=settings.AutoConnect,Foreground=Brushes.White,FontSize=15,Margin=new Thickness(4,10,4,4)};void SaveAuto(){if(restoring)return;bool old=settings.AutoConnect;settings.AutoConnect=auto.IsChecked==true;bool ok=Save("settings.json",settings);if(!ok){settings.AutoConnect=old;restoring=true;auto.IsChecked=old;restoring=false;}MidiSaveState(ok);}auto.Checked+=(s,e)=>SaveAuto();auto.Unchecked+=(s,e)=>SaveAuto();midiSection.Children.Add(auto);

 }
 return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};}
 TextBlock? labelStatus;
 string[]? labelSnapshot;
 readonly string?[] labelParts=new string?[61];
 bool labelPending;
 int labelRequestId=Random.Shared.Next(128);
 DateTime labelDeadline;
 void SetLabelStatus(string text){if(labelStatus!=null&&tab==2)labelStatus.Text=text;}
 void CancelLabelRequest(){labelPending=false;Array.Clear(labelParts);}
 void RequestLabels(){
  CancelLabelRequest();labelSnapshot=null;
  if(!midi.Connected){SetLabelStatus("MIDI未接続：接続後に再取得してください");return;}
  ReadFeedback(); // establish log tail before sending
  if(logPath==null){SetLabelStatus("VRChatログ未検出");return;}
  labelRequestId=(labelRequestId+1)&127;
  labelDeadline=DateTime.UtcNow.AddSeconds(5);labelPending=true;
  if(!midi.CC(6,labelRequestId,1)){CancelLabelRequest();SetLabelStatus("MIDI送信失敗");return;}
  SetLabelStatus("取得中: 0/61");Log("TX label snapshot request "+labelRequestId);
 }
 void ReadLabelRecord(string record){
  if(!labelPending||tab!=2||DateTime.UtcNow>=labelDeadline)return;
  var parts=record.Split('|');if(parts.Length!=3||!int.TryParse(parts[0],out int request)||request!=labelRequestId)return;
  if(parts[1]=="ERR"){CancelLabelRequest();SetLabelStatus("ラベル取得失敗: 長さ制限超過 ID "+parts[2]);return;}
  if(parts[1]=="END"){
   if(parts[2]!="61"||labelParts.Any(x=>x==null)){CancelLabelRequest();SetLabelStatus("取得不完全：再取得してください");return;}
   labelSnapshot=Normalize(labelParts.Select(x=>x!).ToArray());CancelLabelRequest();SetLabelStatus("取得済み: 61/61（空欄は既定名）");Log("RX label snapshot 61/61");return;
  }
  if(!int.TryParse(parts[1],out int id)||id<0||id>=61)return;
  string hex=parts[2];if(hex.Length>1024||hex.Length%4!=0)return;
  var chars=new char[hex.Length/4];for(int i=0;i<chars.Length;i++){if(!ushort.TryParse(hex.AsSpan(i*4,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out ushort code))return;chars[i]=(char)code;}
  labelParts[id]=new string(chars);SetLabelStatus("取得中: "+labelParts.Count(x=>x!=null)+"/61");
 }
 void ImportLabelSnapshot(){
  if(labelSnapshot==null){SetLabelStatus("完全な取得結果がありません");return;}
  string baseName="VRChat "+DateTime.Now.ToString("yyyy-MM-dd HHmmss"),n=baseName;int suffix=2;
  while(presets.Presets.ContainsKey(n))n=baseName+" ("+(suffix++)+")";
  presets.Presets[presets.Selected]=(string[])labels.Clone();
  presets.Presets[n]=(string[])labelSnapshot.Clone();presets.Selected=n;labels=(string[])labelSnapshot.Clone();Save("macro.json",presets);Show(2);SetLabelStatus("取り込み: "+n);
 }
 void ClearLogs(){log.Clear();shortLog.Clear();}
 void Disconnect(){ReleaseExternal();ResetExternalTracking();externalRemoteValid=false;CancelLabelRequest();SetLabelStatus("MIDI切断");ReleaseAll();midi.Dispose();Log("MIDI disconnected");UpdateStatus();}
 void ToggleConnection(){if(midi.Connected)Disconnect();else Connect();Save("settings.json",settings);}
 FrameworkElement MonitorPage(){var p=new Grid();p.RowDefinitions.Add(new(){Height=new GridLength(45)});p.RowDefinitions.Add(new());p.Children.Add(Btn("ログ消去",ClearLogs));if(log.Parent is Panel parent)parent.Children.Remove(log);Grid.SetRow(log,1);p.Children.Add(log);return p;}
 void Connect(){ReleaseAll();ReleaseExternal();ResetExternalTracking();externalRemoteValid=false;if(external.Input==settings.Device||external.Output==settings.Device)CloseExternal();Array.Fill(lastDialCode,-1);var d=Midi.Devices().FirstOrDefault(x=>x.Name==settings.Device);if(d.Name!=null&&midi.Open(d.Id))Log("MIDI connected: "+d.Name);else Log("MIDI offline: "+settings.Device);UpdateStatus();}
 void ToggleFull(){if(!full){oldState=WindowState;oldStyle=WindowStyle;oldResize=ResizeMode;WindowState=WindowState.Normal;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;WindowState=WindowState.Maximized;full=true;}else{WindowState=WindowState.Normal;WindowStyle=oldStyle;ResizeMode=oldResize;WindowState=oldState;full=false;}if(fullscreenButton!=null)SetSelected(fullscreenButton,full);}
 void Log(string s){if(log.Text.Length>40000)log.Text=log.Text[^20000..];log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff")+" "+s+Environment.NewLine);log.ScrollToEnd();if(shortLog.Text.Length>3500)shortLog.Text=shortLog.Text[^1800..];shortLog.AppendText(s+Environment.NewLine);shortLog.ScrollToEnd();}
 bool FeedbackLive=>DateTime.UtcNow-lastFeedback<TimeSpan.FromSeconds(4);
 void UpdateStatus(){
 if(labelPending&&DateTime.UtcNow>=labelDeadline){CancelLabelRequest();SetLabelStatus("取得タイムアウト（接続・Owner・ワールドの版を確認）");}
 RefreshInfo();
 midiStatus.Text="MIDI: "+(midi.Connected?"CONNECTED":"OFFLINE");midiStatus.Foreground=midi.Connected?Green:Red;
 feedbackStatus.Text="VRCHAT: "+(FeedbackLive?"ACTIVE":"OFFLINE");feedbackStatus.Foreground=FeedbackLive?Green:Red;
 if(connectionButton!=null){connectionButton.Content=midi.Connected?"切断":"接続";connectionButton.Foreground=midi.Connected?Green:Red;}
 status.Text=$"MIDI {(midi.Connected?"CONNECTED":"OFFLINE")}   |   VRCHAT FEEDBACK {(FeedbackLive?"ACTIVE":"UNAVAILABLE")}   |   INFO {(InfoLive?"ACTIVE":"OFFLINE")}   |   EXT MIDI {(externalInput.Connected?"ON":"OFF")}   |   v0.1.21";}
 void ReadFeedback(){try{var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","VRChat","VRChat");if(!Directory.Exists(dir))return;var file=Directory.GetFiles(dir,"output_log_*.txt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();if(file==null)return;if(file!=logPath){CancelLabelRequest();labelSnapshot=null;SetLabelStatus("ログ切替：再取得してください");logPath=file;logOffset=new FileInfo(file).Length;pending="";lastFeedback=DateTime.MinValue;externalRemoteValid=false;lastInfoFeedback=DateTime.MinValue;infoFrame=null;return;}using var f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(f.Length<logOffset){CancelLabelRequest();labelSnapshot=null;SetLabelStatus("ログ切替：再取得してください");logOffset=0;pending="";lastFeedback=DateTime.MinValue;externalRemoteValid=false;lastInfoFeedback=DateTime.MinValue;infoFrame=null;}f.Position=logOffset;using var r=new StreamReader(f);pending+=r.ReadToEnd();logOffset=f.Position;var lines=pending.Split('\n');pending=lines[^1];foreach(var line in lines.Take(lines.Length-1)){const string labelPrefix="[NAGOYA_LABEL_V1] ";int labelAt=line.IndexOf(labelPrefix,StringComparison.Ordinal);if(labelAt>=0){ReadLabelRecord(line[(labelAt+labelPrefix.Length)..].Trim());continue;}const string infoPrefix="[NAGOYA_INFO_V1] ";int infoAt=line.IndexOf(infoPrefix,StringComparison.Ordinal);if(infoAt>=0){ReadInfo(line[(infoAt+infoPrefix.Length)..].Trim());continue;}int at=line.IndexOf("[NAGOYA_CONSOLE_V1] ",StringComparison.Ordinal);if(at<0)continue;var payload=line[(at+"[NAGOYA_CONSOLE_V1] ".Length)..].Trim().Split('|');if(payload.Length!=2)continue;var vv=payload[0].Split(',');var mm=payload[1].Split(',');if(vv.Length!=40||mm.Length!=61)continue;var nv=new double[40];var nm=new int[61];bool valid=true;for(int i=0;i<40;i++)valid&=double.TryParse(vv[i],NumberStyles.Float,CultureInfo.InvariantCulture,out nv[i])&&double.IsFinite(nv[i]);for(int i=0;i<61;i++)valid&=int.TryParse(mm[i],out nm[i])&&nm[i]>=0&&nm[i]<=2;if(!valid)continue;for(int i=0;i<40;i++)nv[i]/=10000.0;ObserveExternalRemote(nv,nm);foreach(var k in Knobs){if(k.Kind!=4)continue;if(visuals.TryGetValue(k.Id,out var dial)&&dial.IsDragging){nv[k.Id]=values[k.Id];continue;}lastDialCode[k.Id]=DialDeadZone.Encode(nv[k.Id]);}Array.Copy(nv,values,40);Array.Copy(nm,macros,61);foreach(var kv in visuals)kv.Value.SetRemote(values[kv.Key]);RefreshPanelLighting();lastFeedback=DateTime.UtcNow;}}catch(IOException){}catch(UnauthorizedAccessException){}}
}
public sealed class KnobVisual:FrameworkElement {
 readonly Knob k; readonly Action<double> changed; double value, lastAngle, accumulated; Point start,last; int dragMode; bool dragging; TouchDevice? touch; bool pen;
 public KnobVisual(Knob k,double v,Action<double> changed){this.k=k;value=v;this.changed=changed;Focusable=true;IsManipulationEnabled=false;Stylus.SetIsPressAndHoldEnabled(this,false);Stylus.SetIsFlicksEnabled(this,false);}
 double panelBrightness; public double PanelBrightness {get=>panelBrightness;set{double next=Math.Clamp(value,0,1);if(panelBrightness!=next){panelBrightness=next;InvalidateVisual();}}}
 public bool IsDragging=>dragging;
 public void SetRemote(double v){if(!dragging){value=v;InvalidateVisual();}}
 double CenterY=>ActualHeight*(k.Kind==3?.67:.57);
 double Angle(Point p)=>Math.Atan2(p.X-ActualWidth/2,-(p.Y-CenterY))*180/Math.PI;
 void Begin(Point p){dragging=true;accumulated=0;lastAngle=Angle(p);start=last=p;dragMode=0;}
 void Move(Point p){if(!dragging)return;double a=Angle(p);if(dragMode==0){var delta=p-start;if(delta.Length<4)return;dragMode=Math.Abs(delta.Y)>Math.Abs(delta.X)*1.25?1:2;}double d;if(dragMode==1)d=(last.Y-p.Y)*270/Math.Max(60,ActualHeight*1.4);else{d=a-lastAngle;while(d>180)d-=360;while(d< -180)d+=360;}last=p;lastAngle=a;if(k.Kind==0||k.Kind==4)Set(Math.Clamp(value+d/(k.Kind==4?360:270),0,1));else{double step=k.Kind==3?45:40;accumulated+=d;while(Math.Abs(accumulated)>=step*.65){int sign=Math.Sign(accumulated);Step(sign);accumulated-=sign*step*.65;}}}
 void Step(int direction){double min=k.Kind==1||k.Kind==3?-1:0,max=k.Kind==3?2:1;Set(Math.Clamp(value+direction,min,max));}
 void Set(double v){if(Math.Abs(v-value)<.000001)return;value=v;InvalidateVisual();changed(v);}
 protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){if(e.StylusDevice!=null||dragging)return;Focus();CaptureMouse();Begin(e.GetPosition(this));e.Handled=true;}
 protected override void OnMouseMove(MouseEventArgs e){if(IsMouseCaptured&&e.StylusDevice==null)Move(e.GetPosition(this));}
 protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){if(!IsMouseCaptured)return;dragging=false;ReleaseMouseCapture();e.Handled=true;}
 protected override void OnLostMouseCapture(MouseEventArgs e){if(touch==null&&!pen)dragging=false;}
 protected override void OnTouchDown(TouchEventArgs e){if(dragging)return;touch=e.TouchDevice;CaptureTouch(touch);Begin(e.GetTouchPoint(this).Position);e.Handled=true;}
 protected override void OnTouchMove(TouchEventArgs e){if(touch==e.TouchDevice){Move(e.GetTouchPoint(this).Position);e.Handled=true;}}
 protected override void OnTouchUp(TouchEventArgs e){if(touch==e.TouchDevice){dragging=false;touch=null;ReleaseTouchCapture(e.TouchDevice);e.Handled=true;}}
 protected override void OnLostTouchCapture(TouchEventArgs e){if(touch==e.TouchDevice){touch=null;dragging=false;}}
 protected override void OnStylusDown(StylusDownEventArgs e){if(e.StylusDevice.TabletDevice.Type==TabletDeviceType.Touch||dragging)return;pen=true;CaptureStylus();Begin(e.GetPosition(this));e.Handled=true;}
 protected override void OnStylusMove(StylusEventArgs e){if(pen){Move(e.GetPosition(this));e.Handled=true;}}
 protected override void OnStylusUp(StylusEventArgs e){if(pen){pen=false;dragging=false;ReleaseStylusCapture();e.Handled=true;}}
 protected override void OnLostStylusCapture(StylusEventArgs e){if(pen){pen=false;dragging=false;}}
 protected override void OnMouseWheel(MouseWheelEventArgs e){if(k.Kind==0||k.Kind==4)Set(Math.Clamp(value+Math.Sign(e.Delta)/100.0,0,1));else Step(Math.Sign(e.Delta));e.Handled=true;}
 // Display angles keep the existing -2I,-I,+I,+2I stop ratio.
 static double ReferenceAngle(double v)=>v<-.5?-60:v<.5?-30:v<1.5?30:60;
 // Unity StateFromValue01(value,0.01f); corresponds to +/-3.6 degrees.
 static int DialState(double v)=>DialDeadZone.State(v);
 int DisplayState()=>k.Kind==4?DialState(value):k.Kind==3?0:k.Kind==1?Math.Sign(value):value>0?1:0;
 void DrawTextAt(DrawingContext dc,string text,double x,double y,double size,Brush color){var t=new FormattedText(text,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,new Typeface("Yu Gothic UI"),size,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(t,new Point(x-t.Width/2,y-t.Height/2));}
 static Brush BrushOf(string c)=>(Brush)new BrushConverter().ConvertFromString(c)!;
 static Brush Shade(string a,string b,double angle){var brush=new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a),(Color)ColorConverter.ConvertFromString(b),angle);brush.Freeze();return brush;}
 static readonly Brush Bezel=Shade("#555558","#29292C",90), KnobFace=Shade("#414145","#252528",65), HandleFace=Shade("#444448","#29292C",0), LampRim=Shade("#454548","#202022",90), LampFace=Shade("#69696D","#4B4B4F",90);

 protected override void OnRender(DrawingContext dc){double w=ActualWidth,h=ActualHeight;dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,w,h));double cx=w/2,cy=CenterY,r=Math.Min(w,h)*(k.Kind==3?.15:.21);dc.DrawRectangle(Bezel,new Pen(BrushOf("#555558"),1),new Rect(6,5,w-12,h*.19));dc.DrawRectangle(BrushOf("#080809"),null,new Rect(8,7,w-16,h*.19-4));var txt=new FormattedText(k.Label,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,new Typeface("Yu Gothic UI"),Math.Min(15,w/(Math.Max(k.Label.Length,3)*.93)),PanelTint.State(panelBrightness,DisplayState()==0?0:2),VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(txt,new Point(cx-txt.Width/2,5+(h*.19-txt.Height)/2));double angle=k.Kind==0?value*270:k.Kind==4?(value-.5)*360:k.Kind==3?ReferenceAngle(value):value*40;dc.PushTransform(new RotateTransform(angle,cx,cy));var black=KnobFace;dc.DrawEllipse(black,new Pen(BrushOf("#202022"),1.5),new Point(cx,cy),k.Kind==4?r*1.28:r,k.Kind==4?r*1.28:r);if(k.Kind==0){dc.DrawRoundedRectangle(black,null,new Rect(cx-r*.25,cy,r*.5,r*1.55),4,4);dc.DrawEllipse(black,null,new Point(cx,cy+r*1.55),r*.45,r*.45);}else if(k.Kind==4)dc.DrawLine(new Pen(Brushes.Gray,2),new Point(cx,cy-r*1.2),new Point(cx,cy-r*.7));else{dc.DrawRoundedRectangle(HandleFace,new Pen(BrushOf("#151517"),1),new Rect(cx-r*.33,cy-r*1.28,r*.66,r*2.56),4,4);dc.DrawLine(new Pen(Brushes.Gray,1),new Point(cx,cy-r),new Point(cx,cy-r*.5));}dc.Pop();if(k.Kind==1||k.Kind==4){int direction=DisplayState();for(int sign=-1;sign<=1;sign+=2){double x=cx+sign*w*.35,y=cy-r*.7;bool on=direction==sign;dc.DrawEllipse(LampRim,new Pen(BrushOf("#252527"),.8),new Point(x,y),r*.37,r*.37);dc.DrawEllipse(LampFace,null,new Point(x,y),r*.28,r*.28);DrawTextAt(dc,k.Id==8?(sign<0?"S":"N"):(sign<0?"−":"+"),x,y,Math.Min(15,r*.65),on?PanelTint.Get(panelBrightness,Colors.Lime):BrushOf("#808080"));}}if(k.Kind==3){for(int i=0;i<4;i++){double rad=ReferenceAngle(i-1)*Math.PI/180;double rr=Math.Min(w*.35,h*.36);double x=cx+Math.Sin(rad)*rr,y=cy-Math.Cos(rad)*rr;
 double tickR=r*1.52;dc.DrawLine(new Pen(BrushOf("#555558"),1.3),new Point(cx+Math.Sin(rad)*tickR,cy-Math.Cos(rad)*tickR),new Point(cx+Math.Sin(rad)*(tickR+4),cy-Math.Cos(rad)*(tickR+4)));
 DrawTextAt(dc,i==0?"OFF":i.ToString(),x,y,Math.Min(12,r*.62),BrushOf(Math.Abs(value-(i-1))<.1?(i==0?"#A22922":"#176B32"):"#303033"));}}}
}

public sealed class ScrewHead:FrameworkElement {
 protected override void OnRender(DrawingContext dc){
  double r=Math.Min(ActualWidth,ActualHeight)/2-1.8;if(r<=0)return;
  var c=new Point(ActualWidth/2,ActualHeight/2);
  // Flush countersunk screw: no square seat, raised plate or cast shadow.
  var metal=new LinearGradientBrush(Color.FromRgb(211,211,214),Color.FromRgb(120,120,124),55);
  dc.DrawEllipse(metal,new Pen(Brushes.DimGray,.65),c,r,r);
  dc.DrawEllipse(null,new Pen(new SolidColorBrush(Color.FromRgb(180,180,184)),.55),c,r*.76,r*.76);
  double d=r*.47;var pen=new Pen(new SolidColorBrush(Color.FromRgb(60,60,63)),1.25);
  dc.DrawLine(pen,new Point(c.X-d,c.Y),new Point(c.X+d,c.Y));dc.DrawLine(pen,new Point(c.X,c.Y-d),new Point(c.X,c.Y+d));
 }
}

// Seamless, softly shaded micro-relief. One shared frozen bitmap, built only once.
public static class PaintedMetal {
 public static readonly Brush Brush=Create();
 private static Brush Create(){
  const int n=1024, mask=n-1;
  var random=new Random(27183);
  var noise=new float[n*n];var tmp=new float[n*n];var fine=new float[n*n];var broad=new float[n*n];
  for(int i=0;i<noise.Length;i++)noise[i]=(float)(random.NextDouble()*2-1);
  // Separable binomial filter; periodic boundaries avoid seams.
  void Blur(float[] input,float[] output,int step){
   for(int y=0;y<n;y++)for(int x=0;x<n;x++){
    int row=y*n;tmp[row+x]=(input[row+((x-2*step)&mask)]+4*input[row+((x-step)&mask)]+6*input[row+x]+4*input[row+((x+step)&mask)]+input[row+((x+2*step)&mask)])/16f;
   }
   for(int y=0;y<n;y++)for(int x=0;x<n;x++)output[y*n+x]=(tmp[((y-2*step)&mask)*n+x]+4*tmp[((y-step)&mask)*n+x]+6*tmp[y*n+x]+4*tmp[((y+step)&mask)*n+x]+tmp[((y+2*step)&mask)*n+x])/16f;
  }
  Blur(noise,fine,1);Blur(fine,broad,2);
  for(int i=0;i<noise.Length;i++)noise[i]=fine[i]*.78f+broad[i]*.65f;
  float H(int x,int y)=>noise[(y&mask)*n+(x&mask)];
  var pixels=new byte[n*n*4];
  for(int y=0;y<n;y++)for(int x=0;x<n;x++){
   double dx=(H(x+1,y)-H(x-1,y))*.5,dy=(H(x,y+1)-H(x,y-1))*.5;
   double nx=-dx*2.0,ny=-dy*2.0;
   double light=(-.38*nx-.48*ny+.789)/Math.Sqrt(1+nx*nx+ny*ny);
   int delta=(int)Math.Round(Math.Clamp((light-.789)*37+H(x,y)*7,-16,16));
   int k=(y*n+x)*4;pixels[k]=(byte)(180+delta);pixels[k+1]=(byte)(176+delta);pixels[k+2]=(byte)(176+delta);pixels[k+3]=255;
  }
  var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(n,n,96,96,PixelFormats.Bgra32,null,pixels,n*4);bitmap.Freeze();
  var brush=new ImageBrush(bitmap){TileMode=TileMode.Tile,ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,n,n),Stretch=Stretch.Fill};brush.Freeze();return brush;
 }
}
