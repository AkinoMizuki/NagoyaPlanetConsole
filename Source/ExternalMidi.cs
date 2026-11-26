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
    public List<ExternalMidiMap> Maps {get;set;}=new();
}
public sealed class ExternalMidiMap
{
    // Target 0..39 = knob ID; 100..160 = macro ID + 100.
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
    bool ExternalReady=>midi.Connected&&FeedbackLive&&externalRemoteValid;
    void InitExternal(){external=Load("external-midi.json",new ExternalMidiConfig());external.Maps??=new();var loaded=external.Maps.Where(ValidExternalMap).ToArray();external.Maps=new();foreach(var m in loaded)if(!external.Maps.Any(x=>x.Target==m.Target||InputConflict(x,m)||OutputConflict(x,m)))external.Maps.Add(m);externalTimer.Tick+=(s,e)=>PollExternal();externalTimer.Start();if(external.AutoConnect)OpenExternal();}
    static bool ValidExternalMap(ExternalMidiMap m)=>ValidCurve(m)&&m!=null&&(Knobs.Any(k=>k.Id==m.Target)||m.Target>=100&&m.Target<161)&&m.Channel>=1&&m.Channel<=16&&m.Number>=0&&m.Number<=127&&m.Mode>=0&&m.Mode<=5&&(m.Target>=100?m.Mode>=4:m.Mode<4)&&(m.Mode!=1||m.Number<32)&&m.FeedbackType>=0&&m.FeedbackType<=3&&m.FeedbackChannel>=1&&m.FeedbackChannel<=16&&m.FeedbackNumber>=0&&m.FeedbackNumber<=127&&(m.FeedbackType!=3||m.FeedbackNumber<32)&&(m.Target<100||m.FeedbackType!=3)&&m.Off>=0&&m.Off<=127&&m.On>=0&&m.On<=127&&m.Mixed>=0&&m.Mixed<=127;
    static bool ValidCurve(ExternalMidiMap m){if(m==null)return false;if(m.CurveMode!=0&&(m.Mode>1||!Knobs.Any(k=>k.Id==m.Target&&(k.Kind==0||k.Kind==4))))return false;if(!FaderCurve.TryCreate(m.CurveMode,m.CurvePoints,out var curve,out _))return false;m.Curve=curve;return true;}
    static double CurveForward(ExternalMidiMap m,double n)=>m.Curve?.Forward(n)??n;
    static double CurveInverse(ExternalMidiMap m,double n)=>m.Curve?.Inverse(n)??n;
    void ExternalNotice(string text){if(externalStatus!=null)externalStatus.Text=text;Log("EXT MIDI: "+text);}
    void CancelLearn(){learnCapture=null;if(learnButton!=null)SetSelected(learnButton,false);}
    void ResetExternalTracking(){pickupPrevious.Clear();pickupAcquired.Clear();externalExpected.Clear();msb.Clear();lsb.Clear();sentFeedback.Clear();echoed.Clear();}
    void ReleaseExternal(){foreach(int id in externalHeld.ToArray())ExternalMacro(id,false);}
    void CloseExternal(){CancelLearn();ReleaseExternal();externalInput.Dispose();externalOutput.Dispose();ResetExternalTracking();externalWasLive=false;}
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
    static int DecodeRelative(int mode,int v)=>mode==2?(v==64?0:v<64?v:v-128):v-64;
    void PollExternal(){if(!externalInput.Connected)return;if(Environment.TickCount64>=nextDeviceCheck){nextDeviceCheck=Environment.TickCount64+2000;if(MidiInput.Devices().Count(d=>d.Name==external.Input)!=1){CloseExternal();ExternalNotice("入力機器が切断されました");return;}}if(externalInput.Overflow()){CloseExternal();ExternalNotice("入力エラーまたは入力過多のため停止。再接続してください");return;}bool live=ExternalReady;if(externalWasLive&&!live){ReleaseExternal();ResetExternalTracking();}externalWasLive=live;if(!externalInput.HasData)return;var absolute=new Dictionary<int,(ExternalMidiMap Map,double Value)>();var relative=new Dictionary<int,(ExternalMidiMap Map,double Delta)>();for(int i=0;i<512&&externalInput.TryRead(out uint raw);i++){
        int type=(int)(raw&0xF0),ch=(int)(raw&15)+1,num=(int)((raw>>8)&127),v=(int)((raw>>16)&127);if(type==0x80){type=0x90;v=0;}
        uint key=MessageKey(type,ch,num,v);if(echoed.TryGetValue(key,out long at)&&Environment.TickCount64-at<120)continue;
        if(learnCapture!=null){if(type==0x90&&v==0)continue;var capture=learnCapture;CancelLearn();capture(type,ch,num);continue;}
        if(tab==2||!live)continue;
        foreach(var m in external.Maps){if(m.Channel!=ch)continue;if(m.Mode==5){if(type==0x90&&num==m.Number)ExternalMacro(m.Target-100,v>0);continue;}if(type!=0xB0)continue;
            if(m.Mode==1){if(num==m.Number)msb[m.Target]=v;else if(num==m.Number+32)lsb[m.Target]=v;else continue;if(!msb.TryGetValue(m.Target,out int hi))continue;lsb.TryGetValue(m.Target,out int lo);double n=((hi<<7)|lo)/16383.0;absolute[m.Target]=(m,m.Invert?1-n:n);continue;}
            if(num!=m.Number)continue;if(m.Mode==4){ExternalMacro(m.Target-100,m.Invert?v<64:v>=64);continue;}
            if(m.Mode==0){double n=v/127.0;absolute[m.Target]=(m,m.Invert?1-n:n);}else{int delta=DecodeRelative(m.Mode,v);if(delta!=0){relative.TryGetValue(m.Target,out var prior);relative[m.Target]=(m,prior.Delta+(m.Invert?-delta:delta));}}
        }
    }foreach(var a in absolute.Values)ExternalKnob(a.Map,a.Value,false);foreach(var a in relative.Values)if(a.Delta!=0)ExternalKnob(a.Map,a.Delta,true);}
    void ObserveExternalRemote(double[] nv,int[] nm){if(externalRemoteValid){foreach(var k in Knobs){if(Math.Abs(nv[k.Id]-remoteExternalValues[k.Id])<0.0001)continue;bool expected=externalExpected.TryGetValue(k.Id,out var e)&&(Math.Abs(nv[k.Id]-e.Value)<=0.002||Environment.TickCount64-e.At<400);if(!expected){pickupAcquired.Remove(k.Id);pickupPrevious.Remove(k.Id);externalExpected.Remove(k.Id);}}}Array.Copy(nv,remoteExternalValues,40);Array.Copy(nm,remoteExternalMacros,61);externalRemoteValid=true;}
    static uint MessageKey(int type,int ch,int num,int v)=>(uint)(type|(ch-1)|(num<<8)|(v<<16));
    bool FeedbackSend(int type,int ch,int number,int value){if(!externalOutput.Short(type,ch-1,number,value))return false;echoed[MessageKey(type,ch,number,value)]=Environment.TickCount64;return true;}
    void ExternalFeedback(){if(!externalOutput.Connected||!ExternalReady||tab==2)return;foreach(var m in external.Maps){if(m.FeedbackType==0)continue;int q;if(m.Target>=100){int s=remoteExternalMacros[m.Target-100];q=s==2?m.On:s==1?m.Mixed:m.Off;}else{var k=Knobs.First(x=>x.Id==m.Target);double n=CurveInverse(m,ToNormalized(k,remoteExternalValues[k.Id]));if(m.Invert)n=1-n;q=(int)Math.Round(n*(m.FeedbackType==3?16383:127));}if(sentFeedback.TryGetValue(m.Target,out int old)&&old==q)continue;
        bool ok=m.FeedbackType==3?FeedbackSend(0xB0,m.FeedbackChannel,m.FeedbackNumber,q>>7)&&FeedbackSend(0xB0,m.FeedbackChannel,m.FeedbackNumber+32,q&127):FeedbackSend(m.FeedbackType==2?0x90:0xB0,m.FeedbackChannel,m.FeedbackNumber,q);if(ok)sentFeedback[m.Target]=q;else{externalOutput.Dispose();ExternalNotice("物理MIDI出力失敗：再接続してください");return;}
    }if(echoed.Count>1024){long now=Environment.TickCount64;foreach(var key in echoed.Where(p=>now-p.Value>=120).Select(p=>p.Key).ToArray())echoed.Remove(key);}}
    string TargetName(int id)=>id>=100?$"M{id-100:00} {labels[id-100]}":$"K{id:00} {Knobs.First(k=>k.Id==id).Label}";
    static readonly string[] ExternalModes={"CC 7bit 絶対値","CC 14bit (MSB / LSB+32)","相対 CC 2の補数","相対 CC 64中心","CC ボタン (0/127)","Note ボタン"};
    static readonly string[] ExternalFeedbackModes={"なし","CC 7bit","Note velocity","CC 14bit"};
    bool InputConflict(ExternalMidiMap a,ExternalMidiMap b){if(a.Channel!=b.Channel||(a.Mode==5)!=(b.Mode==5))return false;return a.Number==b.Number||a.Mode==1&&a.Number+32==b.Number||b.Mode==1&&b.Number+32==a.Number;}
    bool OutputConflict(ExternalMidiMap a,ExternalMidiMap b){if(a.FeedbackType==0||b.FeedbackType==0||a.FeedbackChannel!=b.FeedbackChannel||(a.FeedbackType==2)!=(b.FeedbackType==2))return false;return a.FeedbackNumber==b.FeedbackNumber||a.FeedbackType==3&&a.FeedbackNumber+32==b.FeedbackNumber||b.FeedbackType==3&&b.FeedbackNumber+32==a.FeedbackNumber;}
    void BuildExternalSettings(StackPanel p){
        void Label(string text)=>p.Children.Add(new TextBlock{Text=text,Foreground=Brushes.LightGray,Margin=new Thickness(3,8,3,3),TextWrapping=TextWrapping.Wrap});
        ComboBox Combo(IEnumerable<string> items,int index=0){var c=new ComboBox{ItemsSource=items.ToArray(),SelectedIndex=index,MinHeight=32,Margin=new Thickness(3)};p.Children.Add(c);return c;}
        TextBox Num(string title,int value){Label(title);var t=new TextBox{Text=value.ToString(),Background=Black,Foreground=Brushes.White,MinHeight=29,Margin=new Thickness(3)};p.Children.Add(t);return t;}
        CheckBox Check(string text,bool value){var c=new CheckBox{Content=text,IsChecked=value,Foreground=Brushes.White,Margin=new Thickness(3,8,3,3)};p.Children.Add(c);return c;}
        void AddAction(string text,Action a)=>p.Children.Add(Btn(text,a));
        Label("物理MIDI入力（VRChat用loopMIDIとは別のポート）");var input=Combo(MidiInput.Devices().Select(d=>d.Name).Prepend("（なし）"));input.SelectedItem=string.IsNullOrEmpty(external.Input)?"（なし）":external.Input;
        Label("物理MIDI出力（LED・モーターフェーダー用、任意）");var output=Combo(Midi.Devices().Select(d=>d.Name).Prepend("（なし）"));output.SelectedItem=string.IsNullOrEmpty(external.Output)?"（なし）":external.Output;
        var auto=Check("起動時に物理MIDIも接続",external.AutoConnect);
        externalStatus=new TextBlock{Text=externalInput.Connected?"入力接続済み":"未接続",Foreground=Green,TextWrapping=TextWrapping.Wrap};p.Children.Add(externalStatus);
        AddAction("物理MIDI接続 / 設定保存",()=>{external.Input=input.SelectedIndex>0?input.SelectedItem as string??"":"";external.Output=output.SelectedIndex>0?output.SelectedItem as string??"":"";external.AutoConnect=auto.IsChecked==true;Save("external-midi.json",external);OpenExternal();});
        AddAction("物理MIDI切断",()=>{CloseExternal();ExternalNotice("切断");});
        AddAction("デバイス一覧更新",()=>{CancelLearn();var a=input.SelectedItem;var b=output.SelectedItem;input.ItemsSource=MidiInput.Devices().Select(d=>d.Name).Prepend("（なし）").ToArray();output.ItemsSource=Midi.Devices().Select(d=>d.Name).Prepend("（なし）").ToArray();input.SelectedItem=a;output.SelectedItem=b;});
        Label("割当対象（設定中は実機操作を送信しません）");int[] targets=Knobs.Select(k=>k.Id).Concat(Enumerable.Range(100,61)).ToArray();var target=Combo(targets.Select(TargetName));
        Label("入力方式");var mode=Combo(ExternalModes);var channel=Num("入力 Ch (1–16)",1);var number=Num("入力 CC / Note番号 (0–127)",0);var invert=Check("反転（Noteでは無効）",false);var pickup=Check("絶対値：現在値と一致するまで待機（ソフトテイクオーバー）",true);
        Label("フェーダーカーブ（絶対値の連続操作のみ）");var curveMode=Combo(new[]{"リニア（従来）","カスタム % カーブ","音響 dB カーブ（-∞ ～ +10 dB 等）"});
        Label("校正点：受信MIDI位置% = 制御値% または dB。1行1点、最大64点。機器の実測値を入力してください。");
        var curvePoints=new TextBox{AcceptsReturn=true,MinHeight=105,MaxHeight=210,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Black,Foreground=Brushes.White,Margin=new Thickness(3)};p.Children.Add(curvePoints);
        var curvePreview=new TextBlock{Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3)};p.Children.Add(curvePreview);
        void PreviewCurve(){curvePoints.IsEnabled=curveMode.SelectedIndex>0;if(!FaderCurve.TryCreate(curveMode.SelectedIndex,curvePoints.Text,out var c,out string error)){curvePreview.Text=error;return;}curvePreview.Text="変換プレビュー（MIDI位置 → 制御値、送信なし）: "+string.Join(" / ",new[]{0,25,50,75,100}.Select(v=>$"{v}%→{(c?.Forward(v/100.0)??v/100.0)*100:0.##}%"));}
        curveMode.SelectionChanged+=(s,e)=>{if(curveMode.SelectedIndex>0&&string.IsNullOrWhiteSpace(curvePoints.Text))curvePoints.Text=curveMode.SelectedIndex==2?FaderCurve.DbExample:FaderCurve.PercentExample;PreviewCurve();};curvePoints.TextChanged+=(s,e)=>PreviewCurve();
        AddAction("カーブ例を読み込む（実機の校正値ではありません）",()=>{if(curveMode.SelectedIndex==0)curveMode.SelectedIndex=2;curvePoints.Text=curveMode.SelectedIndex==2?FaderCurve.DbExample:FaderCurve.PercentExample;PreviewCurve();});
        learnButton=Btn("MIDI Learn（次の入力を取得）",()=>{if(learnCapture!=null){CancelLearn();return;}if(!externalInput.Connected){ExternalNotice("先に物理MIDIを接続してください");return;}SetSelected(learnButton!,true);learnCapture=(type,ch,num)=>{channel.Text=ch.ToString();number.Text=(type==0xB0&&mode.SelectedIndex==1&&num>=32&&num<64?num-32:num).ToString();if(type==0x90)mode.SelectedIndex=5;else if(target.SelectedIndex>=Knobs.Length)mode.SelectedIndex=4;else if(mode.SelectedIndex<0||mode.SelectedIndex>=4)mode.SelectedIndex=0;ExternalNotice($"Learn: Ch{ch} {(type==0x90?"Note":"CC")} {num}（保存してください）");};});p.Children.Add(learnButton);
        Label("フィードバック方式（機器のMIDI仕様を確認）");var fb=Combo(ExternalFeedbackModes);var fc=Num("出力 Ch (1–16)",1);var fn=Num("出力 CC / Note番号 (0–127)",0);var off=Num("マクロ消灯値 (0–127)",0);var mixed=Num("マクロ中間状態値 (0–127)",64);var on=Num("マクロ点灯値 (0–127)",127);
        var list=new ListBox{MaxHeight=230,MinHeight=65,Margin=new Thickness(3),Background=Black,Foreground=Brushes.White};Label("登録済み割当（選択で編集）");p.Children.Add(list);
        void RefreshList(){list.ItemsSource=external.Maps.Select(m=>$"{TargetName(m.Target)}  ← Ch{m.Channel} / {ExternalModes[m.Mode]} {m.Number}  | OUT: {ExternalFeedbackModes[m.FeedbackType]}").ToArray();}
        void LoadTarget(){CancelLearn();if(target.SelectedIndex<0)return;int id=targets[target.SelectedIndex];var m=external.Maps.FirstOrDefault(x=>x.Target==id)??new ExternalMidiMap{Target=id,Mode=id>=100?4:0};mode.SelectedIndex=m.Mode;channel.Text=m.Channel.ToString();number.Text=m.Number.ToString();invert.IsChecked=m.Invert;pickup.IsChecked=m.Pickup;fb.SelectedIndex=m.FeedbackType;fc.Text=m.FeedbackChannel.ToString();fn.Text=m.FeedbackNumber.ToString();off.Text=m.Off.ToString();mixed.Text=m.Mixed.ToString();on.Text=m.On.ToString();curveMode.SelectedIndex=m.CurveMode;curvePoints.Text=m.CurvePoints??"";PreviewCurve();}
        target.SelectionChanged+=(s,e)=>LoadTarget();list.SelectionChanged+=(s,e)=>{if(list.SelectedIndex>=0&&list.SelectedIndex<external.Maps.Count)target.SelectedIndex=Array.IndexOf(targets,external.Maps[list.SelectedIndex].Target);};
        AddAction("割当を保存 / 更新",()=>{CancelLearn();if(target.SelectedIndex<0||!int.TryParse(channel.Text,out int ch)||!int.TryParse(number.Text,out int n)||!int.TryParse(fc.Text,out int och)||!int.TryParse(fn.Text,out int onum)||!int.TryParse(off.Text,out int ov)||!int.TryParse(on.Text,out int av)||!int.TryParse(mixed.Text,out int mv)){ExternalNotice("数値を確認してください");return;}var m=new ExternalMidiMap{Target=targets[target.SelectedIndex],Mode=mode.SelectedIndex,Channel=ch,Number=n,Invert=invert.IsChecked==true,Pickup=pickup.IsChecked==true,FeedbackType=fb.SelectedIndex,FeedbackChannel=och,FeedbackNumber=onum,Off=ov,On=av,Mixed=mv,CurveMode=curveMode.SelectedIndex,CurvePoints=curvePoints.Text};if(!FaderCurve.TryCreate(m.CurveMode,m.CurvePoints,out _,out string curveError)){ExternalNotice(curveError);return;}if(!ValidExternalMap(m)){ExternalNotice("入力方式・対象・範囲を確認してください（カーブは絶対値の連続操作のみ、14bit番号0–31）");return;}if(external.Maps.Any(x=>x.Target!=m.Target&&(InputConflict(x,m)||OutputConflict(x,m)))){ExternalNotice("別の割当とMIDIアドレスが重複しています");return;}ReleaseExternal();external.Maps.RemoveAll(x=>x.Target==m.Target);external.Maps.Add(m);ResetExternalTracking();Save("external-midi.json",external);RefreshList();ExternalNotice("割当保存: "+TargetName(m.Target));});
        AddAction("選択対象の割当を削除",()=>{if(target.SelectedIndex<0)return;ReleaseExternal();external.Maps.RemoveAll(x=>x.Target==targets[target.SelectedIndex]);ResetExternalTracking();Save("external-midi.json",external);RefreshList();LoadTarget();});RefreshList();LoadTarget();
    }
}
