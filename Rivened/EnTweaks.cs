using System;
using System.Threading.Tasks.Dataflow;
using GLib;

namespace Rivened {
	public class FontSizeData {
		private byte[] Data = null;

		public bool Valid => Data != null;

		public FontSizeData(IFile path) {
			var afs = new AFS(path);
			var entries = afs.Entries;
			foreach(var e in entries) {
				if(e.Name == "DFKYK424.FNI") {
					Data = e.Load(afs);
				}
			}
			if(Data == null) {
				Program.Log("Could not load font size data");
			}
		}

		public (byte, byte) GetWidthAndPadding(int idx) {
			if(idx * 4 + 2 > Data.Length) {
				if(idx >= 0 && idx <= 0xFEFE) { // valid Big5 character unsupported in font, let it pass
					return (0, 0);
				}
				return (255, 255);
			}
			return (Data[idx * 4 + 1], Data[idx * 4 + 2]);
		}
	}

	public static class EnTweaks {
		//{'А', 'Б', 'В', 'Г', 'Д', 'Е', 'Ё', 'Ж', 'З', 'И', 'Й', 'К', 'Л', 'М', 'Н', 'О', 'П', 'Р', 'С', 'Т', 'У', 'Ф', 'Х', 'Ц', 'Ч', 'Ш', 'Щ', 'Ъ', 'Ы', 'Ь', 'Э', 'Ю', 'Я'}
		public static readonly char[] UPPER_ITALICS = new[] {'А', 'Б', 'В', 'Г', 'Д', 'Е', 'Ж', 'З', 'И', 'Й', 'К', 'Л', 'М', 'Н', 'О', 'П', 'Р', 'С', 'Т', 'У', 'Ф', 'Х', 'Ц', 'Ч', 'Ш', 'Щ', 'Ъ', 'Ы', 'Ь', 'Э', 'Ю', 'Я'};
		//{'а', 'б', 'в', 'г', 'д', 'е', 'ё', 'ж', 'з', 'и', 'й', 'к', 'л', 'м', 'н', 'о', 'п', 'р', 'с', 'т', 'у', 'ф', 'х', 'ц', 'ч', 'ш', 'щ', 'ъ', 'ы', 'ь', 'э', 'ю', 'я'}
		public static readonly char[] LOWER_ITALICS = new[] {'а', 'б', 'в', 'г', 'д', 'е', 'ж', 'з', 'и', 'й', 'к', 'л', 'м', 'н', 'о', 'п', 'р', 'с', 'т', 'у', 'ф', 'х', 'ц', 'ч', 'ш', 'щ', 'ъ', 'ы', 'ь', 'э', 'ю', 'я'};

