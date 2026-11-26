using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;
namespace NagoyaConsole;
public sealed partial class ConsoleWindow
{
    string externalSaveMessage="設定の変更は自動保存されます。";
    bool externalSaveError;
    TextBlock? externalSaveStatus;
    bool externalLoadBlocked;
    static void WriteSettingsFile<T>(string path,T value,bool backup=true){
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});stream.Flush(true);}
            if(File.Exists(path))File.Replace(tmp,path,backup?path+".bak":null);else File.Move(tmp,path);
        }finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    ExternalMidiConfig CopyExternal()=>new(){Input=external.Input,Output=external.Output,AutoConnect=external.AutoConnect,LearnAutoSave=external.LearnAutoSave,Maps=external.Maps.ToList()};
    ExternalMidiConfig ReadExternal(string path){var c=JsonSerializer.Deserialize<ExternalMidiConfig>(File.ReadAllText(path))??throw new InvalidDataException("空の設定です");if(c.Input==null||c.Output==null||c.Maps==null||c.Maps.Count>141)throw new InvalidDataException("設定形式が不正です");
        foreach(var m in c.Maps)if(!ValidExternalMap(m))throw new InvalidDataException("無効な割当があります");
        for(int i=0;i<c.Maps.Count;i++)for(int j=i+1;j<c.Maps.Count;j++)if(c.Maps[i].Target==c.Maps[j].Target||InputConflict(c.Maps[i],c.Maps[j])||OutputConflict(c.Maps[i],c.Maps[j]))throw new InvalidDataException("割当が重複しています");return c;}
    void SaveNotice(string message,bool error=false){externalSaveMessage=message;externalSaveError=error;if(externalSaveStatus!=null){externalSaveStatus.Text=message;externalSaveStatus.Foreground=error?Red:Green;}ExternalNotice(message);if(externalEditStatus!=null)externalEditStatus.Foreground=error?Red:Green;}
    ExternalMidiConfig LoadExternalConfig(){string path=Path.Combine(data,"external-midi.json");if(!File.Exists(path)&&!File.Exists(path+".bak"))return new();try{return ReadExternal(path);}catch(Exception e){try{var recovered=ReadExternal(path+".bak");externalLoadBlocked=true;SaveNotice("設定の読込失敗。バックアップを使用中。保存を再開するには正常なJSONを取り込んでください。 "+e.Message,true);return recovered;}catch{externalLoadBlocked=true;SaveNotice("設定を読み込めません。元ファイルを保護しています。正常なJSONを取り込んでください。 "+e.Message,true);return new();}}}
    bool CommitExternal(ExternalMidiConfig next,string message){if(externalLoadBlocked){SaveNotice("読み込みエラーのため保存停止中。正常な設定JSONを取り込んでください。",true);return false;}try{WriteSettingsFile(Path.Combine(data,"external-midi.json"),next);external=next;SaveNotice(message);return true;}catch(Exception e){SaveNotice("保存失敗: "+e.Message,true);return false;}}
    void ExportExternal(){if(externalLoadBlocked){SaveNotice("読み込みエラー中の設定は書き出せません。",true);return;}var dlg=new Microsoft.Win32.SaveFileDialog{Filter="MIDI設定 JSON (*.json)|*.json",FileName="external-midi.json"};if(dlg.ShowDialog()!=true)return;try{WriteSettingsFile(dlg.FileName,external);SaveNotice("設定を書き出しました");}catch(Exception e){SaveNotice("書き出し失敗: "+e.Message,true);}}
    bool ImportExternal(){var dlg=new Microsoft.Win32.OpenFileDialog{Filter="MIDI設定 JSON (*.json)|*.json"};if(dlg.ShowDialog()!=true)return false;try{var next=ReadExternal(dlg.FileName);if(MessageBox.Show("現在の外部MIDI設定を置き換えます。未保存の連続登録も破棄します。","設定の取り込み",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return false;string path=Path.Combine(data,"external-midi.json");if(externalLoadBlocked&&File.Exists(path))File.Copy(path,path+".invalid-"+Guid.NewGuid().ToString("N"));WriteSettingsFile(path,next,!externalLoadBlocked);externalLoadBlocked=false;CloseExternal();CancelBatch();external=next;SaveNotice("設定を取り込みました。必要に応じて機器を接続してください。");return true;}catch(Exception e){SaveNotice("取り込み失敗: "+e.Message,true);return false;}}
}
