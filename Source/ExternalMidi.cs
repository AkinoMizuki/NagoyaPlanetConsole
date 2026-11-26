using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NagoyaConsole;

// Windows MIDI 1.0 short-message input. Native callback never calls WPF or WinMM.
internal sealed class MidiInput : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Caps { public ushort mid,pid; public uint version; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string name; public uint support; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void Callback(IntPtr h,uint msg,UIntPtr instance,UIntPtr p1,UIntPtr p2);
    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll",CharSet=CharSet.Unicode)] static extern uint midiInGetDevCapsW(UIntPtr id,out Caps caps,uint size);
    [DllImport("winmm.dll")] static extern uint midiInOpen(out IntPtr h,uint id,Callback cb,UIntPtr instance,uint flags);
    [DllImport("winmm.dll")] static extern uint midiInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern uint midiInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern uint midiInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern uint midiInClose(IntPtr h);
    readonly ConcurrentQueue<uint> queue=new();
    readonly Callback callback;
    IntPtr handle; int queued,overflow;
    public MidiInput(){callback=Receive;}
    public bool Connected=>handle!=IntPtr.Zero;
    public bool HasData=>!queue.IsEmpty;
    public static List<(uint Id,string Name)> Devices(){var list=new List<(uint,string)>();for(uint i=0;i<midiInGetNumDevs();i++)if(midiInGetDevCapsW((UIntPtr)i,out var c,(uint)Marshal.SizeOf<Caps>())==0)list.Add((i,c.name));return list;}
    void Receive(IntPtr h,uint msg,UIntPtr instance,UIntPtr p1,UIntPtr p2){if(msg==0x3C2||msg==0x3C5){System.Threading.Interlocked.Exchange(ref overflow,1);return;}if(msg!=0x3C3)return;uint raw=(uint)p1.ToUInt64();uint type=raw&0xF0;if(type!=0xB0&&type!=0x90&&type!=0x80)return;if(System.Threading.Interlocked.Increment(ref queued)>2048){System.Threading.Interlocked.Decrement(ref queued);System.Threading.Interlocked.Exchange(ref overflow,1);return;}queue.Enqueue(raw);}
    public bool Open(uint id){Dispose();if(midiInOpen(out handle,id,callback,UIntPtr.Zero,0x30000)!=0){handle=IntPtr.Zero;return false;}if(midiInStart(handle)==0)return true;Dispose();return false;}
    public bool TryRead(out uint raw){if(!queue.TryDequeue(out raw))return false;System.Threading.Interlocked.Decrement(ref queued);return true;}
    public bool Overflow()=>System.Threading.Interlocked.Exchange(ref overflow,0)!=0;
    public void Dispose(){if(Connected){midiInStop(handle);midiInReset(handle);midiInClose(handle);handle=IntPtr.Zero;}while(TryRead(out _)){}Overflow();GC.KeepAlive(callback);}
}
public sealed class ExternalMidiConfig
{
    public string Input {get;set;}="";
    public string Output {get;set;}="";
    public bool AutoConnect {get;set;}
    public bool LearnAutoSave {get;set;}=true;
    public List<ExternalMidiMap> Maps {get;set;}=new();
}
public sealed class ExternalMidiMap
{
    // Target 0..39 = knob ID; 100..160 = macro ID + 100; 200 + knob ID * 4 + position index = fixed selector position.
    public int Target {get;set;}
    public int Channel {get;set;}=1;
    public int Number {get;set;}
    // 0 CC7 absolute, 1 CC14 (MSB n, LSB n+32), 2 relative two's complement,
    // 3 relative binary offset, 4 CC momentary, 5 Note momentary.
    public int Mode {get;set;}
    public bool Invert {get;set;}
    public bool Pickup {get;set;}=true;
    public int FeedbackType {get;set;} // 0 off, 1 CC7, 2 Note, 3 CC14
    public int FeedbackChannel {get;set;}=1;
    public int FeedbackNumber {get;set;}
    public int Off {get;set;}=0;
    public int On {get;set;}=127;
    public int Mixed {get;set;}=64;
    // 0 linear, 1 custom position/percent, 2 custom position/dB to amplitude.
    public int CurveMode {get;set;}
    public string CurvePoints {get;set;}="";
    [System.Text.Json.Serialization.JsonIgnore] internal FaderCurve? Curve {get;set;}
}
public sealed partial class ConsoleWindow
{
    readonly MidiInput externalInput=new();
    readonly Midi externalOutput=new();
    readonly DispatcherTimer externalTimer=new(){Interval=TimeSpan.FromMilliseconds(15)};
    ExternalMidiConfig external=new();
    readonly HashSet<int> externalHeld=new();
    readonly HashSet<int> selectorHeld=new();
    readonly Dictionary<int,double> pickupPrevious=new();
    readonly HashSet<int> pickupAcquired=new();
    readonly Dictionary<int,int> msb=new(),lsb=new();
    readonly Dictionary<int,int> sentFeedback=new();
    readonly Dictionary<uint,long> echoed=new();
    readonly double[] remoteExternalValues=new double[40];
    readonly int[] remoteExternalMacros=new int[61];
    bool externalRemoteValid,externalWasLive;
    long nextDeviceCheck;
    readonly Dictionary<int,(double Value,long At)> externalExpected=new();
    Action<int,int,int>? learnCapture;
    TextBlock? externalStatus;
    Button? learnButton;
    TextBlock? externalEditStatus;
    bool ExternalReady=>midi.Connected&&FeedbackLive&&externalRemoteValid;
    void InitExternal(){external=LoadExternalConfig();externalTimer.Tick+=(s,e)=>PollExternal();externalTimer.Start();if(external.AutoConnect)OpenExternal();}
    static bool IsMacroTarget(int id)=>id>=100&&id<161;
    static bool IsPositionTarget(int id)=>id>=200;
    static int PositionKnobId(int id)=>(id-200)/4;
    static int PositionIndex(int id)=>(id-200)%4;
    static int PositionCount(Knob k)=>k.Kind==1?3:k.Kind==2?2:k.Kind==3?4:0;
    static bool ValidPositionTarget(int id)=>IsPositionTarget(id)&&Knobs.Any(k=>k.Id==PositionKnobId(id)&&PositionIndex(id)<PositionCount(k));
    static double PositionValue(int id,Knob k)=>KnobMin(k)+PositionIndex(id);
    static bool ValidExternalMap(ExternalMidiMap m)=>ValidCurve(m)&&(Knobs.Any(k=>k.Id==m.Target)||IsMacroTarget(m.Target)||ValidPositionTarget(m.Target))&&m.Channel>=1&&m.Channel<=16&&m.Number>=0&&m.Number<=127&&m.Mode>=0&&m.Mode<=5&&((IsMacroTarget(m.Target)||IsPositionTarget(m.Target))?m.Mode>=4:m.Mode<4)&&(m.Mode!=1||m.Number<32)&&m.FeedbackType>=0&&m.FeedbackType<=3&&m.FeedbackChannel>=1&&m.FeedbackChannel<=16&&m.FeedbackNumber>=0&&m.FeedbackNumber<=127&&(m.FeedbackType!=3||m.FeedbackNumber<32)&&(!(IsMacroTarget(m.Target)||IsPositionTarget(m.Target))||m.FeedbackType!=3)&&m.Off>=0&&m.Off<=127&&m.On>=0&&m.On<=127&&m.Mixed>=0&&m.Mixed<=127;
    static bool ValidCurve(ExternalMidiMap m){if(m==null)return false;if(m.CurveMode!=0&&(m.Mode>1||!Knobs.Any(k=>k.Id==m.Target&&(k.Kind==0||k.Kind==4))))return false;if(!FaderCurve.TryCreate(m.CurveMode,m.CurvePoints,out var curve,out _))return false;m.Curve=curve;return true;}
    static double CurveForward(ExternalMidiMap m,double n)=>m.Curve?.Forward(n)??n;
    static double CurveInverse(ExternalMidiMap m,double n)=>m.Curve?.Inverse(n)??n;
    void ExternalNotice(string text){if(externalStatus!=null){externalStatus.Text=text;externalStatus.Foreground=externalInput.Connected?Green:Orange;}if(externalEditStatus!=null)externalEditStatus.Text=text;Log("EXT MIDI: "+text);}
    void CancelLearn(){learnCapture=null;if(learnButton!=null)SetSelected(learnButton,false);}
    void ResetExternalTracking(){selectorHeld.Clear();pickupPrevious.Clear();pickupAcquired.Clear();externalExpected.Clear();msb.Clear();lsb.Clear();sentFeedback.Clear();echoed.Clear();}
    void ReleaseExternal(){foreach(int id in externalHeld.ToArray())ExternalMacro(id,false);}
    void CloseExternal(){CancelLearn();PauseBatch();ReleaseExternal();externalInput.Dispose();externalOutput.Dispose();ResetExternalTracking();externalWasLive=false;}
    void OpenExternal(){CloseExternal();if(string.IsNullOrWhiteSpace(external.Input)||external.Input==settings.Device){ExternalNotice("物理入力を選択してください（VRChat用ポートは不可）");return;}var inputs=MidiInput.Devices().Where(d=>d.Name==external.Input).ToArray();if(inputs.Length!=1||!externalInput.Open(inputs[0].Id)){ExternalNotice("入力を開けません（未接続・同名機器・他アプリ使用中）");return;}
        if(!string.IsNullOrEmpty(external.Output)){var outputs=Midi.Devices().Where(d=>d.Name==external.Output).ToArray();if(external.Output==settings.Device||outputs.Length!=1||!externalOutput.Open(outputs[0].Id)){ExternalNotice("入力接続済み / フィードバック出力を開けません");return;}}
        ExternalNotice("入力接続済み"+(externalOutput.Connected?" / 出力接続済み":" / 出力なし"));}
    void ExternalMacro(int id,bool down){if(down){if(!externalHeld.Add(id))return;if(!held.Contains(id)){Send(4,id,1);if(!FeedbackLive)macros[id]=1;}}else{if(!externalHeld.Remove(id))return;if(!held.Contains(id)){Send(4,id,0);if(!FeedbackLive)macros[id]=0;}}RefreshMacro(id);}
    static double KnobMin(Knob k)=>k.Kind==1||k.Kind==3?-1:0;
    static double KnobMax(Knob k)=>k.Kind==3?2:1;
    static double ToNormalized(Knob k,double value)=>Math.Clamp((value-KnobMin(k))/(KnobMax(k)-KnobMin(k)),0,1);
    static double FromNormalized(Knob k,double normalized){double v=KnobMin(k)+Math.Clamp(normalized,0,1)*(KnobMax(k)-KnobMin(k));return k.Kind==0||k.Kind==4?v:Math.Round(v,MidpointRounding.AwayFromZero);}
    void ExternalKnob(ExternalMidiMap m,double normalized,bool relative){var k=Knobs.First(x=>x.Id==m.Target);if(visuals.TryGetValue(k.Id,out var current)&&current.IsDragging){pickupAcquired.Remove(k.Id);pickupPrevious.Remove(k.Id);return;}
        double value;if(relative){double step=k.Kind==0||k.Kind==4?normalized/127.0:normalized;value=Math.Clamp(values[k.Id]+step,KnobMin(k),KnobMax(k));}
        else{normalized=Math.Clamp(normalized,0,1);double target=CurveInverse(m,ToNormalized(k,values[k.Id]));if(m.Pickup&&!pickupAcquired.Contains(k.Id)){double tolerance=m.Mode==1?2.0/16383:1.5/127;bool close=Math.Abs(normalized-target)<=tolerance|| (k.Kind!=0&&k.Kind!=4&&FromNormalized(k,normalized)==values[k.Id]);bool crossed=pickupPrevious.TryGetValue(k.Id,out double prev)&&(prev-target)*(normalized-target)<=0;pickupPrevious[k.Id]=normalized;if(!close&&!crossed)return;pickupAcquired.Add(k.Id);}value=FromNormalized(k,CurveForward(m,normalized));}
        if(Math.Abs(values[k.Id]-value)<0.000001)return;externalExpected[k.Id]=(value,Environment.TickCount64);SetKnob(k,value);if(visuals.TryGetValue(k.Id,out var visual))visual.SetRemote(value);}
    void ExternalSelector(ExternalMidiMap m,bool down){
        if(!down){selectorHeld.Remove(m.Target);return;}
        if(!selectorHeld.Add(m.Target))return;
        var k=Knobs.First(x=>x.Id==PositionKnobId(m.Target));
        if(visuals.TryGetValue(k.Id,out var visual)&&visual.IsDragging)return;
        double value=PositionValue(m.Target,k);
        pickupAcquired.Remove(k.Id);pickupPrevious.Remove(k.Id);
        if(Math.Abs(values[k.Id]-value)<0.000001)return;
        externalExpected[k.Id]=(value,Environment.TickCount64);SetKnob(k,value);
        if(visual!=null)visual.SetRemote(value);
    }
    static int DecodeRelative(int mode,int v)=>mode==2?(v==64?0:v<64?v:v-128):v-64;
    void PollExternal(){if(!externalInput.Connected)return;if(Environment.TickCount64>=nextDeviceCheck){nextDeviceCheck=Environment.TickCount64+2000;if(MidiInput.Devices().Count(d=>d.Name==external.Input)!=1){CloseExternal();ExternalNotice("入力機器が切断されました");return;}}if(externalInput.Overflow()){CloseExternal();ExternalNotice("入力エラーまたは入力過多のため停止。再接続してください");return;}bool live=ExternalReady;if(externalWasLive&&!live){ReleaseExternal();ResetExternalTracking();}externalWasLive=live;if(!externalInput.HasData)return;var absolute=new Dictionary<int,(ExternalMidiMap Map,double Value)>();var relative=new Dictionary<int,(ExternalMidiMap Map,double Delta)>();for(int i=0;i<512&&externalInput.TryRead(out uint raw);i++){
        int type=(int)(raw&0xF0),ch=(int)(raw&15)+1,num=(int)((raw>>8)&127),v=(int)((raw>>16)&127);if(type==0x80){type=0x90;v=0;}
        uint key=MessageKey(type,ch,num,v);if(echoed.TryGetValue(key,out long at)&&Environment.TickCount64-at<120)continue;
        if(batchCapture!=null){if(type==0x90||type==0xB0)batchCapture(type,ch,num,v);continue;}
        if(learnCapture!=null){if(type!=0x90&&type!=0xB0||type==0x90&&v==0)continue;var capture=learnCapture;CancelLearn();capture(type,ch,num);continue;}
        if(tab==2||tab==3||tab==4||!live)continue;
        foreach(var m in external.Maps){if(m.Channel!=ch)continue;if(m.Mode==5){if(type==0x90&&num==m.Number){if(IsPositionTarget(m.Target)){if(v>0&&!selectorHeld.Contains(m.Target)){absolute.Remove(PositionKnobId(m.Target));relative.Remove(PositionKnobId(m.Target));}ExternalSelector(m,v>0);}else ExternalMacro(m.Target-100,v>0);}continue;}if(type!=0xB0)continue;
            if(m.Mode==1){if(num==m.Number)msb[m.Target]=v;else if(num==m.Number+32)lsb[m.Target]=v;else continue;if(!msb.TryGetValue(m.Target,out int hi))continue;lsb.TryGetValue(m.Target,out int lo);double n=((hi<<7)|lo)/16383.0;absolute[m.Target]=(m,m.Invert?1-n:n);continue;}
            if(num!=m.Number)continue;if(m.Mode==4){bool down=m.Invert?v<64:v>=64;if(IsPositionTarget(m.Target)){if(down&&!selectorHeld.Contains(m.Target)){absolute.Remove(PositionKnobId(m.Target));relative.Remove(PositionKnobId(m.Target));}ExternalSelector(m,down);}else ExternalMacro(m.Target-100,down);continue;}
            if(m.Mode==0){double n=v/127.0;absolute[m.Target]=(m,m.Invert?1-n:n);}else{int delta=DecodeRelative(m.Mode,v);if(delta!=0){relative.TryGetValue(m.Target,out var prior);relative[m.Target]=(m,prior.Delta+(m.Invert?-delta:delta));}}
        }
    }foreach(var a in absolute.Values)ExternalKnob(a.Map,a.Value,false);foreach(var a in relative.Values)if(a.Delta!=0)ExternalKnob(a.Map,a.Delta,true);}
    void ObserveExternalRemote(double[] nv,int[] nm){if(externalRemoteValid){foreach(var k in Knobs){if(Math.Abs(nv[k.Id]-remoteExternalValues[k.Id])<0.0001)continue;bool expected=externalExpected.TryGetValue(k.Id,out var e)&&(Math.Abs(nv[k.Id]-e.Value)<=0.002||Environment.TickCount64-e.At<400);if(!expected){pickupAcquired.Remove(k.Id);pickupPrevious.Remove(k.Id);externalExpected.Remove(k.Id);}}}Array.Copy(nv,remoteExternalValues,40);Array.Copy(nm,remoteExternalMacros,61);externalRemoteValid=true;}
    static uint MessageKey(int type,int ch,int num,int v)=>(uint)(type|(ch-1)|(num<<8)|(v<<16));
    bool FeedbackSend(int type,int ch,int number,int value){if(!externalOutput.Short(type,ch-1,number,value))return false;echoed[MessageKey(type,ch,number,value)]=Environment.TickCount64;return true;}
    void ExternalFeedback(){if(!externalOutput.Connected||!ExternalReady||tab==2||tab==3||tab==4)return;foreach(var m in external.Maps){if(m.FeedbackType==0)continue;int q;if(IsPositionTarget(m.Target)){var k=Knobs.First(x=>x.Id==PositionKnobId(m.Target));q=Math.Abs(remoteExternalValues[k.Id]-PositionValue(m.Target,k))<0.0001?m.On:m.Off;}else if(IsMacroTarget(m.Target)){int s=remoteExternalMacros[m.Target-100];q=s==2?m.On:s==1?m.Mixed:m.Off;}else{var k=Knobs.First(x=>x.Id==m.Target);double n=CurveInverse(m,ToNormalized(k,remoteExternalValues[k.Id]));if(m.Invert)n=1-n;q=(int)Math.Round(n*(m.FeedbackType==3?16383:127));}if(sentFeedback.TryGetValue(m.Target,out int old)&&old==q)continue;
        bool ok=m.FeedbackType==3?FeedbackSend(0xB0,m.FeedbackChannel,m.FeedbackNumber,q>>7)&&FeedbackSend(0xB0,m.FeedbackChannel,m.FeedbackNumber+32,q&127):FeedbackSend(m.FeedbackType==2?0x90:0xB0,m.FeedbackChannel,m.FeedbackNumber,q);if(ok)sentFeedback[m.Target]=q;else{externalOutput.Dispose();ExternalNotice("物理MIDI出力失敗：再接続してください");return;}
    }if(echoed.Count>1024){long now=Environment.TickCount64;foreach(var key in echoed.Where(p=>now-p.Value>=120).Select(p=>p.Key).ToArray())echoed.Remove(key);}}
    string TargetName(int id){
        if(IsMacroTarget(id))return $"M{id-100:00} {labels[id-100]}";
        var k=Knobs.First(k=>k.Id==(IsPositionTarget(id)?PositionKnobId(id):id));
        if(!IsPositionTarget(id))return $"K{id:00} {k.Label}";
        int i=PositionIndex(id);
        string position=k.Kind==3?new[]{"OFF","1","2","3"}[i]:k.Kind==2?new[]{"OFF","ON"}[i]:k.Id==8?new[]{"S / マイナス","停止","N / プラス"}[i]:new[]{"マイナス","停止","プラス"}[i];
        return $"K{k.Id:00} {k.Label} {position}";
    }
    static readonly string[] ExternalModes={"CC 7bit 絶対値","CC 14bit (MSB / LSB+32)","相対 CC 2の補数","相対 CC 64中心","CC ボタン (0/127)","Note ボタン"};
    static readonly string[] ExternalFeedbackModes={"なし","CC 7bit","Note velocity","CC 14bit"};
    bool InputConflict(ExternalMidiMap a,ExternalMidiMap b){if(a.Channel!=b.Channel||(a.Mode==5)!=(b.Mode==5))return false;return a.Number==b.Number||a.Mode==1&&a.Number+32==b.Number||b.Mode==1&&b.Number+32==a.Number;}
    bool OutputConflict(ExternalMidiMap a,ExternalMidiMap b){if(a.FeedbackType==0||b.FeedbackType==0||a.FeedbackChannel!=b.FeedbackChannel||(a.FeedbackType==2)!=(b.FeedbackType==2))return false;return a.FeedbackNumber==b.FeedbackNumber||a.FeedbackType==3&&a.FeedbackNumber+32==b.FeedbackNumber||b.FeedbackType==3&&b.FeedbackNumber+32==a.FeedbackNumber;}
    FrameworkElement ExternalPage(){
        var p=new StackPanel{Margin=new Thickness(14)};
        p.Children.Add(new TextBlock{Text="外部MIDI  /  機器の割り当て",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Margin=new Thickness(0,0,0,5)});
        p.Children.Add(new TextBlock{Text="接続 → 登録 → 保存 → 操作卓へ戻る　｜　この画面では実機操作をVRChatへ送りません。",Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,FontSize=14,Margin=new Thickness(0,0,0,12)});
        BuildExternalSettings(p);
        return new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    StackPanel MidiCard(Panel parent,string title){var inner=new StackPanel{Margin=new Thickness(12)};inner.Children.Add(new TextBlock{Text=title,FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Margin=new Thickness(0,0,0,8)});parent.Children.Add(new Border{Background=B("#242629"),BorderBrush=B("#484B50"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Margin=new Thickness(3,3,3,8),Child=inner});return inner;}
    Grid MidiColumns(Panel parent){var g=new Grid();g.ColumnDefinitions.Add(new ColumnDefinition());g.ColumnDefinitions.Add(new ColumnDefinition());g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.SizeChanged+=(s,e)=>{bool narrow=g.ActualWidth<680;g.ColumnDefinitions[1].Width=narrow?new GridLength(0):new GridLength(1,GridUnitType.Star);foreach(UIElement child in g.Children){int col=Grid.GetColumn(child),row=Grid.GetRow(child);if(col==1||row==1){Grid.SetColumn(child,narrow?0:1);Grid.SetRow(child,narrow?1:0);}}};parent.Children.Add(g);return g;}
    StackPanel MidiColumn(Grid grid,int col){var p=new StackPanel{Margin=new Thickness(3)};Grid.SetColumn(p,col);grid.Children.Add(p);return p;}
    Button MidiAction(string text,Action action){var b=Btn(text,action);b.MinHeight=38;b.BorderThickness=new Thickness(1);b.FontSize=14;b.Foreground=Brushes.White;b.IsEnabledChanged+=(s,e)=>b.Opacity=b.IsEnabled?1:0.38;var caption=new TextBlock{Text=text,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8,5,8,5)};caption.SetBinding(TextBlock.ForegroundProperty,new System.Windows.Data.Binding("Foreground"){Source=b});b.Content=caption;return b;}
    void MidiListStyle(ListBox box){box.FontSize=14;ScrollViewer.SetHorizontalScrollBarVisibility(box,ScrollBarVisibility.Disabled);var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));var txt=new FrameworkElementFactory(typeof(TextBlock));txt.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding());txt.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);box.ItemTemplate=new DataTemplate{VisualTree=txt};style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));var trigger=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};trigger.Setters.Add(new Setter(Control.BackgroundProperty,B("#49351F")));trigger.Setters.Add(new Setter(Control.ForegroundProperty,Orange));style.Triggers.Add(trigger);box.ItemContainerStyle=style;}
    void BuildExternalSettings(StackPanel root){
        var device=MidiCard(root,"1  機器を接続する");var cols=MidiColumns(device);var p=MidiColumn(cols,0);var outCol=MidiColumn(cols,1);
        void Label(string text)=>p.Children.Add(new TextBlock{Text=text,Foreground=Brushes.LightGray,Margin=new Thickness(3,8,3,3),TextWrapping=TextWrapping.Wrap});
        ComboBox Combo(IEnumerable<string> items,int index=0){var c=new ComboBox{ItemsSource=items.ToArray(),SelectedIndex=index,MinHeight=34,FontSize=14,Margin=new Thickness(3)};p.Children.Add(c);return c;}
        TextBox Num(string title,int value){Label(title);var t=new TextBox{Text=value.ToString(),Background=Black,Foreground=Brushes.White,MinHeight=32,FontSize=14,Padding=new Thickness(6),Margin=new Thickness(3)};p.Children.Add(t);return t;}
        CheckBox Check(string text,bool value){var c=new CheckBox{Content=text,IsChecked=value,Foreground=Brushes.White,Margin=new Thickness(3,8,3,3)};p.Children.Add(c);return c;}
        void AddAction(string text,Action a)=>p.Children.Add(MidiAction(text,a));
        Label("物理MIDI入力（VRChat用loopMIDIとは別のポート）");var input=Combo(MidiInput.Devices().Select(d=>d.Name).Append(external.Input).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）"));input.SelectedItem=string.IsNullOrEmpty(external.Input)?"（なし）":external.Input;
        p=outCol;Label("物理MIDI出力（LED・モーターフェーダー用、任意）");var output=Combo(Midi.Devices().Select(d=>d.Name).Append(external.Output).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）"));output.SelectedItem=string.IsNullOrEmpty(external.Output)?"（なし）":external.Output;
        p=device;var auto=Check("起動時に物理MIDIも接続",external.AutoConnect);
        externalStatus=new TextBlock{Text=externalInput.Connected?"入力接続済み":"未接続",Foreground=externalInput.Connected?Green:Orange,FontSize=15,TextWrapping=TextWrapping.Wrap};p.Children.Add(externalStatus);
        var deviceActions=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};device.Children.Add(deviceActions);
        bool deviceLoading=false;
        void SaveDevices(){if(deviceLoading)return;var next=CopyExternal();next.Input=input.SelectedIndex>0?input.SelectedItem as string??"":"";next.Output=output.SelectedIndex>0?output.SelectedItem as string??"":"";next.AutoConnect=auto.IsChecked==true;
            bool changed=next.Input!=external.Input||next.Output!=external.Output;if(!CommitExternal(next,"機器設定を保存しました")){deviceLoading=true;input.SelectedItem=string.IsNullOrEmpty(external.Input)?"（なし）":external.Input;output.SelectedItem=string.IsNullOrEmpty(external.Output)?"（なし）":external.Output;auto.IsChecked=external.AutoConnect;deviceLoading=false;return;}if(changed)CloseExternal();}
        input.SelectionChanged+=(s,e)=>SaveDevices();output.SelectionChanged+=(s,e)=>SaveDevices();auto.Checked+=(s,e)=>SaveDevices();auto.Unchecked+=(s,e)=>SaveDevices();
        deviceActions.Children.Add(MidiAction("接続",()=>OpenExternal()));
        deviceActions.Children.Add(MidiAction("切断",()=>{CloseExternal();ExternalNotice("切断");}));
        deviceActions.Children.Add(MidiAction("機器一覧更新",()=>{deviceLoading=true;input.ItemsSource=MidiInput.Devices().Select(d=>d.Name).Append(external.Input).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）").ToArray();output.ItemsSource=Midi.Devices().Select(d=>d.Name).Append(external.Output).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）").ToArray();input.SelectedItem=string.IsNullOrEmpty(external.Input)?"（なし）":external.Input;output.SelectedItem=string.IsNullOrEmpty(external.Output)?"（なし）":external.Output;deviceLoading=false;}));
        var saveStatus=new TextBlock{Text=externalSaveMessage,Foreground=externalSaveError?Red:Green,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,6,3,6)};device.Children.Add(saveStatus);externalSaveStatus=saveStatus;
        Action imported=()=>{};
        var transfer=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};device.Children.Add(transfer);transfer.Children.Add(MidiAction("MIDI設定を書き出す",ExportExternal));transfer.Children.Add(MidiAction("MIDI設定を取り込む",()=>{if(ImportExternal()){deviceLoading=true;input.ItemsSource=MidiInput.Devices().Select(d=>d.Name).Append(external.Input).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）").ToArray();output.ItemsSource=Midi.Devices().Select(d=>d.Name).Append(external.Output).Where(n=>!string.IsNullOrEmpty(n)).Distinct().Prepend("（なし）").ToArray();input.SelectedItem=string.IsNullOrEmpty(external.Input)?"（なし）":external.Input;output.SelectedItem=string.IsNullOrEmpty(external.Output)?"（なし）":external.Output;auto.IsChecked=external.AutoConnect;deviceLoading=false;imported();}}));
        int[] targets=Knobs.SelectMany(k=>new[]{k.Id}.Concat(Enumerable.Range(0,PositionCount(k)).Select(i=>200+k.Id*4+i))).Concat(Enumerable.Range(100,61)).ToArray();
        Action refreshAfterBatch=()=>{};
        var switcher=new System.Windows.Controls.Primitives.UniformGrid{Columns=2,Margin=new Thickness(0,4,0,6)};root.Children.Add(switcher);
        var batchHost=new StackPanel();var singleHost=new StackPanel{Visibility=Visibility.Collapsed};root.Children.Add(batchHost);root.Children.Add(singleHost);
        Button? batchMode=null,singleMode=null;
        void SelectMode(bool batch){if((batchHost.Visibility==Visibility.Visible)==batch)return;CancelLearn();PauseBatch();batchHost.Visibility=batch?Visibility.Visible:Visibility.Collapsed;singleHost.Visibility=batch?Visibility.Collapsed:Visibility.Visible;SetSelected(batchMode!,batch);SetSelected(singleMode!,!batch);}
        batchMode=MidiAction("連続登録（複数をまとめて設定）",()=>SelectMode(true));singleMode=MidiAction("個別登録 / 編集",()=>SelectMode(false));switcher.Children.Add(batchMode);switcher.Children.Add(singleMode);SetSelected(batchMode,true);
        BuildBatchSettings(batchHost,targets,()=>refreshAfterBatch());
        var singleCols=MidiColumns(singleHost);var left=MidiColumn(singleCols,0);var right=MidiColumn(singleCols,1);p=MidiCard(left,"2  操作対象と入力を選ぶ");
        Label("操作対象");var target=Combo(targets.Select(TargetName));
        Label("入力方式");var mode=Combo(ExternalModes);var basic=p;var numbers=new Grid();numbers.ColumnDefinitions.Add(new ColumnDefinition());numbers.ColumnDefinitions.Add(new ColumnDefinition());basic.Children.Add(numbers);p=MidiColumn(numbers,0);var channel=Num("入力 Ch (1–16)",1);p=MidiColumn(numbers,1);var number=Num("CC / Note番号 (0–127)",0);p=basic;
        var inputPanel=p;
        var advanced=new Expander{Header="詳細設定：フェーダーカーブ / 反転・追従設定",Foreground=Brushes.White,Margin=new Thickness(3,12,3,4),IsExpanded=false};inputPanel.Children.Add(advanced);var curvePanel=new StackPanel();advanced.Content=curvePanel;p=curvePanel;var invert=Check("反転（Noteでは無効）",false);var pickup=Check("絶対値：現在値と一致するまで待機（ソフトテイクオーバー）",true);
        Label("フェーダーカーブ（絶対値の連続操作のみ）");var curveMode=Combo(new[]{"リニア（従来）","カスタム % カーブ","音響 dB カーブ（-∞ ～ +10 dB 等）"});
        Label("校正点：受信MIDI位置% = 制御値% または dB。1行1点、最大64点。機器の実測値を入力してください。");
        var curvePoints=new TextBox{AcceptsReturn=true,MinHeight=105,MaxHeight=210,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Black,Foreground=Brushes.White,Margin=new Thickness(3)};p.Children.Add(curvePoints);
        var curvePreview=new TextBlock{Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3)};p.Children.Add(curvePreview);
        void PreviewCurve(){curvePoints.IsEnabled=curveMode.SelectedIndex>0;if(!FaderCurve.TryCreate(curveMode.SelectedIndex,curvePoints.Text,out var c,out string error)){curvePreview.Text=error;return;}curvePreview.Text="変換プレビュー（MIDI位置 → 制御値、送信なし）: "+string.Join(" / ",new[]{0,25,50,75,100}.Select(v=>$"{v}%→{(c?.Forward(v/100.0)??v/100.0)*100:0.##}%"));}
        curveMode.SelectionChanged+=(s,e)=>{if(curveMode.SelectedIndex>0&&string.IsNullOrWhiteSpace(curvePoints.Text))curvePoints.Text=curveMode.SelectedIndex==2?FaderCurve.DbExample:FaderCurve.PercentExample;PreviewCurve();};curvePoints.TextChanged+=(s,e)=>PreviewCurve();
        AddAction("カーブ例を読み込む（実機の校正値ではありません）",()=>{if(curveMode.SelectedIndex==0)curveMode.SelectedIndex=2;curvePoints.Text=curveMode.SelectedIndex==2?FaderCurve.DbExample:FaderCurve.PercentExample;PreviewCurve();});
        p=inputPanel;Action saveSingle=()=>{};var learnAuto=Check("MIDI Learnで取得したら自動保存",external.LearnAutoSave);bool autoLoading=false;void SaveLearnOption(){if(autoLoading)return;var next=CopyExternal();next.LearnAutoSave=learnAuto.IsChecked==true;if(!CommitExternal(next,"自動保存設定を保存しました")){autoLoading=true;learnAuto.IsChecked=external.LearnAutoSave;autoLoading=false;}}learnAuto.Checked+=(s,e)=>SaveLearnOption();learnAuto.Unchecked+=(s,e)=>SaveLearnOption();learnButton=MidiAction("MIDI Learn（次の入力を取得）",()=>{PauseBatch();if(learnCapture!=null){CancelLearn();return;}if(!externalInput.Connected){ExternalNotice("先に物理MIDIを接続してください");return;}SetSelected(learnButton!,true);ExternalNotice("入力待機中：割り当てる鍵盤・つまみを操作してください");learnCapture=(type,ch,num)=>{channel.Text=ch.ToString();number.Text=(type==0xB0&&mode.SelectedIndex==1&&num>=32&&num<64?num-32:num).ToString();if(type==0x90)mode.SelectedIndex=5;else if(target.SelectedIndex>=0&&(IsMacroTarget(targets[target.SelectedIndex])||IsPositionTarget(targets[target.SelectedIndex])))mode.SelectedIndex=4;else if(mode.SelectedIndex<0||mode.SelectedIndex>=4)mode.SelectedIndex=0;if(external.LearnAutoSave)saveSingle();else ExternalNotice($"Learn: Ch{ch} {(type==0x90?"Note":"CC")} {num}（保存してください）");};});p.Children.Add(learnButton);externalEditStatus=new TextBlock{Text="MIDI Learnを押し、登録する鍵盤・つまみを操作してください。",Foreground=Orange,FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,6,3,8)};p.Children.Add(externalEditStatus);
        var feedbackExpander=new Expander{Header="詳細設定：LED・モーターフェーダー出力",Foreground=Brushes.White,Margin=new Thickness(3,12,3,4)};inputPanel.Children.Add(feedbackExpander);var feedbackPanel=new StackPanel();feedbackExpander.Content=feedbackPanel;p=feedbackPanel;
        Label("フィードバック方式（機器のMIDI仕様を確認）");var fb=Combo(ExternalFeedbackModes);var fc=Num("出力 Ch (1–16)",1);var fn=Num("出力 CC / Note番号 (0–127)",0);var off=Num("マクロ / 位置 消灯値 (0–127)",0);var mixed=Num("マクロ中間状態値 (0–127)",64);var on=Num("マクロ / 位置 点灯値 (0–127)",127);
        p=MidiCard(right,"登録済みの割り当て");var list=new ListBox{MaxHeight=420,MinHeight=180,Margin=new Thickness(3),Background=Black,Foreground=Brushes.White};Label("一覧から選択すると左側で編集できます。");MidiListStyle(list);p.Children.Add(list);
        void RefreshList(){list.ItemsSource=external.Maps.Select(m=>$"{TargetName(m.Target)}  ← Ch{m.Channel} / {ExternalModes[m.Mode]} {m.Number}  | OUT: {ExternalFeedbackModes[m.FeedbackType]}").ToArray();}
        void LoadTarget(){CancelLearn();if(target.SelectedIndex<0)return;int id=targets[target.SelectedIndex];var m=external.Maps.FirstOrDefault(x=>x.Target==id)??new ExternalMidiMap{Target=id,Mode=IsMacroTarget(id)||IsPositionTarget(id)?4:0};mode.SelectedIndex=m.Mode;channel.Text=m.Channel.ToString();number.Text=m.Number.ToString();invert.IsChecked=m.Invert;pickup.IsChecked=m.Pickup;fb.SelectedIndex=m.FeedbackType;fc.Text=m.FeedbackChannel.ToString();fn.Text=m.FeedbackNumber.ToString();off.Text=m.Off.ToString();mixed.Text=m.Mixed.ToString();on.Text=m.On.ToString();curveMode.SelectedIndex=m.CurveMode;curvePoints.Text=m.CurvePoints??"";PreviewCurve();}
        target.SelectionChanged+=(s,e)=>LoadTarget();list.SelectionChanged+=(s,e)=>{if(list.SelectedIndex>=0&&list.SelectedIndex<external.Maps.Count)target.SelectedIndex=Array.IndexOf(targets,external.Maps[list.SelectedIndex].Target);};
        p=inputPanel;saveSingle=()=>{PauseBatch();CancelLearn();if(target.SelectedIndex<0||!int.TryParse(channel.Text,out int ch)||!int.TryParse(number.Text,out int n)||!int.TryParse(fc.Text,out int och)||!int.TryParse(fn.Text,out int onum)||!int.TryParse(off.Text,out int ov)||!int.TryParse(on.Text,out int av)||!int.TryParse(mixed.Text,out int mv)){SaveNotice("数値を確認してください",true);return;}var m=new ExternalMidiMap{Target=targets[target.SelectedIndex],Mode=mode.SelectedIndex,Channel=ch,Number=n,Invert=invert.IsChecked==true,Pickup=pickup.IsChecked==true,FeedbackType=fb.SelectedIndex,FeedbackChannel=och,FeedbackNumber=onum,Off=ov,On=av,Mixed=mv,CurveMode=curveMode.SelectedIndex,CurvePoints=curvePoints.Text};if(!FaderCurve.TryCreate(m.CurveMode,m.CurvePoints,out _,out string curveError)){SaveNotice(curveError,true);return;}if(!ValidExternalMap(m)){SaveNotice("入力方式・対象・範囲を確認してください（個別位置はCC/Noteボタン、カーブは絶対値の連続操作のみ、14bit番号0–31）",true);return;}if(external.Maps.Any(x=>x.Target!=m.Target&&(InputConflict(x,m)||OutputConflict(x,m)))){SaveNotice("別の割当とMIDIアドレスが重複しています",true);return;}var next=CopyExternal();next.Maps.RemoveAll(x=>x.Target==m.Target);next.Maps.Add(m);if(!CommitExternal(next,"割当保存: "+TargetName(m.Target)))return;ReleaseExternal();ResetExternalTracking();RefreshList();};AddAction("割当を保存 / 更新",saveSingle);
        p=right;AddAction("選択対象の割当を削除",()=>{PauseBatch();if(target.SelectedIndex<0)return;var next=CopyExternal();next.Maps.RemoveAll(x=>x.Target==targets[target.SelectedIndex]);if(!CommitExternal(next,"割当を削除しました"))return;ReleaseExternal();ResetExternalTracking();RefreshList();LoadTarget();});RefreshList();LoadTarget();refreshAfterBatch=()=>{RefreshList();LoadTarget();};imported=()=>{autoLoading=true;learnAuto.IsChecked=external.LearnAutoSave;autoLoading=false;RefreshList();LoadTarget();};
    }
}