		public static string ApplyEnTweaks(string replacement) {
			if(replacement.Contains("】「") && replacement.EndsWith('」')) {
				replacement = replacement[..^1] + '”';
			}
			replacement = replacement.Replace("】「", "】“")
				.Replace("」%", "”%")
				.Replace("--", "—");
			var openIdx = 0;
			var open = false;
			for(int k = 0; k < replacement.Length; k++) {
				if(replacement[k] == '\'') {
					if(k + 1 >= replacement.Length || k == 0 ||
							!(char.IsLetter(replacement[k - 1]) && (!open || char.IsLetter(replacement[k + 1])))) {
						if(open) {
							replacement = replacement[..openIdx] + '‘' + replacement[(openIdx + 1)..k] + '’' + replacement[(k + 1)..];
						} else {
							openIdx = k;
						}
						open = !open;
					}
				}
			}
			open = false;
			for(int k = 0; k < replacement.Length; k++) {
				if(replacement[k] == '"') {
					if(open) {
						replacement = replacement[..openIdx] + '“' + replacement[(openIdx + 1)..k] + '”' + replacement[(k + 1)..];
					} else {
						openIdx = k;
					}
					open = !open;
				}
			}
			open = false;
			for(int k = 0; k < replacement.Length; k++) {
				if(char.IsHighSurrogate(replacement[k])) {
					k++;
					continue;
				}
				if(k + 2 < replacement.Length && replacement[k] == '.' && replacement[k + 1] == '.' && replacement[k + 2] == '.') {
					replacement = replacement[..k] + '…' + replacement[(k + 3)..];
				//} else if(k + 1 < replacement.Length && replacement[k] == ',' && replacement[k + 1] == ' ') {
				//	replacement = replacement[..k] + 'ы' + replacement[(k + 2)..];
				} else if(replacement[k] == '*') {
					replacement = replacement[..k] + replacement[(k + 1)..];
					open = !open;
					k--;
				} else if(open) {
					if(replacement[k] >= 'A' && replacement[k] <= 'Z') {
						replacement = replacement[..k] + UPPER_ITALICS[replacement[k] - 'A'] + replacement[(k + 1)..];
					} else if(replacement[k] >= 'a' && replacement[k] <= 'z') {
						replacement = replacement[..k] + LOWER_ITALICS[replacement[k] - 'a'] + replacement[(k + 1)..];
					} else if(replacement[k] == 'Ï') {
						replacement = replacement[..k] + 'Ъ' + replacement[(k + 1)..];
					} else if(replacement[k] == 'é') {
						replacement = replacement[..k] + 'Э' + replacement[(k + 1)..];
					} else if(replacement[k] == 'ï') {
						replacement = replacement[..k] + 'ъ' + replacement[(k + 1)..];
					}
				} else {
					if(replacement[k] == 'Ï') {
						replacement = replacement[..k] + 'Ю' + replacement[(k + 1)..];
					} else if(replacement[k] == 'é') {
						replacement = replacement[..k] + 'э' + replacement[(k + 1)..];
					} else if(replacement[k] == 'ï') {
						replacement = replacement[..k] + 'ю' + replacement[(k + 1)..];
					} else if(replacement[k] == 'ä') {
						replacement = replacement[..k] + 'ь' + replacement[(k + 1)..];
					} else if(replacement[k] == 'ö') {
						replacement = replacement[..k] + 'я' + replacement[(k + 1)..];
					}
				}
			}
			if(MainWindow.Instance.UseBig5 && LoadedGame.Instance.FontSizeData.Valid) {
				var strStart = replacement.IndexOf("】“");
				var isSpoken = strStart != -1;
				strStart++;
				var sizeData = LoadedGame.Instance.FontSizeData;
				const int MAX_LINE_WIDTH = 560;
				int lineWidth = 0;
				for(int i = strStart; i < replacement.Length; i++) {
					if(isSpoken && replacement[i - 1] == '”' && replacement[i] == '%') {
						break;
					}
					if(replacement[i] == '%' && i + 1 < replacement.Length && (replacement[i + 1] == 'N' || replacement[i + 1] == 'P')) {
						lineWidth = 0;
						i += 2;
						continue;
					} else if(replacement[i] == '%' && i + 1 < replacement.Length && replacement[i + 1] == 'F') {
						i += 2;
						continue;
					} else if(replacement[i] == '%' && i + 2 < replacement.Length && replacement[i + 1] == 'O') {
						i += 2;
						while(i < replacement.Length && replacement[i] >= '0' && replacement[i] <= '9') {
							i++;
						}
						i--;
						continue; // i *hope* this works
					} else if(replacement[i] == '%' && i + 1 < replacement.Length && replacement[i + 1] != '%') {
						i++;
						continue;
					} else if(replacement[i] == '⑩' || replacement[i] == '　') { // hotfix for the 11b dual scene
						continue;
					}
					int idx;
					char c = replacement[i];
					if(c == '.') idx = 0x4;
					else if(c == ',') idx = 0x1;
					else if(c == ';') idx = 0x6;
					else if(c == ':') idx = 0x7;
					else if(c == '?') idx = 0x8;
					else if(c == '!') idx = 0x9;
					else if(c == '+') idx = 0x8F;
					else if(c == '-') idx = 0x90;
					else if(c == '"') idx = 0x1DA8;
					else if(c == '(') idx = 0x1D;
					else if(c == ')') idx = 0x1E;
					else if(c == '#') idx = 0x6D;
					else if(c == '&') idx = 0x6E;
					else if(c == '/') idx = 0xBE;
					else if(c == '/') idx = 0xBE;
					else if(c == ' ') idx = 0x0;
					else if(c >= '0' && c <= '9') idx = 0x12D + (c - '0');
					else if(c >= 'A' && c <= 'Z') idx = 0x14E + (c - 'A');
					else if(c >= 'a' && c <= 'z') idx = 0x168 + (c - 'a');
					else if(c >= 0x1 && c <= 0x8) idx = 0x1d2d + c;
					else if(c >= 0xb && c <= 0x19) idx = 0x1d2b + c;
					else if(c >= 0x1a && c <= 0x1d) idx = 101 + (c - 0x1a);
					else if(c == 0x1e) idx = 24;
					else if(c == 0x1f) idx = 11;
					else {
						ushort big5 = c == '@'? Big5.EncodeChar("＠", 0): Big5.EncodeChar(replacement, i);
						(int row, int column) = (big5 >> 8, big5 & 0xFF);
						if(row < 0xA1) {
							continue;
						}
						row -= 0xA1;
						column -= 0x40;
						idx = row * 191 + column;
					}
					var sizes = sizeData.GetWidthAndPadding(idx);
					if(sizes.Item1 == 255) {
						Console.WriteLine("invalid glyph index " + idx + " for char '" + char.ConvertFromUtf32(char.ConvertToUtf32(replacement, i)) + '\'');
						sizes = (0, 0);
					}
					lineWidth += sizes.Item1;
					if(lineWidth > MAX_LINE_WIDTH && replacement[i] != ' ') {
						for(int j = i - 1; j > strStart; j--) {
							if(replacement[j] == ' ' && replacement[j + 1] != '—') {
								replacement = replacement[..j] + "%N" + replacement[(j + 1)..];
								lineWidth = 0;
								i = j + 1; // go back to the start of the word
								goto lineBroken;
							} else if(replacement[j] == '%' && replacement[j + 1] != '%') {
								break;
							}
						}
					}
					lineWidth += sizes.Item2;
					lineBroken:
					if(char.IsSurrogate(replacement[i])) {
						i++;
					}
				}
			}
			return replacement;
		}
	}
}