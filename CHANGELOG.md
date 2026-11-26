# 更新履歴 / NagoyaPlanetConsole

## v0.1.21 — 2026-11-26

- 外部MIDI機器・自動接続設定の自動保存。
- 個別MIDI Learn取得時の自動保存（初期ON）。
- 連続登録をタブ切替時に一時停止し、再開・一括保存可能。
- JSONの一時ファイル経由保存、前世代バックアップ、失敗表示、外部MIDI設定の取り込み・書き出し。
- ソフトMIDI保存失敗時の表示と設定復元。
- 指定の.gitignoreをそのまま同梱。
- Windows実ビルド、WPF描画、実機通信は未実施。

## 過去の更新

各バージョンの詳細はDocuments内の変更点.mdを参照。

### StaticChecks（旧検証記録）

```text
2026-11-26 integrated package static checks
Windows sources / build script / reference images byte-identical to v0.1.6 WithDocuments_MIDI.
Latest two UdonSharp sources copied byte-identically from delivered v0.1.6 Center and v0.1.7 MIDI.
Old manual addition file removed.
No Windows or Unity compilation performed.
8b555025bee87c895a1178e66ad0180709531ed900a4535a928d90ed1663d9c9  Source/Program.cs
6c01725db0eedfe30bfe479ba8478b277221a46525e1a09e57f470ee321c283c  UdonSharp/CenterConsoleController.cs
4160255e98b799b25fa7f9967c621f9d866f1b93928c097181223ef335feec76  UdonSharp/MidiConsoleController.cs

Historical check record:
v0.1.6: source diff and ZIP integrity checked. Windows/WPF build and runtime not tested.

2026-11-26 v0.1.9: 37 knob IDs preserved; 61 macro IDs partitioned without duplicates; both runtime UdonSharp files byte-identical to v0.1.8. Windows/Unity execution not tested.

2026-11-26 v0.1.12: ZIP base comparison passed; unchanged Center, exporter, project, build script, MIDI spec, KnobVisual and PaintedMetal code, MIDI input and macro feedback methods. Python model: 8 cardinal/intercardinal directions, wrap, ties, moving, digit masks and sample packet fields checked. No C#/Unity build or runtime tests.

2026-11-26 v0.1.13: Compared all files with v0.1.12. All UdonSharp files, build script, project, existing MIDI/INFO protocol and unrelated source sections unchanged. Python static layout model: all 61 IDs unique and complete; moved header and macro button bounds/non-overlap verified. Windows/Unity runtime and compilation NOT performed.
```

### StaticChecks_v0.1.16（旧検証記録）

```text
UdonSharp files: byte-identical to v0.1.15.
Knob IDs: 37 distinct in actual Program.cs.
Python reference model: exhaustive 7-bit and 14-bit split/recombine checks passed.
Windows WPF / C# compile, native input, hardware, VRChat tests: NOT RUN.
```

### StaticChecks_v0.1.17（旧検証記録）

```text
Python reference model: both example curves, exhaustive 7-bit and 14-bit monotonicity and forward/inverse round-trip, normal and reversed: PASS.
UdonSharp byte comparison with v0.1.16: PASS.
C#/.NET build and hardware tests: NOT RUN.
```

### StaticChecks_v0.1.18（旧検証記録）

```text
Python static ID checks: 37 knobs, 61 macros, 43 position targets, 141 total, no collision. K03 values -1/0/+1; existing Ch3 encoding 0/1/2. UdonSharp, FaderCurve.cs, InfoDisplay.cs byte-identical. No .NET SDK or Windows runtime test.
```
