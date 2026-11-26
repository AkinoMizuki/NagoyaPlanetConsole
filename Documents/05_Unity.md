# Unity / UdonSharp 導入 — 2026-11-26 統合版
既存リポジトリ: https://github.com/AkinoMizuki/Planetarium_World_NAGOYA
公式: https://creators.vrchat.com/worlds/udon/midi/realtime-midi/
指定Skills: https://github.com/niaka3dayo/agent-skills-vrc-udon/tree/dev

## ソース導入
1. Scene、既存ソースと .meta をバックアップ。テスト環境で作業する。
2. `UdonSharp/CenterConsoleController.cs` と `UdonSharp/MidiConsoleController.cs` を `Assets/AkinoMizuki/Model/Nagoya/Console/UdonSharp/` に配置・上書きする。既存 .meta は維持する。前者は2026-11-25提供のユーザーソースを基準としたMIDI追加版、後者はv0.1.7。別途の手動追記やパッチは不要。導入後にローカルで追加変更した場合は差分を確認してから上書きする。
3. 既存のMidiConsoleController GameObjectを使用する。新規導入時はGameObjectを作り、MidiConsoleControllerとVRC Midi Listenerを追加する。
4. ConsoleにNagoyaConsoleSystemのCenterConsoleControllerを登録。右上のコンテキストメニューから `Collect MIDI Macro References` を実行する。Editor上で既存MacroPressRelay、MaterialChangeSw、SlideSwButton、ToggleSwButtonを収集。手動登録不要。Unity Consoleで `[Nagoya MIDI] Macro routes n/61` と未取得IDを確認する。61件全取得は未確認。
5. VRC Midi ListenerのBehaviourに同じGameObjectのUdonBehaviourを設定。Active EventsはCCのみON。Local Input EnabledとFeedback Enabledを閉じた検証環境でONにする。入力には初回同期完了とOwnerが必要。既存操作ロックとの統合は未完了。
6. 下記Program Asset参照切れ対処を確認し、Scene保存、UdonSharpコンパイル、Build & Testを実行。

## MIDI接続
loopMIDIを先に起動。実在するポート名をWindows版のMIDI出力とVRChatの起動オプションで一致させる。2026-11-25の画像では `loopMIDI Port` が実在し、Steam側は `Nagoya Console MIDI` だった。

```text
--midi="loopMIDI Port"
```

SteamからVRChatを起動する場合は再起動が必要。SDKから直接起動する場合はSteamの引数が適用されるとは限らない。ローカル検証の候補は `--midi="loopMIDI Port" --watch-worlds --no-vr` でSteam起動し、SDKのローカルビルドを読み込む方法。実際のSDK表示・挙動は未検証。

## Program Asset参照切れ（2026-11-25のビルドエラー）
`Source C# script on MidiConsoleController (UdonSharp.UdonSharpProgramAsset) is null`

これはProgram AssetのC#参照がnullというエラー。ソース上書きだけで直るとは限らない。
- ProjectでMidiConsoleControllerを検索し、同名のProgram Asset (.asset) を確認。
- InspectorのSource Script（必要に応じDebug Inspector）に正しいMidiConsoleController.csが参照されているか確認し、参照切れなら再設定する。
- 複数の同名Assetがある場合は使用中と未使用を区別し、参照切れの旧Assetも確認する。削除はバックアップと参照確認の後に限る。
- .metaを削除しない。SDK再インストールやLibrary削除は不要。
- 本ZIPにはUnityが生成する.asset/.metaを同梱しない。既存GUIDを保護するためで、参照修復はUnity上で必要。

## 動作検証
WindowsのTX CC表示とloopMIDIのTotal data増加を確認。LOCAL CCは未送信。ログ受信だけではMIDI受信成功を意味しない。恒星調光、セレクター、ダイヤルOFF範囲、旧Relayマクロ、新方式マクロ、RESETを順番に確認する。既存Relayを優先し、次にMaterialChangeSw、SlideSwButton、ToggleSwButtonの_Play()を解放時に1回実行。未取得IDは実行しない。既存7×ulong同期とビット配置は維持。

未検証: Unity実コンパイル、Program Asset修復、MIDI入力の実受信、全61ルート、操作ロック、解放喪失対策。今回の統合ではソースを追加修正せず、Git変更も行わない。

## v0.1.12 INFO追加時（既存導入済み）
`MidiConsoleController.cs` のみ差し替え。既存 `.meta` は維持。`Status Monitor` に IntegratedSystem が参照する既存 StatusMonitor を割当。コンテキストメニュー `Collect INFO Status Monitor` は同一Sceneで候補が1つのときのみ登録。複数候補は手動登録。Feedback Enabled を有効にし Scene を保存、コンパイル、再ビルド。StatusMonitor / IntegratedSystem / AstroInfo / CenterConsoleController の差し替えは不要。
