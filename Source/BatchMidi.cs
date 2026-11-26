using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;
namespace NagoyaConsole;

public sealed partial class ConsoleWindow
{
    Action<int,int,int,int>? batchCapture;
    Action? batchCancel;
    Action? batchPause;
    void PauseBatch(){batchCapture=null;batchPause?.Invoke();}
    void CancelBatch(){batchCapture=null;var cancel=batchCancel;batchCancel=null;batchPause=null;cancel?.Invoke();}
    void BuildBatchSettings(StackPanel parent,int[] targets,Action refresh)
    {
        var layout=MidiColumns(parent);var left=MidiColumn(layout,0);var right=MidiColumn(layout,1);
        var p=MidiCard(left,"2  登録する操作を選ぶ");
        void Label(string text)=>p.Children.Add(new TextBlock{Text=text,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,6,3,3)});
        Label("登録範囲を選択してください。任意選択では対象をクリックして追加・解除できます。");
        var groups=new[]{"セレクター全ポジション（−・停止・＋など）","全37操作（連続値）","全61マクロ","全割当対象","任意選択（複数選択）"};
        var group=new ComboBox{ItemsSource=groups,SelectedIndex=0,Margin=new Thickness(3),MinHeight=34,FontSize=14};p.Children.Add(group);
        var selectors=Knobs.Where(k=>PositionCount(k)>0).ToArray();
        var selector=new ComboBox{ItemsSource=selectors.Select(k=>TargetName(k.Id)).ToArray(),SelectedIndex=0,Margin=new Thickness(3),MinHeight=34,FontSize=14};p.Children.Add(selector);
        var choices=new ListBox{ItemsSource=targets.Select(TargetName).ToArray(),SelectionMode=SelectionMode.Multiple,Height=220,Background=Black,Foreground=Brushes.White,Margin=new Thickness(3),Visibility=Visibility.Collapsed};MidiListStyle(choices);p.Children.Add(choices);
        Label("つまみ・フェーダーの入力方式（鍵盤は自動判定）");
        var continuous=new ComboBox{ItemsSource=ExternalModes.Take(4).ToArray(),SelectedIndex=0,Margin=new Thickness(3),MinHeight=34,FontSize=14};p.Children.Add(continuous);
        var result=MidiCard(right,"3  順番に入力して保存する");
        var notice=new TextBlock{Foreground=Orange,TextWrapping=TextWrapping.Wrap,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(3,8,3,8)};result.Children.Add(notice);
        var progressText=new TextBlock{Foreground=Brushes.LightGray,FontSize=14,Margin=new Thickness(3,0,3,8)};result.Children.Add(progressText);
        var rows=new ListBox{MinHeight=100,MaxHeight=230,Background=Black,Foreground=Brushes.White,Margin=new Thickness(3)};MidiListStyle(rows);rows.ItemTemplate=null;result.Children.Add(rows);
        int[] order=Array.Empty<int>();int index=0;bool running=false;
        var staged=new Dictionary<int,ExternalMidiMap>();var skipped=new HashSet<int>();
        // Track recent CC addresses so a moving fader cannot enroll its trailing messages as another control.
        var recent=new Dictionary<(int Type,int Ch,int Num),long>();
        Button? start=null,save=null,back=null,skip=null,stop=null;
        void Render(string? message=null){
            rows.Items.Clear();
            for(int i=0;i<order.Length;i++){int id=order[i];bool acquired=staged.TryGetValue(id,out var m);rows.Items.Add(new ListBoxItem{Content=new TextBlock{Text=TargetName(id)+"  ←  "+(acquired?$"Ch{m!.Channel} / {ExternalModes[m.Mode]} {m.Number}":skipped.Contains(id)?"スキップ":"未取得"),TextWrapping=TextWrapping.Wrap},Foreground=running&&i==index?Orange:acquired?Green:Brushes.LightGray});}
            if(index<order.Length){rows.ScrollIntoView(rows.Items[index]);}
            progressText.Text=order.Length==0?"対象を選んで開始すると、ここに登録一覧が表示されます。":$"取得 {staged.Count} / {order.Length} 件　スキップ {skipped.Count} 件";
            notice.Text=message??(running&&index<order.Length?$"次の入力: {TargetName(order[index])}  ({staged.Count}/{order.Length})":$"取得済み {staged.Count}/{order.Length}（未保存）");
            SetSelected(start!,running);start!.IsEnabled=order.Length==0||(!running&&index<order.Length);start.Opacity=running?1:start.IsEnabled?1:0.38; if(start.Content is TextBlock caption)caption.Text=order.Length>0&&!running&&index<order.Length?"連続登録を再開":"連続登録を開始";save!.IsEnabled=staged.Count>0;SetSelected(save,save.IsEnabled);back!.IsEnabled=order.Length>0&&index>0;skip!.IsEnabled=running&&index<order.Length;stop!.IsEnabled=order.Length>0;
            group.IsEnabled=selector.IsEnabled=choices.IsEnabled=continuous.IsEnabled=order.Length==0;
        }
        void Reset(){running=false;order=Array.Empty<int>();index=0;staged.Clear();skipped.Clear();recent.Clear();Render("左側で対象を選び、登録を開始してください");}
        void Advance(){index++;if(index>=order.Length){running=false;batchCapture=null;}Render();}
        void Capture(int type,int ch,int num,int v){
            if(!running||index>=order.Length)return;
            int id=order[index];bool button=IsMacroTarget(id)||IsPositionTarget(id);
            var prior=external.Maps.FirstOrDefault(m=>m.Target==id);
            if(type==0x90&&(v==0||!button))return;
            if(type==0xB0&&button&&(prior?.Invert==true?v>=64:v<64))return;
            int mode=button?(type==0x90?5:4):(prior?.Mode??continuous.SelectedIndex);
            if(mode==1){if(num>=32)return;} // Only enroll the MSB; the paired LSB is never a separate input here.
            var key=(type,ch,num);long now=Environment.TickCount64;
            bool trailing=recent.TryGetValue(key,out long last)&&now-last<250;recent[key]=now;
            if(trailing)return;
            if(type==0xB0&&staged.Values.Any(m=>m.Mode==1&&m.Channel==ch&&m.Number+32==num))return;
            var map=prior==null?new ExternalMidiMap{Target=id}:JsonSerializer.Deserialize<ExternalMidiMap>(JsonSerializer.Serialize(prior))!;
            map.Mode=mode;map.Channel=ch;map.Number=num;
            if(button){map.Pickup=false;map.CurveMode=0;map.CurvePoints="";if(mode==5)map.Invert=false;}
            if(!ValidExternalMap(map)){Render("入力方式または既存カーブ設定が不正です。個別登録で調整してください。");return;}
            if(staged.Values.Any(m=>m.Target!=id&&(InputConflict(m,map)||OutputConflict(m,map)))){Render("取得済みの割当と重複しています。別の入力を操作してください。");return;}
            staged[id]=map;skipped.Remove(id);Advance();
        }
        group.SelectionChanged+=(s,e)=>{selector.Visibility=group.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;choices.Visibility=group.SelectedIndex==4?Visibility.Visible:Visibility.Collapsed;};
        start=MidiAction("連続登録を開始",()=>{
            if(order.Length>0){if(!externalInput.Connected){notice.Text="先に物理MIDIを接続してください";return;}if(!running&&index<order.Length){for(int i=0;i<2048&&externalInput.TryRead(out _);i++){}running=true;batchCapture=Capture;Render();}return;}
            if(!externalInput.Connected){notice.Text="先に物理MIDIを接続してください";return;}
            CancelLearn();CancelBatch();
            order=group.SelectedIndex switch{0=>selector.SelectedIndex<0?Array.Empty<int>():Enumerable.Range(0,PositionCount(selectors[selector.SelectedIndex])).Select(i=>200+selectors[selector.SelectedIndex].Id*4+i).ToArray(),1=>Knobs.Select(k=>k.Id).ToArray(),2=>Enumerable.Range(100,61).ToArray(),3=>targets,4=>choices.SelectedItems.Cast<string>().Select(n=>Array.IndexOf(targets.Select(TargetName).ToArray(),n)).Where(i=>i>=0).OrderBy(i=>i).Select(i=>targets[i]).ToArray(),_=>Array.Empty<int>()};
            if(order.Length==0){notice.Text="対象を選択してください";return;}
            for(int i=0;i<2048&&externalInput.TryRead(out _);i++){} // discard pre-start messages
            index=0;running=true;batchCancel=Reset;batchPause=()=>{if(running){running=false;Render("一時停止中：取得結果は保持されています。再開または保存してください。");}};batchCapture=Capture;Render();
        });p.Children.Add(start);
        var actions=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};result.Children.Add(actions);
        back=MidiAction("1つ戻る / 再取得",()=>{if(index==0)return;index--;staged.Remove(order[index]);skipped.Remove(order[index]);running=true;batchCapture=Capture;Render();});actions.Children.Add(back);
        skip=MidiAction("スキップ",()=>{if(!running||index>=order.Length)return;staged.Remove(order[index]);skipped.Add(order[index]);Advance();});actions.Children.Add(skip);
        stop=MidiAction("中止（未保存分を破棄）",CancelBatch);result.Children.Add(stop);
        save=MidiAction("取得した割当を一括保存",()=>{
            if(staged.Count==0)return;
            var merged=external.Maps.Where(m=>!staged.ContainsKey(m.Target)).Concat(staged.Values).ToList();
            for(int i=0;i<merged.Count;i++)for(int j=i+1;j<merged.Count;j++)if(InputConflict(merged[i],merged[j])||OutputConflict(merged[i],merged[j])){Render($"重複: {TargetName(merged[i].Target)} / {TargetName(merged[j].Target)}。未保存です。");return;}
            var next=CopyExternal();next.Maps=merged;
            if(!CommitExternal(next,$"{staged.Count}件を一括保存しました")){Render(externalSaveMessage);return;}
            int count=staged.Count;ReleaseExternal();ResetExternalTracking();CancelBatch();refresh();notice.Text=$"{count}件を一括保存しました";ExternalNotice(notice.Text);
        });result.Children.Add(save);Render("左側で対象を選び、登録を開始してください");
    }
}
