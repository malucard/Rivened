using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Rivened {
	public class ScriptCompiler {
		public static (bool, string) ApplyPatches(string filename, string src, string[] patches) {
			if(patches.Length == 0) return (true, src);
			var insertIdx = src.LastIndexOf("\ntrailer.");
			var lines = new List<string>(src.Split('\n'));
			for(int i = 0; i < patches.Length; i++) {
				var patch = patches[i];
				Trace.Assert(patch[0] == ':');
				var mode = patch[1];
				Trace.Assert(mode == '<' || mode == '>' || mode == '='); // insert before, insert after, replace
				var lineNumEnd = patch.IndexOf(':', 1);
				var lineNum = int.Parse(patch.AsSpan(2, lineNumEnd - 2)) - 1;
				var endOfSetup = patch.IndexOf('\n');
				if(endOfSetup == -1) endOfSetup = patch.Length;
				var lineInScript = lines[lineNum];
				while(lineInScript.StartsWith('&')) {
					lineInScript = lineInScript[(lineInScript.IndexOf(": ") + 2)..];
				}
				var patchCheck = patch[(lineNumEnd + 1)..endOfSetup];
				if(lineInScript == patchCheck || (patch[endOfSetup - 1] == '*' && lineInScript.StartsWith(patchCheck[..(patchCheck.Length - 1)]))) {
					var szStr = patch[(patch.IndexOf('.') + 1)..patch.IndexOf(' ')];
					var sz = int.Parse(szStr);
					if(sz < 10) {
						return (false, i + 1 + ": can only patch instructions of 10 or higher length for now");
					}
					if(endOfSetup == patch.Length) {
						lines[lineNum] = "0." + szStr + " 00-00-00-00-00-00-00-00";
						for(int j = 10; j < sz; j++) {
							lines[lineNum] += "-00";
						}
						if(mode != '=') {
							return (false, i + 1 + " in " + filename + ": empty patch must be in replacement mode (=)");
						}
					} else {
						lines[lineNum] = "Loop_Cond." + szStr + " &P" + (i + 1) + "-00-00-00-00-00-00";
						for(int j = 10; j < sz; j++) {
							lines[lineNum] += "-00";
						}
						lines[lineNum + 1] = "&PR" + (i + 1) + ": " + lines[lineNum + 1];
						if(mode == '<') {
							lines[^2] += "\n&P" + (i + 1) + ": " + patch[(endOfSetup + 1)..] + '\n' + lineInScript + "\nLoop_Cond.10 &PR" + (i + 1) + "-00-00-00-00-00-00";
						} else if(mode == '>') {
							lines[^2] += "\n&P" + (i + 1) + ": " + lineInScript + '\n' + patch[(endOfSetup + 1)..] + "\nLoop_Cond.10 &PR" + (i + 1) + "-00-00-00-00-00-00";
						} else if(mode == '=') {
							lines[^2] += "\n&P" + (i + 1) + ": " + patch[(endOfSetup + 1)..] + "\nLoop_Cond.10 &PR" + (i + 1) + "-00-00-00-00-00-00";
						}
					}
				} else if(lineInScript.StartsWith("Loop_Cond")) {
					Program.Log("Skipping patch " + i + " in " + filename + " as it appears to be applied; if it was updated, revert all and fetch again");
					continue;
				} else {
					return (false, i + 1 + " in " + filename + ": line does not match patch");
				}
			}
			return (true, string.Join('\n', lines));
		}

		public static bool Compile(string filename, string source, out byte[] arr, out string err) {
			var isDataBin = filename == "DATA.BIN";
			arr = null;
			using var stream = new MemoryStream();
			using var wr = new BinaryWriter(stream);
			var strings = new List<(int, string)>();
			var lines = source.Split('\n');
			var labels = new Dictionary<string, ushort>();
			var pendingLabelRefs = new List<(int, string)>();
			byte[] trailer = null;
			for(var lineIdx = 0; lineIdx < lines.Length; lineIdx++) {
				var line = lines[lineIdx];
				var i = 0;
				var dotIdx = 0;
				for(; i < line.Length; i++) {
					if(!char.IsWhiteSpace(line[i])) {
						if(line[i] == '&') {
							var endOfName = line.IndexOf(':', i + 1);
							wr.Flush();
							labels[line[(i + 1)..endOfName]] = (ushort) stream.Position;
							i = endOfName;
						} else if(line[i] == '#') {
							goto skip_line;
						} else {
							dotIdx = line.IndexOf('.', i);
							if(dotIdx == -1) {
								goto skip_line;
							}
							break; // found the opcode, hopefully
						}
					}
				}
				var startPos = stream.Position;
				var opcode = line[i..dotIdx];
				if(opcode == "header" || opcode == "trailer" || isDataBin) {
					// not an actual opcode
				} else if(Enum.TryParse(typeof(Opcode), opcode, out var openum)) {
					wr.Write((byte) (int) openum);
				} else {
					err = lineIdx + 1 + ":" + (i + 1) + ": could not parse '" + opcode + "' into opcode";
					return false;
				}
				var lenLen = 0;
				for(; dotIdx + lenLen + 1 < line.Length; lenLen++) {
					if(!char.IsDigit(line[dotIdx + lenLen + 1])) {
						break;
					}
				}
				if(lenLen == 0) {
					err = lineIdx + 1 + ":" + (dotIdx + 2) + ": expected command length after '.'";
					return false;
				}
				var curWr = wr;
				if(uint.TryParse(line[(dotIdx + 1)..(dotIdx + lenLen + 1)], out uint oplen)) {
					if(opcode == "header") {
						Trace.Assert(stream.Position == 0);
						wr.Write((byte) oplen);
					} else if(opcode == "trailer") {
						curWr = new BinaryWriter(new MemoryStream((int) oplen));
					} else if(!isDataBin) {
						wr.Write((byte) oplen);
					}
				} else {
					err = lineIdx + 1 + ":" + (dotIdx + 2) + ": could not parse '" + line[(dotIdx + 1)..(dotIdx + 3)].Trim() + "' into command length";
					return false;
				}
				var stringPos = new List<int>();
				var stringPosIdx = 0;
				for(i = dotIdx + lenLen + 1; i < line.Length; i++) {
					if(line[i] == '-' || char.IsWhiteSpace(line[i])) {
						// ignore
					} else if(line[i] == 'S') {
						stringPos.Add((int) stream.Position);
						curWr.Write((ushort) 0);
					} else if(line[i] == '§') {
						int end = line.IndexOf('§', i + 1);
						if(end == -1) {
							err = lineIdx + 1 + ":" + (i + 1) + ": §-string must be terminated with another §";
							return false;
						}
						strings.Add((stringPos[stringPosIdx++], line[(i + 1)..end].Trim()));
						i = end;
					} else if(line[i] == '@') {
						if(opcode == "route") {
							strings.Add((stringPos[stringPosIdx++], line[(i + 1)..].Trim() + "\0%T2"));
						} else {
							strings.Add((stringPos[stringPosIdx++], line[(i + 1)..].Trim()));
						}
						break;
					//} else if(line[i] == '#') {
					//	break;
					} else if(line[i] == '&') {
						var end = i + 1;
						while((line[end] >= '0' && line[end] <= '9') || (line[end] >= 'A' && line[end] <= 'Z') || (line[end] >= 'a' && line[end] <= 'z') || line[end] == '_') {
							end++;
						}
						var label = line[(i + 1)..end];
						if(label.Length == 0) {
							err = lineIdx + 1 + ":" + (i + 1) + ": could not parse label reference";
							return false;
						}
						if(labels.TryGetValue(label, out var location)) {
							curWr.Write((ushort) location);
						} else {
							curWr.Flush(); // this is fine because trailer (which replaces the stream) wouldn't have a &
							pendingLabelRefs.Add(((int) stream.Position, label));
							curWr.Write((ushort) 0);
						}
						i = end - 1;
					} else if(i + 1 < line.Length && ((line[i] >= '0' && line[i] <= '9') || (line[i] >= 'A' && line[i] <= 'F') || (line[i] >= 'a' && line[i] <= 'f'))
							&& ((line[i + 1] >= '0' && line[i + 1] <= '9') || (line[i + 1] >= 'A' && line[i + 1] <= 'F') || (line[i + 1] >= 'a' && line[i + 1] <= 'f'))) {
						try {
							curWr.Write(Convert.ToByte(line[i..(i + 2)], 16));
							i++;
						} catch {
							err = lineIdx + 1 + ":" + (i + 1) + ": could not parse data byte";
							return false;
						}
					} else {
						err = lineIdx + 1 + ":" + (i + 1) + ": unexpected character '" + line[i] + '\'';
						return false;
					}
				}
				if(opcode == "trailer") {
					var trailerStream = (MemoryStream) curWr.BaseStream;
					curWr.Dispose();
					trailerStream.Flush();
					trailer = trailerStream.ToArray();
					if(trailer.Length != oplen) {
						err = lineIdx + 1 + ":1: trailer has length " + trailer.Length + " instead of the expected " + oplen;
						return false;
					}
					trailerStream.Dispose();
				} else if(stream.Position != startPos + oplen) {
					err = lineIdx + 1 + ":1: instruction has length " + (stream.Position - startPos) + " instead of the expected " + oplen;
					return false;
				}
				skip_line:;
			}
			for(int i = 0; i < pendingLabelRefs.Count; i++) {
				wr.Flush();
				var posBackup = stream.Position;
				stream.Position = pendingLabelRefs[i].Item1;
				wr.Write((ushort) labels[pendingLabelRefs[i].Item2]);
				wr.Flush();
				stream.Position = posBackup;
			}
			//if(isDataBin) { // this doesn't seem necessary
			//	if(wr.BaseStream.Position > 0x2640) {
			//		throw new Exception("Did not expect DATA.BIN tokens to pass 0x2640");
			//	}
			//	wr.BaseStream.Position = 0x2640;
			//}
			var useBig5 = MainWindow.Instance.UseBig5;
			Encoding encoding = null;
			if(!useBig5) {
				encoding = (Encoding) Encoding.GetEncoding("Shift-JIS").Clone();
				encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
				encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
			}
			// the start of strings is technically aligned up to 0x10 originally, but that doesn't seem necessary or beneficial
			foreach(var pair in strings) {
				var stringPos = (int) wr.BaseStream.Position;
				if(stringPos > 0xFFFF) {
					err = "string passes 64kb mark: " + pair.Item2;
					return false;
				}
				wr.Flush();
				wr.BaseStream.Position = pair.Item1;
				wr.Write((ushort) stringPos);
				wr.Flush();
				wr.BaseStream.Position = stringPos;
				var str = pair.Item2.Replace('«', '《').Replace('»', '》');
				if(MainWindow.Instance.EnTweaks) {
					str = EnTweaks.ApplyEnTweaks(str);
				}
				if(str.Length > 0) {
					if(str[0] == '【') {
						int bracketEnd = str.IndexOf('】') + 1;
						if(bracketEnd != 0) {
							if(useBig5) {
								if(JpToChNames.TryGetValue(str[..bracketEnd], out var chName)) {
									str = chName + str[bracketEnd..];
								}
							} else {
								if(ChToJpNames.TryGetValue(str[..bracketEnd], out var jpName)) {
									str = jpName + str[bracketEnd..];
								}
							}
						}
					}
					if(useBig5) {
						wr.Write(Big5.Encode(str));
					} else {
						try {
							wr.Write(encoding.GetBytes(str.Replace('«', 'Ы').Replace('»', 'Я')));
						} catch {
							Console.WriteLine("Error on line: " + str);	
							throw;
						}
					}
				}
				wr.Write((byte) 0);
			}
			if(trailer == null) {
				err = "script has no trailer";
				return false;
			}
			wr.Write(trailer);
			stream.Flush();
			arr = stream.ToArray();
			err = "";
			return true;
		}

		public static readonly Dictionary<string, string> JpToChNames = new() {
			["【１１７女】"] = "【一一七報時女】",
			["【お兄ちゃん】"] = "【哥哥】",
			["【キモ男】"] = "【變態男】",
			["【サイ１】"] = "【Ψ成員１】",
			["【サイ２】"] = "【Ψ成員２】",
			["【サイクＡ】"] = "【ΨｃＡ】",
			["【サイクＢ】"] = "【ΨｃＢ】",
			["【サイクＣ】"] = "【ΨｃＣ】",
			["【シムＡ】"] = "【志村Ａ】",
			["【シムＢ】"] = "【志村Ｂ】",
			["【シムＣ】"] = "【志村Ｃ】",
			["【タンゴ】"] = "【誕吾】",
			["【チサト】"] = "【千里】",
			["【ひろし】"] = "【弘】",
			["【フロント】"] = "【櫃檯】",
			["【ボス】"] = "【老大】",
			["【マイナ】"] = "【舞菜】",
			["【ミホ】"] = "【美保】",
			["【ミュウ】"] = "【繆】",
			["【ミュウの父】"] = "【繆父】",
			["【ミュウの母】"] = "【繆母】",
			["【ミュウの義父】"] = "【繆的義父】",
			["【ミュウの義母】"] = "【繆的義母】",
			["【ミラＡ】"] = "【幻象Ａ】",
			["【ミラＢ】"] = "【幻象Ｂ】",
			["【ミラＣ】"] = "【幻象Ｃ】",
			["【ミラＤ】"] = "【幻象Ｄ】",
			["【ミラＳ】"] = "【幻象Ｓ】",
			["【ミラＳＴＵ】"] = "【幻象ＳＴＵ】",
			["【ミラＴ】"] = "【幻象Ｔ】",
			["【ミラＵ】"] = "【幻象Ｕ】",
			["【ミラＶ】"] = "【幻象Ｖ】",
			["【ミラＷ】"] = "【幻象Ｗ】",
			["【ミラＸ】"] = "【幻象Ｘ】",
			["【ミラＹ】"] = "【幻象Ｙ】",
			["【ミラ女】"] = "【幻象女】",
			["【ミラ男】"] = "【幻象男】",
			["【メイ】"] = "【冥】",
			["【ラジ女】"] = "【女播音員】",
			["【ラジ男】"] = "【男播音員】",
			["【伊野瀬】"] = "【伊野瀨】",
			["【医師】"] = "【醫師】",
			["【猿】"] = "【猴】",
			["【遠くの男】"] = "【遠方男子】",
			["【科学捜査官】"] = "【科學搜查官】",
			["【学者】"] = "【學者】",
			["【患者】"] = "【患者】",
			["【看護師】"] = "【護士】",
			["【機動隊員Ａ】"] = "【機動隊員Ａ】",
			["【機動隊員Ｂ】"] = "【機動隊員Ｂ】",
			["【機動隊員Ｃ】"] = "【機動隊員Ｃ】",
			["【機動隊員達】"] = "【機動隊員們】",
			["【機動隊員達】"] = "【機動隊員們】",
			["【牛】"] = "【牛】",
			["【群集】"] = "【群集】",
			["【刑事部の捜査官】"] = "【刑事部的搜查官】",
			["【刑事役】"] = "【刑警角色】",
			["【警官】"] = "【警官】",
			["【警官Ａ】"] = "【警官Ａ】",
			["【警察官】"] = "【警察官】",
			["【犬】"] = "【狗】",
			["【研女Ｂ】"] = "【研究員女Ｂ】",
			["【研男Ａ】"] = "【研究員男Ａ】",
			["【研男Ｃ】"] = "【研究員男Ｃ】",
			["【虎】"] = "【虎】",
			["【公安上層部】"] = "【公安上層部】",
			["【高林】"] = "【高林】",
			["【死神人形】"] = "【死神人偶】",
			["【蛇】"] = "【蛇】",
			["【従業Ａ】"] = "【工作Ａ】",
			["【従業Ｂ】"] = "【工作Ｂ】",
			["【従業員Ａ】"] = "【服務生Ａ】",
			["【従業員Ｂ】"] = "【服務生Ｂ】",
			["【女子Ａ】"] = "【女子Ａ】",
			["【女子Ｂ】"] = "【女子Ｂ】",
			["【女子Ｃ】"] = "【女子Ｃ】",
			["【女子Ｄ】"] = "【女子Ｄ】",
			["【女性客】"] = "【女客人】",
			["【信号】"] = "【信號】",
			["【慎久郎】"] = "【慎久郎】",
			["【新米役】"] = "【新人角色】",
			["【真稲父】"] = "【真稻父】",
			["【真稲母】"] = "【真稻母】",
			["【真琴】"] = "【真琴】",
			["【真琴の自殺したホテル】"] = "【真琴自殺的飯店】",
			["【杉本さん】"] = "【杉本先生】",
			["【鼠】"] = "【鼠】",
			["【捜査１課長】"] = "【搜查一課課長】",
			["【隊長】"] = "【隊長】",
			["【大手町】"] = "【大手町】",
			["【誕吾】"] = "【誕吾】",
			["【男子Ａ】"] = "【男子Ａ】",
			["【男子Ｂ】"] = "【男子Ｂ】",
			["【男子Ｃ】"] = "【男子Ｃ】",
			["【男子Ｄ】"] = "【男子Ｄ】",
			["【男子Ｓ】"] = "【男子Ｓ】",
			["【男性客】"] = "【男客人】",
			["【中年男】"] = "【中年男子】",
			["【猪】"] = "【豬】",
			["【弟】"] = "【弟】",
			["【天気予報】"] = "【天氣預報台】",
			["【兎】"] = "【兔】",
			["【桃ビキ】"] = "【桃色泳裝】",
			["【同級生１】"] = "【同學１】",
			["【同級生２】"] = "【同學２】",
			["【同級生３】"] = "【同學３】",
			["【馬】"] = "【馬】",
			["【白ビキ】"] = "【白色泳裝】",
			["【秘密結社員Ａ】"] = "【秘密組織成員Ａ】",
			["【秘密結社員Ｂ】"] = "【秘密組織成員Ｂ】",
			["【秘密結社員Ｘ】"] = "【秘密組織成員Ｘ】",
			["【秘密結社員ＸＹＺ】"] = "【秘密組織成員ＸＹＺ】",
			["【妹】"] = "【妹】",
			["【霧寺】"] = "【霧寺】",
			["【霧寺が】"] = "【霧寺】",
			["【鳴海】"] = "【鳴海】",
			["【羊】"] = "【羊】",
			["【龍】"] = "【龍】",
			["【錬丸】"] = "【鍊丸】",
			["【錬丸Ｚ】"] = "【鍊丸Ｚ】",
			["【遊々】"] = "【遊遊】",
			["【オメガ】"] = "【御目賀】",
			["【男】"] = "【男】",
			["【執刀医】"] = "【執刀醫生】",
			["【ミラージュ】"] = "【幻象】",
			["【志村クラブ】"] = "【志村俱樂部】",
			["【センターの女】"] = "【女接聽員】",
			["【モールス】"] = "【摩斯密碼】",
			["【ガード】"] = "【守衛】",
			["【女】"] = "【女】",
			["【？？？】"] = "【？？？】"
		};
		public static readonly Dictionary<string, string> ChToJpNames = new() {
			["【一一七報時女】"] = "【１１７女】",
			["【哥哥】"] = "【お兄ちゃん】",
			["【變態男】"] = "【キモ男】",
			["【Ψ成員１】"] = "【サイ１】",
			["【Ψ成員２】"] = "【サイ２】",
			["【ΨｃＡ】"] = "【サイクＡ】",
			["【ΨｃＢ】"] = "【サイクＢ】",
			["【ΨｃＣ】"] = "【サイクＣ】",
			["【志村Ａ】"] = "【シムＡ】",
			["【志村Ｂ】"] = "【シムＢ】",
			["【志村Ｃ】"] = "【シムＣ】",
			["【誕吾】"] = "【タンゴ】",
			["【千里】"] = "【チサト】",
			["【弘】"] = "【ひろし】",
			["【櫃檯】"] = "【フロント】",
			["【老大】"] = "【ボス】",
			["【舞菜】"] = "【マイナ】",
			["【美保】"] = "【ミホ】",
			["【繆】"] = "【ミュウ】",
			["【繆父】"] = "【ミュウの父】",
			["【繆母】"] = "【ミュウの母】",
			["【繆的義父】"] = "【ミュウの義父】",
			["【繆的義母】"] = "【ミュウの義母】",
			["【幻象Ａ】"] = "【ミラＡ】",
			["【幻象Ｂ】"] = "【ミラＢ】",
			["【幻象Ｃ】"] = "【ミラＣ】",
			["【幻象Ｄ】"] = "【ミラＤ】",
			["【幻象Ｓ】"] = "【ミラＳ】",
			["【幻象ＳＴＵ】"] = "【ミラＳＴＵ】",
			["【幻象Ｔ】"] = "【ミラＴ】",
			["【幻象Ｕ】"] = "【ミラＵ】",
			["【幻象Ｖ】"] = "【ミラＶ】",
			["【幻象Ｗ】"] = "【ミラＷ】",
			["【幻象Ｘ】"] = "【ミラＸ】",
			["【幻象Ｙ】"] = "【ミラＹ】",
			["【幻象女】"] = "【ミラ女】",
			["【幻象男】"] = "【ミラ男】",
			["【冥】"] = "【メイ】",
			["【女播音員】"] = "【ラジ女】",
			["【男播音員】"] = "【ラジ男】",
			["【伊野瀨】"] = "【伊野瀬】",
			["【醫師】"] = "【医師】",
			["【猴】"] = "【猿】",
			["【遠方男子】"] = "【遠くの男】",
			["【科學搜查官】"] = "【科学捜査官】",
			["【學者】"] = "【学者】",
			["【患者】"] = "【患者】",
			["【護士】"] = "【看護師】",
			["【機動隊員Ａ】"] = "【機動隊員Ａ】",
			["【機動隊員Ｂ】"] = "【機動隊員Ｂ】",
			["【機動隊員Ｃ】"] = "【機動隊員Ｃ】",
			["【機動隊員們】"] = "【機動隊員達】",
			["【機動隊員們】"] = "【機動隊員達】",
			["【牛】"] = "【牛】",
			["【群集】"] = "【群集】",
			["【刑事部的搜查官】"] = "【刑事部の捜査官】",
			["【刑警角色】"] = "【刑事役】",
			["【警官】"] = "【警官】",
			["【警官Ａ】"] = "【警官Ａ】",
			["【警察官】"] = "【警察官】",
			["【狗】"] = "【犬】",
			["【研究員女Ｂ】"] = "【研女Ｂ】",
			["【研究員男Ａ】"] = "【研男Ａ】",
			["【研究員男Ｃ】"] = "【研男Ｃ】",
			["【虎】"] = "【虎】",
			["【公安上層部】"] = "【公安上層部】",
			["【高林】"] = "【高林】",
			["【死神人偶】"] = "【死神人形】",
			["【蛇】"] = "【蛇】",
			["【工作Ａ】"] = "【従業Ａ】",
			["【工作Ｂ】"] = "【従業Ｂ】",
			["【服務生Ａ】"] = "【従業員Ａ】",
			["【服務生Ｂ】"] = "【従業員Ｂ】",
			["【女子Ａ】"] = "【女子Ａ】",
			["【女子Ｂ】"] = "【女子Ｂ】",
			["【女子Ｃ】"] = "【女子Ｃ】",
			["【女子Ｄ】"] = "【女子Ｄ】",
			["【女客人】"] = "【女性客】",
			["【信號】"] = "【信号】",
			["【慎久郎】"] = "【慎久郎】",
			["【新人角色】"] = "【新米役】",
			["【真稻父】"] = "【真稲父】",
			["【真稻母】"] = "【真稲母】",
			["【真琴】"] = "【真琴】",
			["【真琴自殺的飯店】"] = "【真琴の自殺したホテル】",
			["【杉本先生】"] = "【杉本さん】",
			["【鼠】"] = "【鼠】",
			["【搜查一課課長】"] = "【捜査１課長】",
			["【隊長】"] = "【隊長】",
			["【大手町】"] = "【大手町】",
			["【誕吾】"] = "【誕吾】",
			["【男子Ａ】"] = "【男子Ａ】",
			["【男子Ｂ】"] = "【男子Ｂ】",
			["【男子Ｃ】"] = "【男子Ｃ】",
			["【男子Ｄ】"] = "【男子Ｄ】",
			["【男子Ｓ】"] = "【男子Ｓ】",
			["【男客人】"] = "【男性客】",
			["【中年男子】"] = "【中年男】",
			["【豬】"] = "【猪】",
			["【弟】"] = "【弟】",
			["【天氣預報台】"] = "【天気予報】",
			["【兔】"] = "【兎】",
			["【桃色泳裝】"] = "【桃ビキ】",
			["【同學１】"] = "【同級生１】",
			["【同學２】"] = "【同級生２】",
			["【同學３】"] = "【同級生３】",
			["【馬】"] = "【馬】",
			["【白色泳裝】"] = "【白ビキ】",
			["【秘密組織成員Ａ】"] = "【秘密結社員Ａ】",
			["【秘密組織成員Ｂ】"] = "【秘密結社員Ｂ】",
			["【秘密組織成員Ｘ】"] = "【秘密結社員Ｘ】",
			["【秘密組織成員ＸＹＺ】"] = "【秘密結社員ＸＹＺ】",
			["【妹】"] = "【妹】",
			["【霧寺】"] = "【霧寺】",
			["【霧寺】"] = "【霧寺が】",
			["【鳴海】"] = "【鳴海】",
			["【羊】"] = "【羊】",
			["【龍】"] = "【龍】",
			["【鍊丸】"] = "【錬丸】",
			["【鍊丸Ｚ】"] = "【錬丸Ｚ】",
			["【遊遊】"] = "【遊々】",
			["【御目賀】"] = "【オメガ】",
			["【男】"] = "【男】",
			["【執刀醫生】"] = "【執刀医】",
			["【幻象】"] = "【ミラージュ】",
			["【志村俱樂部】"] = "【志村クラブ】",
			["【女接聽員】"] = "【センターの女】",
			["【摩斯密碼】"] = "【モールス】",
			["【守衛】"] = "【ガード】",
			["【女】"] = "【女】",
			["【？？？】"] = "【？？？】"
		};
	}
}